using System.CommandLine;
using System.Globalization;
using System.Text;
using Ungrep.Bench;
using Ungrep.EditorLink;

namespace Ungrep.Cli;

/// <summary>
/// `ug bench`: question suites run on the code-graph arms and scored, and the overhead budgets
/// (specs/SPEC-bench.md). Exit codes: 0 every check held, 1 a budget broken or a regression
/// against the baseline, 2 a usage or suite error.
/// </summary>
internal static class BenchCommand
{
    private const string DefaultProbe = "Assets/UngrepBenchProbe.cs";

    // hostFactory: the shell, files and processes the runner works through; tests only.
    public static Command Create(string? claudeConfigDir, Func<string?, IBenchHost>? hostFactory = null)
    {
        hostFactory ??= extraPath => new BenchHost(extraPath);
        return new Command("bench", "Benchmark suites and overhead budgets (specs/SPEC-bench.md).")
        {
            Run(claudeConfigDir, hostFactory),
            BudgetsCommand(),
        };
    }

    private static Command Run(string? claudeConfigDir, Func<string?, IBenchHost> hostFactory)
    {
        var suiteArg = new Argument<string>("suite") { Description = "The suite file, e.g. bench/suites/purge-squad.json." };
        var mode = new Option<string>("--mode") { Description = "script (default): each arm's recorded calls. agent: claude -p per question, arm and repeat; spends usage.", DefaultValueFactory = _ => "script" };
        var arm = new Option<string>("--arm") { Description = "Only these arms, comma-separated." };
        var question = new Option<string>("--question") { Description = "Only these questions, comma-separated ids." };
        var project = new Option<string>("--project") { Description = "The Unity project. Default: the suite's project." };
        var output = new Option<string>("--out") { Description = "Results file. Default: bench/results/<suite>-<mode>-<date>.json beside the suite's folder." };
        var baseline = new Option<string>("--baseline") { Description = "A results file to compare with; a ungrep regression exits 1." };
        var answers = new Option<string>("--answers") { Description = "Folder for what each call answered (holds project code). Default: artifacts/bench/<suite>-<mode>-<date>." };
        var repeat = new Option<int>("--repeat") { Description = "Agent mode: runs per question and arm.", DefaultValueFactory = _ => 1 };
        var claude = new Option<string>("--claude") { Description = "Agent mode: the claude executable. Default: claude on PATH." };
        var model = new Option<string>("--model") { Description = "Agent mode: the model. Default: the CLI's own." };
        var maxUsd = new Option<double?>("--max-usd") { Description = "Agent mode: dollar cap per run (--max-budget-usd)." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Print what would run (agent mode: the run count and command lines); run nothing." };

        var command = new Command("run", "Run a suite on its arms, score it, check the budgets, compare with a baseline.")
        {
            suiteArg, mode, arm, question, project, output, baseline, answers, repeat, claude, model, maxUsd, dryRun,
        };

        command.SetAction(result =>
        {
            TextWriter stdout = result.InvocationConfiguration.Output;
            TextWriter stderr = result.InvocationConfiguration.Error;
            string suitePath = Path.GetFullPath(result.GetValue(suiteArg)!);
            string runMode = result.GetValue(mode)!;
            if (runMode is not ("script" or "agent"))
            {
                stderr.WriteLine("ug bench: --mode is script or agent, not " + runMode);
                return 2;
            }

            if (result.GetValue(repeat) < 1)
            {
                stderr.WriteLine("ug bench: --repeat is 1 or more");
                return 2;
            }

            Suite suite;
            try
            {
                suite = Suite.Parse(File.ReadAllText(suitePath));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
            {
                stderr.WriteLine($"ug bench: {suitePath}: {e.Message}");
                return 2;
            }

            string? projectPath = result.GetValue(project) ?? suite.Project;
            if (projectPath is null || !Directory.Exists(projectPath))
            {
                stderr.WriteLine(projectPath is null ? "ug bench: the suite names no project; pass --project" : "ug bench: no project folder " + projectPath);
                return 2;
            }

            projectPath = Path.GetFullPath(projectPath);
            string date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string? pluginRoot = PluginRoot();
            string suiteDir = Path.GetDirectoryName(suitePath)!;
            string? answersDir = result.GetValue(answers) ?? (pluginRoot is null ? null : Path.Combine(pluginRoot, "artifacts", "bench", $"{suite.Name}-{runMode}-{date}"));
            var options = new RunOptions(projectPath, List(result.GetValue(arm)), List(result.GetValue(question)), pluginRoot, suiteDir, answersDir, stderr);
            var agent = new AgentOptions(result.GetValue(claude) ?? "claude", result.GetValue(model), result.GetValue(maxUsd), result.GetValue(repeat), claudeConfigDir);
            IBenchHost host = hostFactory(pluginRoot is null ? null : Path.Combine(pluginRoot, "bin"));

            try
            {
                if (result.GetValue(dryRun))
                {
                    stdout.Write((runMode == "agent"
                        ? DryRun(suite, new AgentRunner(host).Plan(suite, options, agent), agent)
                        : DryRun(suite, options)).ReplaceLineEndings());
                    return 0;
                }

                BenchRun? before = null;
                if (result.GetValue(baseline) is { } baselinePath)
                {
                    try
                    {
                        before = BenchRun.FromJson(File.ReadAllText(baselinePath));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
                    {
                        stderr.WriteLine($"ug bench: can't read baseline {baselinePath}: {e.Message}");
                        return 2;
                    }
                }

                string? head = Head(host, projectPath);
                BenchRun run = runMode == "agent"
                    ? new AgentRunner(host).Run(suite, options, agent, date, head)
                    : new ScriptRunner(host).Run(suite, options, date, head);
                run = run with { Budgets = CheckBudgets(pluginRoot, projectPath, suite.GateProbe) };

                string outPath = result.GetValue(output) ?? DefaultOut(suiteDir, suite.Name, runMode, date);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
                File.WriteAllText(outPath, run.ToJson());

                Comparison? comparison = before is null ? null : Baseline.Compare(run, before);
                stdout.Write(BenchText.Render(run, comparison, result.GetValue(baseline), outPath).ReplaceLineEndings());
                return run.Budgets.Any(b => b.Ok == false) || comparison?.Regressed == true ? 1 : 0;
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // IOException: an arm's skill to copy is missing, or the results can't be written.
                stderr.WriteLine("ug bench: " + e.Message);
                return 2;
            }
        });

        return command;
    }

    private static Command BudgetsCommand()
    {
        var project = new Option<string>("--project") { Description = "The Unity project for the compile-gate check. Default: current folder." };
        var gate = new Option<bool>("--gate") { Description = "Also check the compile gate live: needs the open editor; writes and deletes a probe script." };
        var probe = new Option<string>("--probe") { Description = "The probe script's path in the project.", DefaultValueFactory = _ => DefaultProbe };
        var command = new Command("budgets", "Check the overhead budgets: skill description, skill body, compile gate.")
        {
            project, gate, probe,
        };

        command.SetAction(result =>
        {
            TextWriter stdout = result.InvocationConfiguration.Output;
            string projectPath = Path.GetFullPath(result.GetValue(project) ?? Directory.GetCurrentDirectory());
            IReadOnlyList<BudgetResult> budgets = CheckBudgets(PluginRoot(), projectPath, result.GetValue(gate) ? result.GetValue(probe) : null);
            foreach (BudgetResult b in budgets)
            {
                stdout.WriteLine($"{b.Name}  {b.Detail}{(b.Ok switch { true => "  ok", false => "  BROKEN", null => "" })}");
            }

            return budgets.Any(b => b.Ok == false) ? 1 : 0;
        });

        return command;
    }

    private static IReadOnlyList<BudgetResult> CheckBudgets(string? pluginRoot, string projectPath, string? probe)
    {
        var budgets = new List<BudgetResult>();
        if (pluginRoot is null)
        {
            budgets.Add(new BudgetResult(Budgets.Description, false, "no plugin root above " + AppContext.BaseDirectory));
            budgets.Add(new BudgetResult(Budgets.Body, false, "no plugin root above " + AppContext.BaseDirectory));
        }
        else
        {
            budgets.AddRange(Budgets.Skill(pluginRoot));
        }

        budgets.Add(probe is null
            ? Budgets.GateNotChecked("no --gate or suite gate probe")
            : Budgets.CheckGate(projectPath, probe, (root, deadline) => CompileGate.Check(root, deadline)));
        return budgets;
    }

    private static string DryRun(Suite suite, IReadOnlyList<AgentPlan> plan, AgentOptions agent)
    {
        int questions = plan.Select(p => p.Question.Id).Distinct().Count();
        int arms = plan.Select(p => p.Arm.Name).Distinct().Count();
        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
            $"bench {suite.Name} agent --dry-run: {plan.Count} runs ({questions} questions x {arms} arms x {agent.Repeat} repeats); nothing ran\n"));
        foreach (AgentPlan p in plan)
        {
            text.Append(string.Create(CultureInfo.InvariantCulture, $"{p.Question.Id} {p.Arm.Name} #{p.Repeat}: "))
                .Append(Quote(agent.Claude)).Append(' ').Append(string.Join(' ', p.Arguments.Select(Quote))).Append('\n');
        }

        return text.ToString();
    }

    // Script mode: each question's legs and their calls, as they would run; needs are not checked.
    private static string DryRun(Suite suite, RunOptions options)
    {
        IReadOnlyList<Arm> arms = ScriptRunner.Selected(suite, options.Arms);
        IReadOnlyList<Question> questions = ScriptRunner.Questions(suite, options.Questions);
        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
            $"bench {suite.Name} script --dry-run: {questions.Count} questions x {arms.Count} arms; nothing ran\n"));
        foreach (Question q in questions)
        {
            string needs = q.Needs.Count == 0 ? "" : " (needs " + string.Join(", ", q.Needs) + ")";
            text.Append(q.Id).Append(needs).Append('\n');
            foreach (var leg in arms.GroupBy(a => q.LegOf(a.Name)))
            {
                text.Append("  ").Append(string.Join(", ", leg.Select(a => a.Name + (a.Needs.Count == 0 ? "" : " (needs " + string.Join(", ", a.Needs) + ")")))).Append(": ");
                if (leg.Key is null)
                {
                    text.Append("no steps\n");
                    continue;
                }

                IReadOnlyList<Step> steps = q.Legs[leg.Key];
                text.Append(string.Create(CultureInfo.InvariantCulture, $"{steps.Count(s => s.Control.Counted)} counted"))
                    .Append(q.Before.Count + q.Check.Count + q.After.Count > 0
                        ? string.Create(CultureInfo.InvariantCulture, $", {q.Before.Count} before, {q.Check.Count} check, {q.After.Count} after")
                        : "")
                    .Append('\n');
                foreach (Step step in steps)
                {
                    text.Append("    ").Append(step.Control.Counted ? "" : "(not counted) ").Append(step.Label).Append('\n');
                }
            }
        }

