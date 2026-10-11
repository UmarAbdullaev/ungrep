namespace Ungrep.Bench.Tests;

/// <summary>
/// Baseline comparison (SPEC-bench, Baseline): only the ungrep arm can fail the run, on a
/// question no longer correct, more calls, or tokens up by more than 10% and 50; other arms'
/// changes are printed, not failed.
/// </summary>
public sealed class BaselineTests
{
    private static ArmResult Arm(string arm, int calls, int tokens, bool correct = true) =>
        new(arm, true, Found: correct ? 2 : 1, Of: 2, Missing: correct ? null : ["Awake"],
            Steps: [.. Enumerable.Range(0, calls).Select(i => new StepResult("step " + i, tokens * 4 / calls, 0, 0.5, 0))]);

    private static BenchRun RunOf(string commit, params QuestionResult[] questions) =>
        new(BenchRun.CurrentVersion, "tiny", "script", "2026-10-11", commit, commit, ["grep", "ungrep"], questions, [], []);

    private static QuestionResult Q(string id, ArmResult grep, ArmResult ug) => new(id, "navigation", [grep, ug]);

    private static readonly BenchRun _before = RunOf("abc1234",
        Q("Q1", Arm("grep", 2, 400), Arm("ungrep", 1, 200)),
        Q("Q2", Arm("grep", 2, 400), Arm("ungrep", 1, 200)));

    [Fact]
    public void The_same_numbers_are_no_change()
    {
        Comparison same = Baseline.Compare(_before, _before);

        Assert.Equal(["no change"], same.Lines);
        Assert.False(same.Regressed);
    }

    [Fact]
    public void Ungrep_regresses_on_lost_correctness_more_calls_or_more_tokens()
    {
        BenchRun now = RunOf("abc1234",
            Q("Q1", Arm("grep", 2, 400), Arm("ungrep", 1, 200, correct: false)),
            Q("Q2", Arm("grep", 2, 400), Arm("ungrep", 2, 300)));

        Comparison c = Baseline.Compare(now, _before);

        Assert.True(c.Regressed);
        Assert.Equal(
            [
                "Q1 ungrep: correct in the baseline, not now (missing Awake)  REGRESSION",
                "Q2 ungrep: calls 1 -> 2; tokens 200 -> 300 (+50%)  REGRESSION",
            ],
            c.Lines);
    }

    [Fact]
    public void Small_token_changes_and_other_arms_do_not_fail()
    {
        BenchRun now = RunOf("def5678",
            Q("Q1", Arm("grep", 3, 900), Arm("ungrep", 1, 240)),
            Q("Q3", Arm("grep", 2, 400), Arm("ungrep", 1, 200)));

        Comparison c = Baseline.Compare(now, _before);

        Assert.False(c.Regressed);
        Assert.Equal(
            [
                "keys changed commit: baseline abc1234, now def5678",
                "Q1 grep: calls 2 -> 3; tokens 400 -> 900 (+125%)",
                "Q3: not in the baseline",
                "Q2: only in the baseline",
                "no regression",
            ],
            c.Lines);
    }

    [Fact]
    public void Fewer_calls_and_tokens_are_printed_as_gains()
    {
        BenchRun now = RunOf("abc1234",
            Q("Q1", Arm("grep", 2, 400), Arm("ungrep", 1, 100)),
            Q("Q2", Arm("grep", 2, 400), Arm("ungrep", 1, 200)));

        Comparison c = Baseline.Compare(now, _before);

        Assert.Equal(["Q1 ungrep: tokens 200 -> 100 (-50%)", "no regression"], c.Lines);
        Assert.False(c.Regressed);
    }

    [Fact]
    public void Another_suite_or_mode_is_not_compared()
    {
        Comparison c = Baseline.Compare(_before with { Mode = "agent" }, _before);

        Assert.Equal(["baseline is tiny script, this run tiny agent: not compared"], c.Lines);
        Assert.False(c.Regressed);
    }

    [Fact]
    public void The_report_names_the_baseline_and_its_verdict()
    {
        BenchRun now = RunOf("abc1234",
            Q("Q1", Arm("grep", 2, 400), Arm("ungrep", 2, 200)),
            Q("Q2", Arm("grep", 2, 400), Arm("ungrep", 1, 200)));

        Golden.Match(BenchText.Render(now, Baseline.Compare(now, _before), "bench/baselines/tiny-script.json", "bench/results/tiny-script-2026-10-11.json"));
    }

    [Fact]
    public void A_results_file_of_another_version_is_refused()
    {
        string json = (_before with { Version = 9 }).ToJson();

        Assert.Equal("results version 9 is not supported (expected 1)", Assert.Throws<FormatException>(() => BenchRun.FromJson(json)).Message);
        Assert.StartsWith("not a ug bench results file", Assert.Throws<FormatException>(() => BenchRun.FromJson("[]")).Message, StringComparison.Ordinal);
    }
}
