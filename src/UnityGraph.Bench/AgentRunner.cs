using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityGraph.Stats;

namespace UnityGraph.Bench;

/// <summary>Agent-mode settings: which `claude`, model, dollar cap per run, repeats, where transcripts are.</summary>
public sealed record AgentOptions(string Claude, string? Model, double? MaxUsd, int Repeat, string? ClaudeConfigDir)
{
    /// <summary>One run's wall-clock limit.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(20);
}

/// <summary>One planned `claude -p` run.</summary>
public sealed record AgentPlan(Question Question, Arm Arm, int Repeat, string SessionId, IReadOnlyList<string> Arguments);

/// <summary>
/// Agent mode (SPEC-bench): one isolated `claude -p` per question, arm and repeat, scored from its
/// own transcript with `TranscriptAnalyzer`, the numbers `ug stats` prints.
/// </summary>
public sealed class AgentRunner
{
    /// <summary>Tools every arm may use without a prompt; an arm adds its MCP servers.</summary>
    public static readonly IReadOnlyList<string> BaseTools = ["Read", "Grep", "Glob", "Bash", "Skill"];

    private readonly IBenchHost _host;
    private readonly Func<string> _newSessionId;
    private readonly Action<TimeSpan>? _sleep;

    // newSessionId, sleep: tests pass fixed ids and a no-op wait between polls.
    public AgentRunner(IBenchHost host, Func<string>? newSessionId = null, Action<TimeSpan>? sleep = null)
    {
        _host = host;
        _newSessionId = newSessionId ?? (() => Guid.NewGuid().ToString());
        _sleep = sleep;
    }

    /// <summary>Every run, in order, with its command line; nothing runs.</summary>
    public IReadOnlyList<AgentPlan> Plan(Suite suite, RunOptions options, AgentOptions agent)
    {
        var plan = new List<AgentPlan>();
        foreach (Question question in ScriptRunner.Questions(suite, options.Questions))
        {
            foreach (Arm arm in ScriptRunner.Selected(suite, options.Arms))
            {
                for (int i = 1; i <= agent.Repeat; i++)
                {
                    string id = _newSessionId();
                    plan.Add(new AgentPlan(question, arm, i, id, Arguments(suite, arm, question, id, agent, options.PluginRoot)));
                }
            }
        }

        return plan;
    }