        return text.ToString();
    }

    private static string Quote(string arg) =>
        arg.Length > 0 && arg.All(c => char.IsAsciiLetterOrDigit(c) || "-_./:,=@%+".Contains(c, StringComparison.Ordinal))
            ? arg
            : "'" + arg.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string? Head(IBenchHost host, string projectPath)
    {
        CallAnswer answer = host.Shell("git rev-parse --short HEAD", projectPath, TimeSpan.FromSeconds(30));
        return answer.Exit == 0 && answer.Text.Trim() is { Length: > 0 } head && !head.Contains('\n', StringComparison.Ordinal) ? head : null;
    }

    /// <summary>`bench/results/` when the suite sits in `bench/suites/`, else `results/` beside the suite.</summary>
    private static string DefaultOut(string suiteDir, string suite, string mode, string date)
    {
        string dir = Path.GetFileName(suiteDir) == "suites" ? Path.Combine(Path.GetDirectoryName(suiteDir)!, "results") : Path.Combine(suiteDir, "results");
        return Path.Combine(dir, $"{suite}-{mode}-{date}.json");
    }

    /// <summary>The plugin this ug belongs to: CLAUDE_PLUGIN_ROOT, else the first folder above ug holding `.claude-plugin/plugin.json`.</summary>
    private static string? PluginRoot()
    {
        if (Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_ROOT") is { Length: > 0 } root && Directory.Exists(root))
        {
            return Path.GetFullPath(root);
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, ".claude-plugin", "plugin.json")))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> List(string? value) =>
        value is null ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
