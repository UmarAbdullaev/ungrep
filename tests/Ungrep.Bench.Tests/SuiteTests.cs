namespace Ungrep.Bench.Tests;

/// <summary>
/// Suite files (SPEC-bench, Suites): JSON with comments, arms, legs shared by name, before, check
/// and after steps, step controls; a bad suite fails with the place named. The committed
/// purge-squad suite must parse and give every question a ungrep leg.
/// </summary>
public sealed class SuiteTests
{
    internal const string Minimal = """
        // a comment
        {
          "suite": "tiny",
          "project": "D:/Game",
          "commit": "abc1234",
          "needs": { "editor": "ug status", "index": "test -d .index" },
          "servers": { "pipe": { "command": "pipe-mcp", "args": ["--stdio"] } },
          "arms": {
            "grep": { "agent": { "mcp": ["pipe"], "disallowedTools": ["Bash(ug:*)"] } },
            "other": { "needs": ["index"], "own": "(^|\\s)other\\s" },
            "ungrep": { "own": "(^|\\s)ug\\s", "skills": ["${plugin}/skills/unity/SKILL.md"], "agent": { "pluginDir": true } }
          },
          "agentPrompt": "Be brief.",
          "gate": "Assets/Probe.cs",
          "questions": [
            {
              "id": "Q1", "kind": "navigation", "prompt": "Who calls Foo?",
              "key": { "items": ["Bar\\.cs:12"], "not": ["Baz"], "checked": "by hand" },
              "steps": {
                "grep": [ { "run": "grep -rn Foo Assets" }, { "read": "Assets/Bar.cs", "offset": 10, "limit": 5 } ],
                "ungrep": [ { "run": "ug refs Foo", "timeout": 30 } ]
              }
            },
            {
              "id": "C1", "kind": "chore", "needs": ["editor"], "prompt": "Set it.",
              "before": [ { "run": "ug eval group", "capture": { "group": "result: (\\d+)" } } ],
              "check": [ { "run": "ug inspect X" } ],
              "after": [ { "run": "ug eval revert ${group}" } ],
              "key": { "check": ["value 1"], "checked": "by hand", },
              "steps": {
                "ungrep": [ { "run": "ug set X 1" } ],
                "pipe": [
                  { "mcp": "pipe", "tool": "set", "args": { "target": "X", "value": 1 } },
                  { "mcp": "pipe", "tool": "status", "until": "ready", "every": 2, "max": 3, "redo": "polls" },
                  { "run": "touch x", "count": false }
                ],
                "grep": "pipe", "other": "grep"
              }
            }
          ]
        }
        """;

    [Fact]
    public void Parses_arms_legs_aliases_and_step_controls()
    {
        Suite suite = Suite.Parse(Minimal);

        Assert.Equal("tiny", suite.Name);
        Assert.Equal(["grep", "other", "ungrep"], suite.Arms.Select(a => a.Name));
        Assert.Equal(["index"], suite.Arms[1].Needs);
        Assert.True(suite.Arms[2].Agent.PluginDir);
        Assert.Equal(["pipe"], suite.Arms[0].Agent.Mcp);
        Assert.Equal("pipe-mcp", suite.Servers["pipe"].Command);
        Assert.Equal(["--stdio"], suite.Servers["pipe"].Args);
        Assert.Equal("Assets/Probe.cs", suite.GateProbe);

        Question q1 = suite.Questions[0];
        Assert.Equal(["grep", "ungrep"], q1.Legs.Keys.Order(StringComparer.Ordinal));
        Assert.Null(q1.LegOf("other"));
        var read = Assert.IsType<ReadStep>(q1.Legs["grep"][1]);
        Assert.Equal((10, 5), (read.Offset!.Value, read.Limit!.Value));
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.IsType<RunStep>(q1.Legs["ungrep"][0]).Timeout);

