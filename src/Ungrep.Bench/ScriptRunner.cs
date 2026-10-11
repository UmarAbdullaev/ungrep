using System.Globalization;
using System.Text;

namespace Ungrep.Bench;

/// <summary>What a run covers and where it writes: shared by script and agent mode.</summary>
public sealed record RunOptions(
    string Project,
    IReadOnlyList<string> Arms,
    IReadOnlyList<string>? Questions,
    string? PluginRoot,
    string SuiteDir,
    string? AnswersDir,
    TextWriter Log);

/// <summary>
/// Script mode (SPEC-bench): per question and arm, the suite's recorded calls run for real, every
/// call counted; then the check steps (not counted) and the after steps. Steps an arm shares with
/// others (a named leg) run once and count for each of them.
/// </summary>
public sealed class ScriptRunner
{
    private readonly IBenchHost _host;
    private readonly Action<TimeSpan>? _sleep;

    // sleep: waits between polls; tests pass a no-op.
    public ScriptRunner(IBenchHost host, Action<TimeSpan>? sleep = null)
    {
        _host = host;
        _sleep = sleep;
    }

    public BenchRun Run(Suite suite, RunOptions options, string date, string? head)
    {
        var needs = new Needs(_host, suite, options.Project);
        var used = new Dictionary<string, HashSet<(string Server, string Tool)>>(StringComparer.Ordinal);
        var results = new List<QuestionResult>();
        IReadOnlyList<Arm> arms = Selected(suite, options.Arms);
        using var executor = new StepExecutor(_host, suite, options.Project, _sleep);

        foreach (Question question in Questions(suite, options.Questions))
        {
            var legs = new Dictionary<string, ArmResult>(StringComparer.Ordinal);
            var armResults = new List<ArmResult>();
            string? questionUnmet = needs.FirstUnmet(question.Needs);
            foreach (Arm arm in arms)
            {
                string? leg = question.LegOf(arm.Name);
                string? unmet = questionUnmet ?? needs.FirstUnmet(arm.Needs);
                ArmResult result;
                if (leg is null)
                {
                    result = new ArmResult(arm.Name, false, "no steps");
                }
                else if (unmet is not null)
                {
                    result = new ArmResult(arm.Name, false, "needs " + unmet);
                }
                else
                {
                    if (!legs.TryGetValue(leg, out ArmResult? shared))
                    {
                        shared = RunLeg(question, leg, executor, options);
                        legs[leg] = shared;
                    }

                    result = shared with { Arm = arm.Name, Leg = leg == arm.Name ? null : leg };
                    var tools = used.TryGetValue(arm.Name, out var set) ? set : used[arm.Name] = [];
                    foreach (McpStep step in question.Legs[leg].OfType<McpStep>())
                    {
                        tools.Add((step.Server, step.Tool));
                    }
                }

                armResults.Add(result);
            }

            results.Add(new QuestionResult(question.Id, question.Kind, armResults));
        }

        IReadOnlyList<IdleRow> idle = Idle(arms, used, executor, options);
        return new BenchRun(BenchRun.CurrentVersion, suite.Name, "script", date, suite.Commit, head, arms.Select(a => a.Name).ToList(), results, idle, []);
    }

    /// <summary>The arms named (in suite order), or all of them.</summary>
    /// <exception cref="ArgumentException">An arm the suite does not define.</exception>
    public static IReadOnlyList<Arm> Selected(Suite suite, IReadOnlyList<string> names)
    {
        foreach (string name in names.Where(n => suite.Arms.All(a => a.Name != n)))
        {
            throw new ArgumentException($"suite {suite.Name} has no arm {name} (arms: {string.Join(", ", suite.Arms.Select(a => a.Name))})");
        }

        return names.Count == 0 ? suite.Arms : suite.Arms.Where(a => names.Contains(a.Name)).ToList();
    }

    /// <exception cref="ArgumentException">A question the suite does not have.</exception>
    public static IReadOnlyList<Question> Questions(Suite suite, IReadOnlyList<string>? ids)
    {
        if (ids is null || ids.Count == 0)
        {
            return suite.Questions;
        }

        foreach (string id in ids.Where(id => suite.Questions.All(q => q.Id != id)))
        {
            throw new ArgumentException($"suite {suite.Name} has no question {id}");
        }

        return suite.Questions.Where(q => ids.Contains(q.Id)).ToList();
    }

    /// <summary>Runs steps that are not counted (before, check, after); returns what they answered.</summary>
    internal static string RunUncounted(IReadOnlyList<Step> steps, string phase, StepExecutor executor, Dictionary<string, string> values, StringBuilder? dump)
    {
        var text = new StringBuilder();
        foreach (Step step in steps)
        {
            foreach ((StepResult call, string answer) in executor.Execute(step, values))
            {
                text.Append(answer).Append('\n');
                Dump(dump, phase + " " + call.Label, call.Exit, answer);
            }
        }

        return text.ToString();
    }

