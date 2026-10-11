using System.Text.RegularExpressions;

namespace Ungrep.Bench.Tests;

/// <summary>
/// Scoring (SPEC-bench): key items against answers and check output, unwanted items, and from a
/// transcript the main thread's tool calls, redo (a text search after the arm's own first call)
/// and leaks (another arm's tool used).
/// </summary>
public sealed class ScoringTests
{
    private static readonly AnswerKey _key = new(
        [new Regex("Bar\\.cs:12"), new Regex("\\bAwake\\b")],
        [new Regex("value 1")],
        [new Regex("Baz")],
        "by hand");

    private static readonly Regex _ug = new("(^|[\\s;&|(])ug\\s|Skill\\(ungrep");

    private static Arm ArmOf(string name, Regex? own) => new(name, [], [], own, new AgentArm(false, [], [], null));

    internal static string Assistant(string id, string tool, string inputJson, bool sidechain = false) =>
        "{\"type\":\"assistant\",\"isSidechain\":" + (sidechain ? "true" : "false")
        + ",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\"" + id + "\",\"name\":\"" + tool + "\",\"input\":" + inputJson + "}]}}";

    [Fact]
    public void Missing_lists_items_not_in_the_answers_and_check_items_not_in_the_check()
    {
        Assert.Empty(Scoring.Missing(_key, "Bar.cs:12 in Awake", "value 1"));
        Assert.Equal(["\\bAwake\\b", "check: value 1"], Scoring.Missing(_key, "Bar.cs:12", "value 0"));
    }

    [Fact]
    public void Unwanted_lists_not_items_the_answer_holds()
    {
        Assert.Equal(["Baz"], Scoring.Unwanted(_key, "Bar.cs:12 and Baz"));
        Assert.Empty(Scoring.Unwanted(_key, "Bar.cs:12"));
    }

    [Fact]
    public void Tool_uses_are_the_main_threads_signed_by_command_or_skill_once_each()
    {
        string[] lines =
        [
            """{"type":"user","message":{"role":"user","content":"Who calls Foo?"}}""",
            Assistant("t1", "Bash", """{"command":"ug refs Foo"}"""),
            Assistant("t1", "Bash", """{"command":"ug refs Foo"}"""), // the same message split over two lines
            Assistant("t2", "Skill", """{"skill":"ungrep:unity"}"""),
            Assistant("t3", "Grep", """{"pattern":"Foo"}""", sidechain: true),
            Assistant("t4", "mcp__codegraph__codegraph_explore", """{"query":"ug refs"}"""),
            "not json",
            "",
        ];

        Assert.Equal(
            [new ToolUse("Bash", "ug refs Foo"), new ToolUse("Skill", "Skill(ungrep:unity)"), new ToolUse("mcp__codegraph__codegraph_explore", "mcp__codegraph__codegraph_explore")],
            Scoring.ToolUses(lines));
    }

    [Fact]
    public void A_text_search_after_the_arms_first_own_call_is_a_redo()
    {
        ToolUse ug = new("Bash", "ug refs Foo");
        ToolUse grep = new("Bash", "cd Assets && grep -rn Foo .");
        ToolUse glob = new("Glob", "Glob");

        Assert.True(Scoring.IsRedo([ug, grep], _ug));
        Assert.True(Scoring.IsRedo([ug, glob], _ug));
        Assert.False(Scoring.IsRedo([grep, ug], _ug)); // searched before the own tool: not a redo
        Assert.False(Scoring.IsRedo([ug, new ToolUse("Bash", "ug refs Foo | grep Bar")], _ug)); // its own call, piped
        Assert.False(Scoring.IsRedo([grep, grep], null)); // the grep arm's own call is the search
    }

    [Fact]
    public void Using_another_arms_tool_is_a_leak()
    {
        Arm grep = ArmOf("grep", null);
        Arm ungrep = ArmOf("ungrep", _ug);
        Arm codegraph = ArmOf("codegraph", new Regex("mcp__codegraph__|(^|\\s)codegraph\\s"));
        Arm[] arms = [grep, codegraph, ungrep];

        Assert.True(Scoring.IsLeaked([new ToolUse("Bash", "ug refs Foo")], grep, arms));
        Assert.True(Scoring.IsLeaked([new ToolUse("mcp__codegraph__codegraph_explore", "mcp__codegraph__codegraph_explore")], ungrep, arms));
        Assert.False(Scoring.IsLeaked([new ToolUse("Bash", "ug refs Foo"), new ToolUse("Grep", "Grep")], ungrep, arms));
    }

    [Theory]
    [InlineData("Bash", "grep -rn Foo Assets", true)]
    [InlineData("Bash", "rg Foo", true)]
    [InlineData("PowerShell", "Get-ChildItem | Select-String Foo", true)]
    [InlineData("Bash", "ug refs Foo", false)]
    [InlineData("Bash", "echo grepped", false)]
    [InlineData("Read", "Read", false)]
    public void Text_searches_are_grep_glob_and_shell_search_commands(string tool, string signature, bool search)
    {
        Assert.Equal(search, Scoring.IsTextSearch(new ToolUse(tool, signature)));
    }
}
