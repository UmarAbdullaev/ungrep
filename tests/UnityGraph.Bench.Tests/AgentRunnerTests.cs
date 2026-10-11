using UnityGraph.Stats;

namespace UnityGraph.Bench.Tests;

/// <summary>
/// Agent mode on a fake host (SPEC-bench, Agent mode): each run's isolated `claude -p` command
/// line, before and check steps around it, and the score read from its JSON answer and its own
/// transcript (context the way `ug stats` counts it, tool calls, redo, leaks).
/// </summary>
public sealed class AgentRunnerTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "ug-bench-agent-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHost _host = new();
    private readonly Suite _suite = Suite.Parse(SuiteTests.Minimal);
    private int _ids;

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    private static string Turn(string id, long context, string contentJson) =>
        "{\"isSidechain\":false,\"type\":\"assistant\",\"message\":{\"id\":\"" + id + "\",\"role\":\"assistant\",\"content\":[" + contentJson
        + "],\"usage\":{\"input_tokens\":0,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":" + context.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ",\"output_tokens\":1}},\"timestamp\":\"2026-10-11T08:00:00.000Z\"}";

    private static readonly string[] _transcript =
    [
        """{"type":"user","message":{"role":"user","content":"Who calls Foo?"},"timestamp":"2026-10-11T08:00:00.000Z"}""",
        Turn("m1", 20_000, """{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ug refs Foo"}}"""),
        Turn("m2", 21_000, """{"type":"tool_use","id":"t2","name":"Grep","input":{"pattern":"Foo"}}"""),
        Turn("m3", 22_000, """{"type":"text","text":"Bar.cs:12 in Awake"}"""),
    ];

    private const string Output = """{"type":"result","subtype":"success","is_error":false,"duration_ms":12345,"result":"Foo is called from Bar.cs:12 in Awake.","total_cost_usd":0.04213}""";

    private static AgentOptions Agent(int repeat = 1) => new("claude", "claude-sonnet-5-5", 0.5, repeat, null);

    private RunOptions Options(params string[] arms) =>
        new("D:/Game", arms, null, "D:/plugin", _temp, Path.Combine(_temp, "answers"), TextWriter.Null);

    [Fact]
    public void Each_arm_runs_isolated_with_its_own_servers_plugin_and_deny_rules()
    {
        IReadOnlyList<string> grep = AgentRunner.Arguments(_suite, _suite.Arms[0], _suite.Questions[0], "s-1", Agent(), "D:/plugin");
        IReadOnlyList<string> ug = AgentRunner.Arguments(_suite, _suite.Arms[2], _suite.Questions[0], "s-2", Agent(), "D:/plugin");

        Assert.Equal(
            [
                "-p", "Who calls Foo?\n\nBe brief.",
                "--output-format", "json",
                "--session-id", "s-1",
                "--setting-sources", "project,local",
                "--strict-mcp-config",
                "--mcp-config", """{"mcpServers":{"pipe":{"type":"stdio","command":"pipe-mcp","args":["--stdio"]}}}""",
                "--allowedTools", "Read,Grep,Glob,Bash,Skill,mcp__pipe",
                "--disallowedTools", "Bash(ug:*)",
                "--model", "claude-sonnet-5-5",
                "--max-budget-usd", "0.5",
            ],
            grep);
        Assert.Equal(["--plugin-dir", "D:/plugin", "--allowedTools", "Read,Grep,Glob,Bash,Skill"], ug.Skip(9).Take(4));
        Assert.DoesNotContain("--mcp-config", ug);
    }

    [Fact]
    public void The_plan_is_every_question_arm_and_repeat()
    {
        IReadOnlyList<AgentPlan> plan = new AgentRunner(_host, () => "s-" + ++_ids).Plan(_suite, Options("grep", "unitygraph"), Agent(repeat: 2));

        Assert.Equal(8, plan.Count);
        Assert.Equal(("Q1", "grep", 1, "s-1"), (plan[0].Question.Id, plan[0].Arm.Name, plan[0].Repeat, plan[0].SessionId));
        Assert.Equal(("C1", "unitygraph", 2, "s-8"), (plan[7].Question.Id, plan[7].Arm.Name, plan[7].Repeat, plan[7].SessionId));
        Assert.Empty(_host.Calls);
    }

    [Fact]
    public void Scores_a_run_from_its_answer_and_its_transcript()
    {
        AgentRun run = AgentRunner.Score(_suite.Questions[0], _suite.Arms[2], _suite.Arms, "s-1", "warming up\n" + Output, _transcript, null, null, 99);

        Assert.Equal((3, 2, 63_000L, 0L), (run.Turns, run.ToolCalls, run.ContextTokens, run.SubagentTokens));
        Assert.Equal((0.0421, 12.3), (run.Usd, run.Seconds)); // seconds from duration_ms, not the wall clock given
        Assert.Equal((true, true, false, null), (run.Correct, run.Redo, run.Leaked, run.Error));
        Assert.Empty(run.Missing);
    }

    [Fact]
    public void A_run_without_a_json_answer_is_an_error_and_wrong()
    {
        AgentRun run = AgentRunner.Score(_suite.Questions[0], _suite.Arms[0], _suite.Arms, "s-1", "Error: no auth", [], null, null, 3);

        Assert.Equal(("no JSON result from claude", false, 3.0), (run.Error, run.Correct, run.Seconds));
        Assert.Equal(["Bar\\.cs:12"], run.Missing);
    }

    [Fact]
    public void An_unwanted_item_in_the_answer_makes_it_wrong()
    {
        string output = Output.Replace("in Awake.", "in Awake, and Baz.", StringComparison.Ordinal);
        AgentRun run = AgentRunner.Score(_suite.Questions[0], _suite.Arms[2], _suite.Arms, "s-1", output, _transcript, null, null, 1);

        Assert.False(run.Correct);
        Assert.Equal(["Baz"], run.Unwanted);
    }

    [Fact]
    public void Runs_before_and_check_steps_around_claude_and_scores_the_chore_by_its_check()
    {
        string config = Path.Combine(_temp, "claude");
        string transcripts = ClaudeProjects.TranscriptDirectory("D:/Game", config);
        _host.OnShell = command => new CallAnswer(command == "ug inspect X" ? "X  value 1\n" : "ran snippet\n  result: 7\n", 0, TimeSpan.Zero);
        _host.OnExec = (_, args) =>
        {
            string id = args[args.ToList().IndexOf("--session-id") + 1];
            Directory.CreateDirectory(transcripts);
            File.WriteAllLines(Path.Combine(transcripts, id + ".jsonl"), _transcript);
            return (0, Output, "", TimeSpan.FromSeconds(13));
        };
        var agent = new AgentOptions("claude", null, null, 1, config);
        var options = Options("unitygraph") with { Questions = ["C1"] };

        BenchRun run = new AgentRunner(_host, () => "s-" + ++_ids, _ => { }).Run(_suite, options, agent, "2026-10-11", "def5678");

        Assert.Equal(["shell ug status", "shell ug eval group", "exec claude", "shell ug inspect X", "shell ug eval revert 7"], _host.Calls);
        AgentRun only = Assert.Single(run.Questions[0].Arms[0].Runs!);
        Assert.Equal((true, 63_000L, 0.0421), (only.Correct, only.ContextTokens, only.Usd));
        Assert.Equal(("agent", null), (run.Mode, run.Model));
        string dump = File.ReadAllText(Path.Combine(_temp, "answers", "C1-unitygraph-1.txt"));
        Assert.StartsWith("session s-1\n\nFoo is called from Bar.cs:12 in Awake.\n\n$ before ug eval group\n", dump, StringComparison.Ordinal);
    }
}