        Question c1 = suite.Questions[1];
        Assert.Equal("pipe", c1.LegOf("grep"));
        Assert.Equal("pipe", c1.LegOf("other"));
        Assert.Equal(["editor"], c1.Needs);
        var set = Assert.IsType<McpStep>(c1.Legs["pipe"][0]);
        Assert.Equal("""{"target":"X","value":1}""", set.ArgumentsJson);
        StepControl poll = c1.Legs["pipe"][1].Control;
        Assert.Equal(("ready", TimeSpan.FromSeconds(2), 3, "polls"), (poll.Until!.ToString(), poll.Every, poll.MaxPolls, poll.Redo));
        Assert.False(c1.Legs["pipe"][2].Control.Counted);
        Assert.Equal("result: (\\d+)", c1.Before[0].Control.Capture["group"].ToString());
        Assert.Single(c1.Key.CheckItems);
    }

    [Theory]
    [InlineData("\"kind\": \"navigation\"", "\"kind\": \"trivia\"", "questions[0] (Q1).kind: navigation, asset or chore, not trivia")]
    [InlineData("\"grep\": \"pipe\", \"other\": \"grep\"", "\"grep\": \"other\", \"other\": \"grep\"", "questions[1] (C1).steps.grep: aliases go round in a circle")]
    [InlineData("\"grep\": \"pipe\", \"other\": \"grep\"", "\"grep\": \"nowhere\"", "questions[1] (C1).steps.grep: no steps named nowhere")]
    [InlineData("\"mcp\": \"pipe\", \"tool\": \"set\"", "\"mcp\": \"pump\", \"tool\": \"set\"", "questions[1] (C1).steps.pipe[0].mcp: no server pump")]
    [InlineData("\"items\": [\"Bar\\\\.cs:12\"]", "\"items\": [\"Bar(\"]", "questions[0] (Q1).key.items[0]: bad regular expression")]
    [InlineData("\"needs\": [\"editor\"]", "\"needs\": [\"gpu\"]", "questions[1] (C1).needs: gpu is not in the suite's needs")]
    [InlineData("\"check\": [ { \"run\": \"ug inspect X\" } ],", "", "questions[1] (C1).key.check: the question has no check steps")]
    [InlineData("\"every\": 2", "\"every\": 0", "questions[1] (C1).steps.pipe[1].every: a positive whole number")]
    [InlineData("{ \"run\": \"touch x\", \"count\": false }", "{ \"touch\": \"x\" }", "questions[1] (C1).steps.pipe[2]: a step is run, read or mcp")]
    public void A_bad_suite_names_the_place(string from, string to, string message)
    {
        Assert.Contains(from, Minimal, StringComparison.Ordinal);
        var e = Assert.Throws<FormatException>(() => Suite.Parse(Minimal.Replace(from, to, StringComparison.Ordinal)));
        Assert.StartsWith(message, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_question_id_twice_is_refused()
    {
        string twice = Minimal.Replace("\"id\": \"C1\"", "\"id\": \"Q1\"", StringComparison.Ordinal);
        Assert.Equal("questions: id Q1 twice", Assert.Throws<FormatException>(() => Suite.Parse(twice)).Message);
    }

    [Fact]
    public void Not_json_is_a_format_error()
    {
        Assert.StartsWith("not a JSON suite", Assert.Throws<FormatException>(() => Suite.Parse("{ nope")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_purge_squad_suite_parses_with_a_ungrep_leg_and_a_checked_key_for_every_question()
    {
        Suite suite = Suite.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "bench", "suites", "purge-squad.json")));

        Assert.Equal(["grep", "codegraph", "graphify", "ungrep"], suite.Arms.Select(a => a.Name));
        Assert.All(suite.Questions, q =>
        {
            Assert.NotNull(q.LegOf("ungrep"));
            Assert.StartsWith("20", q.Key.Checked, StringComparison.Ordinal);
        });
        Assert.Equal(["navigation", "asset", "chore"], suite.Questions.Select(q => q.Kind).Distinct());
    }

    [Theory]
    [InlineData("`C2_Central.asset` (the Swarm Central) never uses it, because its wave table is melee-only.", false)]
    [InlineData("The one node without it is `C2_Central`, the Swarm Central.", false)]
    [InlineData("`C2_Central.asset` is the only node that doesn't use it.", false)]
    [InlineData("C1_Central, C2_Central and C3_Central each spawn it. No other assets do.", true)]
    [InlineData("- `Assets/Data/Nodes/C2_Central.asset:68-265` (20)", true)]
    public void A5_takes_C2_Central_named_as_excluded_and_not_as_a_spawner(string answer, bool unwanted)
    {
        Suite suite = Suite.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "bench", "suites", "purge-squad.json")));

        Assert.Equal(unwanted, Scoring.Unwanted(suite.Questions.Single(q => q.Id == "A5").Key, answer).Count > 0);
    }

    internal static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ungrep.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("no ungrep.sln above " + AppContext.BaseDirectory);
    }
}
