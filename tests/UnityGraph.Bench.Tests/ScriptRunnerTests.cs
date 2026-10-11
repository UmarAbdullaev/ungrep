namespace UnityGraph.Bench.Tests;

/// <summary>
/// Script mode on a fake host (SPEC-bench, Script mode): per question and arm, before steps, the
/// counted leg, check and after steps, in that order; a leg shared by several arms runs once;
/// unmet needs skip an arm; polls repeat until their answer matches; captured values fill later
/// steps; the idle cost comes from skills and the MCP tools an arm used.
/// </summary>
public sealed class ScriptRunnerTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "ug-bench-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHost _host = new();
    private int _statusPolls;

    public ScriptRunnerTests()
    {
        string skill = Path.Combine(_temp, "plugin", "skills", "unity");
        Directory.CreateDirectory(skill);
        Directory.CreateDirectory(Path.Combine(_temp, "plugin", ".claude-plugin"));
        File.WriteAllText(Path.Combine(_temp, "plugin", ".claude-plugin", "plugin.json"), """{ "name": "unitygraph" }""");
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), "---\nname: unity\ndescription: Unity answers.\n---\nUse ug.\n");

        _host.OnShell = command => command switch
        {
            "ug status" => new CallAnswer("idle\n", 0, TimeSpan.FromMilliseconds(100)),
            "test -d .index" => new CallAnswer("", 1, TimeSpan.FromMilliseconds(10)),
            "grep -rn Foo Assets" => new CallAnswer("Assets/Bar.cs:12:    Foo();\n", 0, TimeSpan.FromMilliseconds(200)),
            "ug refs Foo" => new CallAnswer("Foo  method  Assets/Bar.cs:12  Awake\n", 0, TimeSpan.FromMilliseconds(300)),
            "ug eval group" => new CallAnswer("ran snippet in 1 ms\n  result: 41\n", 0, TimeSpan.FromMilliseconds(100)),
            "ug inspect X" => new CallAnswer("X  value 1\n", 0, TimeSpan.FromMilliseconds(100)),
            _ => new CallAnswer("ran " + command + "\n", 0, TimeSpan.FromMilliseconds(100)),
        };
        _host.Files["Bar.cs"] = string.Concat(Enumerable.Range(1, 20).Select(i => i == 12 ? "    void Awake() => Foo();\n" : $"line {i}\n"));
        _host.Servers["pipe-mcp"] = new FakeMcp(
            [("set", """{"name":"set","inputSchema":{}}"""), ("status", """{"name":"status","inputSchema":{}}"""), ("unused", """{"name":"unused"}""")],
            (tool, _) => tool == "status" ? (++_statusPolls < 2 ? "busy" : "ready") : "ok",
            _host.Calls);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    private RunOptions Options(params string[] arms) =>
        new("D:/Game", arms, null, Path.Combine(_temp, "plugin"), _temp, Path.Combine(_temp, "answers"), TextWriter.Null);

    private BenchRun Run(Suite? suite = null, params string[] arms) =>
        new ScriptRunner(_host, _ => { }).Run(suite ?? Suite.Parse(SuiteTests.Minimal), Options(arms), "2026-10-11", "def5678");

    [Fact]
    public void Runs_before_leg_check_after_in_order_and_a_shared_leg_once()
    {
        BenchRun run = Run();

        int from = _host.Calls.IndexOf("shell ug eval group");
        Assert.Equal(
            [
                "shell ug eval group",
                "open pipe-mcp",
                """mcp set {"target":"X","value":1}""",
                "mcp status {}",
                "mcp status {}",
                "shell touch x",
                "shell ug inspect X",
                "shell ug eval revert 41",
                "shell ug eval group",
                "shell ug set X 1",
                "shell ug inspect X",
                "shell ug eval revert 41",
            ],
            _host.Calls.Skip(from).Take(12));
        Assert.Single(_host.Calls, c => c == "shell ug status");
        Assert.Single(_host.Calls, c => c == "open pipe-mcp");

        ArmResult[] c1 = [.. run.Questions[1].Arms];
        Assert.Equal(("grep", "pipe", 3, 1, 1), (c1[0].Arm, c1[0].Leg, c1[0].Steps!.Count, c1[0].Found, c1[0].Of));
        Assert.Equal(2, c1[0].Redo);
        Assert.Equal((false, "needs index"), (c1[1].Ran, c1[1].Reason));
        Assert.Equal(("unitygraph", null, 1), (c1[2].Arm, c1[2].Leg, c1[2].Steps!.Count));
        Assert.Equal((false, "no steps"), (run.Questions[0].Arms[1].Ran, run.Questions[0].Arms[1].Reason));
    }

    [Fact]
    public void Scores_answers_and_counts_bytes_the_way_claude_sees_them()
    {
        BenchRun run = Run();

        ArmResult grep = run.Questions[0].Arms[0];
        Assert.Equal((1, 1), (grep.Found, grep.Of));
        Assert.Null(grep.Missing);
        StepResult read = grep.Steps![1];
        Assert.Equal("read Assets/Bar.cs:10+5", read.Label);
        Assert.Equal(StepExecutor.ReadLikeClaude(_host.Files["Bar.cs"], 10, 5).Length, read.AnswerBytes);

        ArmResult ug = run.Questions[0].Arms[2];
        int answer = "Foo  method  Assets/Bar.cs:12  Awake\n".Length;
        Assert.Equal(("ug refs Foo", "ug refs Foo".Length, answer, 0.3), (ug.Steps![0].Label, ug.Steps[0].RequestBytes, ug.Steps[0].AnswerBytes, ug.Steps[0].Seconds));
        Assert.Equal(("ug refs Foo".Length + answer) / 4.0, ug.Tokens);
    }

    [Fact]
    public void The_idle_cost_counts_skills_and_every_tool_name_of_a_used_server()
    {
        BenchRun run = Run();

        IdleRow grep = run.Idle[0];
        Assert.Equal("mcp__pipe__set\nmcp__pipe__status\nmcp__pipe__unused\n".Length, grep.AlwaysBytes);
        Assert.Equal("""{"name":"set","inputSchema":{}}{"name":"status","inputSchema":{}}""".Length, grep.OnUseBytes);
        Assert.Equal("mcp pipe 3 tools, 2 used", grep.Detail);

        IdleRow ug = run.Idle[2];
        Assert.Equal(("- unitygraph:unity: Unity answers.\n".Length, "Use ug.\n".Length, "skill unity"), (ug.AlwaysBytes, ug.OnUseBytes, ug.Detail));
    }

    [Fact]
    public void A_poll_stops_at_its_maximum()
    {
        _statusPolls = int.MinValue; // never ready
        Run(null, "grep");

        Assert.Equal(3, _host.Calls.Count(c => c == "mcp status {}"));
    }

    [Fact]
    public void A_server_that_does_not_start_answers_every_call_with_the_error()
    {
        _host.Servers.Clear();
        BenchRun run = Run(null, "grep");

        ArmResult c1 = run.Questions[1].Arms[0];
        Assert.All(c1.Steps!, s => Assert.Equal(1, s.Exit));
        Assert.Equal(4, c1.Steps!.Count); // set, then status polled to its maximum of 3
        Assert.Equal("mcp pipe did not start", run.Idle[0].Detail);
        Assert.Single(_host.Calls, c => c == "open pipe-mcp");
    }

    [Fact]
    public void Each_leg_starts_its_servers_fresh_and_a_server_that_failed_is_not_tried_again()
    {
        // A server may remember what it sent in a session (codegraph does); a real session answers one question.
        Suite suite = Suite.Parse(SuiteTests.Minimal.Replace(
            "\"questions\": [",
            "\"questions\": [ { \"id\": \"Q0\", \"kind\": \"navigation\", \"prompt\": \"Set?\", \"key\": { \"items\": [\"ok\"], \"checked\": \"by hand\" }, \"steps\": { \"grep\": [ { \"mcp\": \"pipe\", \"tool\": \"set\", \"args\": {} } ] } },",
            StringComparison.Ordinal));

        BenchRun run = Run(suite, "grep");
        Assert.Equal(2, _host.Calls.Count(c => c == "open pipe-mcp"));
        Assert.Equal("mcp pipe 3 tools, 2 used", run.Idle[0].Detail);

        _host.Calls.Clear();
        _host.Servers.Clear();
        run = Run(suite, "grep");
        Assert.Single(_host.Calls, c => c == "open pipe-mcp");
        Assert.Equal("mcp pipe did not start", run.Idle[0].Detail);
    }

    [Fact]
    public void Answers_are_dumped_per_leg_with_uncounted_steps_marked()
    {
        Run();

        string dump = File.ReadAllText(Path.Combine(_temp, "answers", "C1-pipe.txt"));
        Assert.Contains("$ before ug eval group\nran snippet in 1 ms\n  result: 41\n", dump, StringComparison.Ordinal);
        Assert.Contains("$ (not counted) touch x\n", dump, StringComparison.Ordinal);
        Assert.Contains("$ after ug eval revert ${group}\nran ug eval revert 41\n", dump, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_temp, "answers", "Q1-unitygraph.txt")));
    }

    [Fact]
    public void Unknown_arms_and_questions_are_refused()
    {
        Suite suite = Suite.Parse(SuiteTests.Minimal);

        Assert.Equal("suite tiny has no arm nope (arms: grep, other, unitygraph)", Assert.Throws<ArgumentException>(() => ScriptRunner.Selected(suite, ["nope"])).Message);
        Assert.Equal("suite tiny has no question Q9", Assert.Throws<ArgumentException>(() => ScriptRunner.Questions(suite, ["Q9"])).Message);
        Assert.Equal(["unitygraph"], ScriptRunner.Selected(suite, ["unitygraph"]).Select(a => a.Name));
    }

    [Fact]
    public void The_report_shows_cells_reasons_totals_and_idle_cost()
    {
        // A read's request holds its absolute path, which differs per machine.
        Suite suite = Suite.Parse(SuiteTests.Minimal.Replace(
            """{ "read": "Assets/Bar.cs", "offset": 10, "limit": 5 }""", """{ "run": "sed -n 10,14p Assets/Bar.cs" }""", StringComparison.Ordinal));

        Golden.Match(BenchText.Render(Run(suite)));
    }

    [Fact]
    public void Results_read_back_as_written()
    {
        BenchRun run = Run();

        string json = run.ToJson();
        Assert.Equal(json, BenchRun.FromJson(json).ToJson());
        Assert.DoesNotContain(_temp.Replace('\\', '/'), json.Replace("\\\\", "/", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("a\nb\n", null, null, "     1\ta\n     2\tb\n")]
    [InlineData("a\nb\nc", 2, 1, "     2\tb\n")]
    [InlineData("a\n", 5, null, "<system-reminder>Warning: the file exists but is shorter than the provided offset (5). The file has 1 lines.</system-reminder>")]
    [InlineData("", null, null, "<system-reminder>Warning: the file exists but the contents are empty.</system-reminder>")]
    public void Reads_number_lines_like_the_read_tool(string text, int? offset, int? limit, string expected)
    {
        Assert.Equal(expected, StepExecutor.ReadLikeClaude(text, offset, limit));
    }

    [Fact]
    public void A_read_cuts_long_lines_and_stops_at_two_thousand()
    {
        string text = new string('x', 2500) + "\n" + string.Concat(Enumerable.Repeat("y\n", 2500));
        string[] lines = StepExecutor.ReadLikeClaude(text, null, null).TrimEnd('\n').Split('\n');

        Assert.Equal(StepExecutor.ReadLines, lines.Length);
        Assert.Equal(6 + 1 + StepExecutor.ReadLineChars, lines[0].Length);
    }

    [Fact]
    public void Unknown_captures_stay_as_written()
    {
        Assert.Equal("revert 41 ${other}", StepExecutor.Fill("revert ${group} ${other}", new Dictionary<string, string> { ["group"] = "41" }));
    }
}
