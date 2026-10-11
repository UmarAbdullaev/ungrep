using Ungrep.EditorLink;

namespace Ungrep.Bench.Tests;

/// <summary>
/// The overhead budgets (SPEC.md Overhead budget): the plugin skill's listing line and body, and
/// the compile gate driven through its own Stop hook against a stand-in for the editor: silent
/// when clean, at most 20 error lines on a 30-error probe, clean again once the probe is gone.
/// </summary>
public sealed class BudgetsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-bench-budget-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _checks = [];

    public BudgetsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "Scripts"));
        Directory.CreateDirectory(Path.Combine(_root, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_root, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.3.14f1\n");
        File.WriteAllText(Path.Combine(_root, "Assets", "Scripts", "Player.cs"), "class Player { }\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string Errors(int count, int shown) =>
        $"compiled in 2.0 s: {count} errors\n"
        + string.Concat(Enumerable.Range(1, shown).Select(i => $"  Assets/Probe.cs:{i}:9  CS0029  Cannot convert\n"))
        + (count > shown ? $"  (+{count - shown} more errors: ug compile --all)\n" : "");

    // The editor as the gate sees it: errors while the probe exists, clean otherwise.
    private GateAnswer Editor(string root, int shown = 20)
    {
        bool probe = File.Exists(Path.Combine(root, "Assets", "UngrepBenchProbe.cs"));
        _checks.Add(probe ? "errors" : "clean");
        return probe ? new GateAnswer(GateResult.Errors, Errors(Budgets.ProbeErrors, shown)) : new GateAnswer(GateResult.Clean);
    }

    [Fact]
    public void The_gate_is_silent_when_clean_and_caps_errors_at_twenty_lines()
    {
        BudgetResult result = Budgets.CheckGate(_root, "Assets/UngrepBenchProbe.cs", (r, _) => Editor(r));

        Assert.Equal(new BudgetResult(Budgets.Gate, true, "0 bytes clean, 20/20 error lines"), result);
        Assert.Equal(["clean", "clean", "errors", "clean"], _checks);
        Assert.False(File.Exists(Path.Combine(_root, "Assets", "UngrepBenchProbe.cs")));
    }

    [Fact]
    public void Over_twenty_error_lines_breaks_the_budget()
    {
        BudgetResult result = Budgets.CheckGate(_root, "Assets/UngrepBenchProbe.cs", (r, _) => Editor(r, shown: 30));

        Assert.Equal((false, "blocked with 30 error lines, over 20"), (result.Ok, result.Detail));
    }

    [Fact]
    public void The_last_check_waits_out_the_reload_after_the_probe_is_deleted()
    {
        int unreachable = 2;
        var waits = new List<TimeSpan>();
        BudgetResult result = Budgets.CheckGate(_root, "Assets/UngrepBenchProbe.cs",
            (r, _) => _checks.Count >= 3 && !File.Exists(Path.Combine(r, "Assets", "UngrepBenchProbe.cs")) && unreachable-- > 0
                ? new GateAnswer(GateResult.Unreachable)
                : Editor(r),
            sleep: waits.Add);

        Assert.True(result.Ok);
        Assert.Equal(2, waits.Count);
    }

    [Theory]
    [InlineData(GateResult.Unreachable, "not checked: the editor did not answer")]
    [InlineData(GateResult.Playing, "not checked: the editor is playing")]
    [InlineData(GateResult.Errors, "not checked: the project has compile errors already")]
    public void An_editor_that_cannot_be_checked_leaves_the_gate_unchecked(GateResult state, string detail)
    {
        BudgetResult result = Budgets.CheckGate(_root, "Assets/UngrepBenchProbe.cs", (_, _) => new GateAnswer(state, "x"));

        Assert.Equal(new BudgetResult(Budgets.Gate, null, detail), result);
    }

    [Theory]
    [InlineData("Probe.cs", "the probe must be a .cs file under Assets/: Probe.cs")]
    [InlineData("Assets/Probe.txt", "the probe must be a .cs file under Assets/: Assets/Probe.txt")]
    [InlineData("Assets/Scripts/Player.cs", "the probe exists already; delete it first: Assets/Scripts/Player.cs")]
    public void A_probe_outside_assets_or_already_there_is_refused(string probe, string detail)
    {
        BudgetResult result = Budgets.CheckGate(_root, probe, (r, _) => Editor(r));

        Assert.Equal((false, detail), (result.Ok, result.Detail));
        Assert.Empty(_checks);
    }

    [Fact]
    public void The_probe_holds_more_errors_than_the_cap()
    {
        Assert.Equal(Budgets.ProbeErrors, Budgets.Probe().Split('\n').Count(l => l.Contains("= \"x\";", StringComparison.Ordinal)));
        Assert.Equal(3, Budgets.ErrorLines(Errors(5, 3)));
    }

    [Fact]
    public void The_skill_budgets_count_the_listing_line_and_the_body()
    {
        string plugin = Path.Combine(_root, "plugin");
        Directory.CreateDirectory(Path.Combine(plugin, "skills", "unity"));
        Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin"));
        File.WriteAllText(Path.Combine(plugin, ".claude-plugin", "plugin.json"), """{ "name": "ungrep" }""");
        File.WriteAllText(Path.Combine(plugin, "skills", "unity", "SKILL.md"), "---\nname: unity\ndescription: >\n  Unity answers\n  in one line.\n---\n" + new string('x', 2401));

        IReadOnlyList<BudgetResult> results = Budgets.Skill(plugin);

        Assert.Equal(new BudgetResult(Budgets.Description, true, "11/300"), results[0]); // "- ungrep:unity: Unity answers in one line.\n"
        Assert.Equal(new BudgetResult(Budgets.Body, false, "601/600"), results[1]);
    }

    [Fact]
    public void A_missing_skill_breaks_both_skill_budgets()
    {
        Assert.All(Budgets.Skill(Path.Combine(_root, "nowhere")), r => Assert.False(r.Ok));
    }
}