    internal static void WriteAnswers(string? dir, string question, string leg, StringBuilder dump)
    {
        if (dir is null)
        {
            return;
        }

        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{question}-{leg}.txt"), dump.ToString());
    }

    private static ArmResult RunLeg(Question question, string leg, StepExecutor executor, RunOptions options)
    {
        var steps = new List<StepResult>();
        var answers = new StringBuilder();
        var dump = new StringBuilder();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        RunUncounted(question.Before, "before", executor, values, dump);
        foreach (Step step in question.Legs[leg])
        {
            foreach ((StepResult call, string answer) in executor.Execute(step, values))
            {
                if (step.Control.Counted)
                {
                    steps.Add(call);
                    answers.Append(answer).Append('\n');
                }

                Dump(dump, (step.Control.Counted ? "" : "(not counted) ") + call.Label, call.Exit, answer);
            }
        }

        string check = RunUncounted(question.Check, "check", executor, values, dump);
        RunUncounted(question.After, "after", executor, values, dump);
        executor.EndSession();
        IReadOnlyList<string> missing = Scoring.Missing(question.Key, answers.ToString(), check);
        WriteAnswers(options.AnswersDir, question.Id, leg, dump);

        int of = question.Key.Items.Count + question.Key.CheckItems.Count;
        var result = new ArmResult(leg, true, Found: of - missing.Count, Of: of, Missing: missing.Count == 0 ? null : missing, Steps: steps);
        options.Log.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{question.Id} {leg}: {result.Calls:0} calls, {result.Tokens:#,0} tokens, {result.Seconds:0.0} s, {result.Found}/{result.Of}"));
        return result;
    }

    private static void Dump(StringBuilder? dump, string label, int exit, string answer)
    {
        dump?.Append("$ ").Append(label).Append(exit == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"  (exit {exit})")).Append('\n')
            .Append(answer).Append(answer.EndsWith('\n') ? "" : "\n").Append('\n');
    }

    /// <summary>
    /// What each arm puts in context besides its calls: always, the skills' listing lines and the
    /// deferred names of every tool of each MCP server it used; on use, the skill bodies and the
    /// schemas of the MCP tools it called (O10's idle-cost rule).
    /// </summary>
    private static IReadOnlyList<IdleRow> Idle(IReadOnlyList<Arm> arms, Dictionary<string, HashSet<(string Server, string Tool)>> used, StepExecutor executor, RunOptions options)
    {
        var rows = new List<IdleRow>();
        foreach (Arm arm in arms)
        {
            int always = 0;
            int onUse = 0;
            var detail = new List<string>();
            foreach (string skillPath in arm.Skills)
            {
                try
                {
                    SkillFile skill = SkillFile.Read(SkillFile.Resolve(skillPath, options.PluginRoot, options.SuiteDir));
                    always += Encoding.UTF8.GetByteCount(skill.Listing);
                    onUse += Encoding.UTF8.GetByteCount(skill.Body);
                    detail.Add("skill " + Path.GetFileName(Path.GetDirectoryName(skill.Path)));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
                {
                    detail.Add($"skill {skillPath} unreadable: {e.Message}");
                }
            }

            if (used.TryGetValue(arm.Name, out var tools))
            {
                foreach (var server in tools.GroupBy(t => t.Server).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    if (executor.ToolsOf(server.Key, out string? error) is not { } list)
                    {
                        detail.Add($"mcp {server.Key} {error}");
                        continue;
                    }

                    always += list.Sum(t => Encoding.UTF8.GetByteCount($"mcp__{server.Key}__{t.Name}\n"));
                    var calledNames = server.Select(t => t.Tool).ToHashSet(StringComparer.Ordinal);
                    onUse += list.Where(t => calledNames.Contains(t.Name)).Sum(t => Encoding.UTF8.GetByteCount(t.Json));
                    detail.Add(string.Create(CultureInfo.InvariantCulture, $"mcp {server.Key} {list.Count} tools, {calledNames.Count} used"));
                }
            }

            rows.Add(new IdleRow(arm.Name, always, onUse, string.Join("; ", detail)));
        }

        return rows;
    }
}

/// <summary>A suite's named preconditions, each a shell command run once in the project; exit 0 means met.</summary>
internal sealed class Needs
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    private readonly IBenchHost _host;
    private readonly Suite _suite;
    private readonly string _project;
    private readonly Dictionary<string, bool> _met = new(StringComparer.Ordinal);

    public Needs(IBenchHost host, Suite suite, string project)
    {
        _host = host;
        _suite = suite;
        _project = project;
    }

    /// <summary>The first need not met, or null.</summary>
    public string? FirstUnmet(IEnumerable<string> names) => names.FirstOrDefault(n => !IsMet(n));

    private bool IsMet(string name)
    {
        if (!_met.TryGetValue(name, out bool met))
        {
            met = _host.Shell(_suite.Needs[name], _project, _timeout).Exit == 0;
            _met[name] = met;
        }

        return met;
    }
}