    /// <summary>The `claude` arguments of one run (SPEC-bench, Agent mode: isolation).</summary>
    public static IReadOnlyList<string> Arguments(Suite suite, Arm arm, Question question, string sessionId, AgentOptions agent, string? pluginRoot)
    {
        string prompt = suite.AgentPrompt is null ? question.Prompt : question.Prompt + "\n\n" + suite.AgentPrompt;
        var args = new List<string>
        {
            "-p", prompt,
            "--output-format", "json",
            "--session-id", sessionId,
            "--setting-sources", "project,local",
            "--strict-mcp-config",
        };

        if (arm.Agent.Mcp.Count > 0)
        {
            var servers = new JsonObject();
            foreach (string name in arm.Agent.Mcp)
            {
                McpServer server = suite.Servers[name];
                servers[name] = new JsonObject
                {
                    ["type"] = "stdio",
                    ["command"] = server.Command,
                    ["args"] = new JsonArray(server.Args.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
                };
            }

            args.Add("--mcp-config");
            args.Add(new JsonObject { ["mcpServers"] = servers }.ToJsonString());
        }

        if (arm.Agent.PluginDir && pluginRoot is not null)
        {
            args.Add("--plugin-dir");
            args.Add(pluginRoot);
        }

        args.Add("--allowedTools");
        args.Add(string.Join(",", BaseTools.Concat(arm.Agent.Mcp.Select(s => "mcp__" + s))));
        if (arm.Agent.DisallowedTools.Count > 0)
        {
            args.Add("--disallowedTools");
            args.Add(string.Join(",", arm.Agent.DisallowedTools));
        }

        if (arm.Agent.AppendSystemPrompt is { } append)
        {
            args.Add("--append-system-prompt");
            args.Add(append);
        }

        if (agent.Model is { } model)
        {
            args.Add("--model");
            args.Add(model);
        }

        if (agent.MaxUsd is { } usd)
        {
            args.Add("--max-budget-usd");
            args.Add(usd.ToString("0.##", CultureInfo.InvariantCulture));
        }

        return args;
    }

    public BenchRun Run(Suite suite, RunOptions options, AgentOptions agent, string date, string? head)
    {
        IReadOnlyList<Arm> arms = ScriptRunner.Selected(suite, options.Arms);
        IReadOnlyList<AgentPlan> plan = Plan(suite, options, agent);
        var needs = new Needs(_host, suite, options.Project);
        string transcripts = ClaudeProjects.TranscriptDirectory(options.Project, agent.ClaudeConfigDir);
        var results = new List<QuestionResult>();
        using var executor = new StepExecutor(_host, suite, options.Project, _sleep);

        foreach (var byQuestion in plan.GroupBy(p => p.Question))
        {
            Question question = byQuestion.Key;
            var armResults = new List<ArmResult>();
            foreach (var byArm in byQuestion.GroupBy(p => p.Arm))
            {
                Arm arm = byArm.Key;
                if ((needs.FirstUnmet(question.Needs) ?? needs.FirstUnmet(arm.Needs)) is { } unmet)
                {
                    armResults.Add(new ArmResult(arm.Name, false, "needs " + unmet));
                    continue;
                }

                var runs = byArm.Select(p => RunOne(suite, p, executor, transcripts, agent, options)).ToList();
                armResults.Add(new ArmResult(arm.Name, true, Runs: runs));
            }

            results.Add(new QuestionResult(question.Id, question.Kind, armResults));
        }

        return new BenchRun(BenchRun.CurrentVersion, suite.Name, "agent", date, suite.Commit, head, arms.Select(a => a.Name).ToList(), results, [], [], agent.Model);
    }

    /// <summary>Scores one finished run from its `--output-format json` answer and its transcript.</summary>
    internal static AgentRun Score(Question question, Arm arm, IEnumerable<Arm> arms, string sessionId, string output, IReadOnlyList<string> transcript, string? subagentDir, string? check, double seconds)
    {
        string answer = "";
        double usd = 0;
        string? error = null;
        if (LastJson(output) is JsonObject json)
        {
            answer = json["result"]?.GetValue<string>() ?? "";
            usd = json["total_cost_usd"]?.GetValue<double>() ?? 0;
            if (json["is_error"]?.GetValue<bool>() == true)
            {
                error = json["subtype"]?.GetValue<string>() ?? "error";
            }

            if (json["duration_ms"]?.GetValue<double>() is { } ms)
            {
                seconds = ms / 1000;
            }
        }
        else
        {
            error = "no JSON result from claude";
        }

        SessionStats? stats = TranscriptAnalyzer.AnalyzeSession(sessionId, transcript, TimeWindow.All);
        IReadOnlyList<ToolUse> uses = Scoring.ToolUses(transcript);
        long subagents = subagentDir is null ? 0 : TranscriptAnalyzer.AnalyzeSubagents(subagentDir, TimeWindow.All).Sum(r => r.ContextTokens);
        if (stats is null && error is null)
        {
            error = "no transcript";
        }

        IReadOnlyList<string> missing = Scoring.Missing(question.Key, answer, check ?? "");
        IReadOnlyList<string> unwanted = Scoring.Unwanted(question.Key, answer);
        return new AgentRun(
            sessionId,
            stats?.TurnContext.Count ?? 0,
            uses.Count,
            stats?.ContextTokens ?? 0,
            subagents,
            Math.Round(usd, 4),
            Math.Round(seconds, 1),
            error is null && missing.Count == 0 && unwanted.Count == 0,
            missing,
            unwanted,
            Scoring.IsRedo(uses, arm.Own),
            Scoring.IsLeaked(uses, arm, arms),
            error);
    }

    private AgentRun RunOne(Suite suite, AgentPlan p, StepExecutor executor, string transcripts, AgentOptions agent, RunOptions options)
    {
        var dump = new StringBuilder();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        ScriptRunner.RunUncounted(p.Question.Before, "before", executor, values, dump);
        var (exit, stdout, stderr, elapsed) = _host.Exec(agent.Claude, p.Arguments, options.Project, agent.Timeout, stdin: "");
        string check = ScriptRunner.RunUncounted(p.Question.Check, "check", executor, values, dump);
        ScriptRunner.RunUncounted(p.Question.After, "after", executor, values, dump);
        string file = Path.Combine(transcripts, p.SessionId + ".jsonl");
        IReadOnlyList<string> lines = File.Exists(file) ? File.ReadAllLines(file) : [];
        AgentRun run = Score(p.Question, p.Arm, suite.Arms, p.SessionId, stdout, lines, Path.Combine(transcripts, p.SessionId, "subagents"), check, elapsed.TotalSeconds);
        if (run.Error is null && exit != 0)
        {
            run = run with { Error = "claude exited " + exit.ToString(CultureInfo.InvariantCulture) + ": " + FirstLine(stderr) };
        }

        string answer = LastJson(stdout)?["result"]?.GetValue<string>() ?? stdout;
        dump.Insert(0, "session " + p.SessionId + "\n\n" + answer + "\n\n");
        ScriptRunner.WriteAnswers(options.AnswersDir, p.Question.Id, Invariant($"{p.Arm.Name}-{p.Repeat}"), dump);
        options.Log.WriteLine(Invariant(
            $"{p.Question.Id} {p.Arm.Name} #{p.Repeat}: {run.ToolCalls} calls, {run.ContextTokens:#,0} context, {run.SubagentTokens:#,0} subagents, ${run.Usd:0.00}, {run.Seconds:0} s, {(run.Correct ? "ok" : "wrong")}{(run.Redo ? ", redo" : "")}{(run.Leaked ? ", leaked" : "")}{(run.Error is null ? "" : ", " + run.Error)}"));
        return run;
    }

    private static JsonNode? LastJson(string output)
    {
        foreach (string line in output.Split('\n').Reverse())
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith('{'))
            {
                try
                {
                    return JsonNode.Parse(trimmed);
                }
                catch (JsonException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    private static string FirstLine(string text) => text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
