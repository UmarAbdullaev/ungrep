namespace UnityGraph.Stats.Tests;

/// <summary>
/// Subagents counted from their own transcripts: a session's `subagents/agent-&lt;id&gt;.jsonl`,
/// typed by the `.meta.json` beside each. The fixture session ran five: two general-purpose (one
/// with a line Claude Code wrote twice, one resumed), an Explore, one with no meta file and one
/// typed `unitygraph:unity-worker`.
/// </summary>
public sealed class SubagentsTests : IDisposable
{
    private static string SessionDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "subagents");

    private static string SubagentDir => Path.Combine(SessionDir, "session", "subagents");

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "ug-subagents-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    private static TimeWindow Window(string? since = null, string? until = null) =>
        new(since is null ? null : DateTimeOffset.Parse(since, System.Globalization.CultureInfo.InvariantCulture),
            until is null ? null : DateTimeOffset.Parse(until, System.Globalization.CultureInfo.InvariantCulture));

    private static StatsReport Report(string directory) =>
        StatsReport.From("Game", TimeWindow.All, TranscriptAnalyzer.AnalyzeDirectory(directory, TimeWindow.All));

    [Fact]
    public void Sums_what_each_agent_type_read_in_its_own_transcripts()
    {
        Assert.Equal(
            [
                new SubagentRow("general-purpose", 2, 108_000),
                new SubagentRow("Explore", 1, 50_000),
                new SubagentRow(TranscriptAnalyzer.UnknownAgentType, 1, 31_000),
                new SubagentRow("unitygraph:unity-worker", 1, 18_000),
            ],
            TranscriptAnalyzer.AnalyzeSubagents(SubagentDir, TimeWindow.All));
    }

    [Fact]
    public void Counts_only_turns_inside_the_window()
    {
        // Until 08:00:10: the resumed run's last turn and the two later runs fall outside.
        Assert.Equal(
            [new SubagentRow("Explore", 1, 50_000), new SubagentRow("general-purpose", 2, 78_000)],
            TranscriptAnalyzer.AnalyzeSubagents(SubagentDir, Window(until: "2026-10-11T08:00:10Z")).OrderBy(r => r.Type, StringComparer.Ordinal));

        Assert.Equal(
            [new SubagentRow(TranscriptAnalyzer.UnknownAgentType, 1, 31_000), new SubagentRow("unitygraph:unity-worker", 1, 18_000)],
            TranscriptAnalyzer.AnalyzeSubagents(SubagentDir, Window(since: "2026-10-11T08:00:13Z")));
    }

    [Fact]
    public void Subagents_stay_out_of_the_main_threads_figures()
    {
        UsageStats stats = TranscriptAnalyzer.AnalyzeDirectory(SessionDir, TimeWindow.All);

        Assert.Single(stats.Sessions);
        Assert.Equal(2, stats.Turns);
        Assert.Equal(162_000, stats.ContextTokens);
        Assert.Equal(5, stats.Subagents.Sum(r => r.Runs));
        Assert.Equal(207_000, stats.Subagents.Sum(r => r.ContextTokens));
    }

    [Fact]
    public void A_missing_or_unreadable_meta_file_types_the_run_unknown()
    {
        Directory.CreateDirectory(_temp);
        string turn = """{"isSidechain":true,"type":"assistant","message":{"id":"x1","role":"assistant","content":[],"usage":{"input_tokens":1000}},"timestamp":"2026-10-11T08:00:00Z"}""";
        foreach (string id in new[] { "a", "b", "c" })
        {
            File.WriteAllText(Path.Combine(_temp, "agent-" + id + ".jsonl"), turn + "\n");
        }

        File.WriteAllText(Path.Combine(_temp, "agent-a.meta.json"), "not json");
        File.WriteAllText(Path.Combine(_temp, "agent-b.meta.json"), """{"agentType":""}""");
        File.WriteAllText(Path.Combine(_temp, "notes.jsonl"), turn + "\n");

        Assert.Equal([new SubagentRow(TranscriptAnalyzer.UnknownAgentType, 3, 3_000)], TranscriptAnalyzer.AnalyzeSubagents(_temp, TimeWindow.All));
    }

    [Fact]
    public void No_subagent_folder_is_no_subagents()
    {
        Assert.Empty(TranscriptAnalyzer.AnalyzeSubagents(Path.Combine(_temp, "missing"), TimeWindow.All));
        Assert.Empty(TranscriptAnalyzer.AnalyzeDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures"), TimeWindow.All).Subagents);
    }

    [Fact]
    public void Text_report_with_subagents() => Golden.Match(StatsText.Render(Report(SessionDir)));

    [Fact]
    public void No_subagent_line_without_subagents()
    {
        string text = StatsText.Render(Report(Path.Combine(AppContext.BaseDirectory, "Fixtures")));

        Assert.DoesNotContain("subagents", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_baseline_written_before_subagents_were_counted_gives_no_deltas()
    {
        StatsReport old = StatsReport.FromJson("""
            {"version":1,"project":"Game","sessions":1,"turns":2,"contextTokens":40000,"compactions":0,"firstTurnMedian":20000,"categories":[],
             "worker":{"delegations":5,"short":1,"overBudget":1,"noResult":1,"verdictTokens":1192,"contextTokens":157000}}
            """);

        string text = StatsText.Render(Report(SessionDir), old);

        Assert.Null(old.Subagents);
        Assert.Contains(
            "\nsubagents 5 runs | context 207.0k | main + subagents 369.0k | general-purpose 2 runs 108.0k, Explore 1 run 50.0k, unknown 1 run 31.0k, unitygraph:unity-worker 1 run 18.0k\n",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Baseline_subagents_show_even_when_there_are_none_now()
    {
        StatsReport now = Report(Path.Combine(AppContext.BaseDirectory, "Fixtures"));

        string text = StatsText.Render(now, Report(SessionDir));

        Assert.Contains("\nsubagents 0 runs (-5) | context 0 (-100.0%) | main + subagents ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Subagent_rows_round_trip_through_json()
    {
        StatsReport report = Report(SessionDir);

        StatsReport back = StatsReport.FromJson(report.ToJson());

        Assert.Equal(report.Subagents, back.Subagents);
        Assert.Contains("\"subagents\": [\n    {\n      \"type\": \"general-purpose\",\n      \"runs\": 2,\n      \"contextTokens\": 108000\n", report.ToJson(), StringComparison.Ordinal);
    }
}
