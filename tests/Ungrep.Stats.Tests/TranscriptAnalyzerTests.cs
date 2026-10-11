using System.Globalization;

namespace Ungrep.Stats.Tests;

/// <summary>
/// Turn counting, context attribution and compaction detection on small recorded transcripts.
/// </summary>
public sealed class TranscriptAnalyzerTests
{
    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static DateTimeOffset Utc(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture);

    private static SessionStats Analyze(string fixture, TimeWindow? window = null) =>
        TranscriptAnalyzer.AnalyzeSession(fixture, File.ReadLines(Path.Combine(FixtureDir, fixture)), window ?? TimeWindow.All)
        ?? throw new InvalidOperationException("no turns");

    [Fact]
    public void Counts_each_message_once_and_skips_sidechain_and_broken_lines()
    {
        SessionStats session = Analyze("attribution.jsonl");

        Assert.Equal([10_010L, 12_000L, 14_004L], session.TurnContext);
        Assert.Equal(36_014L, session.ContextTokens);
        Assert.Empty(session.CompactionTurns);
    }

    [Fact]
    public void Charges_each_turn_to_the_calls_whose_results_it_consumed()
    {
        SessionStats session = Analyze("attribution.jsonl");

        Assert.Equal(
            new Dictionary<string, CategoryTotals>
            {
                ["read: .cs"] = new(1, 7_002),
                ["search: grep/glob"] = new(1, 12_000),
                ["shell: git"] = new(1, 7_002),
            },
            session.Categories);
    }

    [Fact]
    public void A_drop_of_more_than_40_percent_is_a_compaction()
    {
        SessionStats session = Analyze("compaction.jsonl");

        Assert.Equal([100_000L, 150_000L, 50_000L, 55_000L, 40_000L], session.TurnContext);
        Assert.Equal([2], session.CompactionTurns);
    }

    [Fact]
    public void Window_keeps_entries_inside_the_bounds()
    {
        var window = new TimeWindow(Since: Utc("2026-09-21T00:00:00Z"), Until: Utc("2026-09-23T12:00:00Z"));

        SessionStats session = Analyze("compaction.jsonl", window);

        Assert.Equal([150_000L, 50_000L, 55_000L], session.TurnContext);
    }

    [Fact]
    public void Session_without_turns_in_the_window_is_null()
    {
        var window = new TimeWindow(Since: Utc("2030-01-01T00:00:00Z"));

        Assert.Null(TranscriptAnalyzer.AnalyzeSession("x", File.ReadLines(Path.Combine(FixtureDir, "compaction.jsonl")), window));
    }

    [Fact]
    public void Directory_totals_sum_the_sessions()
    {
        UsageStats stats = TranscriptAnalyzer.AnalyzeDirectory(FixtureDir, TimeWindow.All);

        Assert.Equal(["attribution", "compaction"], stats.Sessions.Select(s => s.Id));
        Assert.Equal(8, stats.Turns);
        Assert.Equal(36_014L + 395_000L, stats.ContextTokens);
        Assert.Equal(1, stats.Compactions);
        Assert.Equal(100_000L, stats.FirstTurnMedian);
        Assert.Equal(3, stats.Categories.Values.Sum(c => c.Calls));
    }

    [Fact]
    public void Missing_directory_gives_empty_stats()
    {
        UsageStats stats = TranscriptAnalyzer.AnalyzeDirectory(Path.Combine(FixtureDir, "no-such-dir"), TimeWindow.All);

        Assert.Empty(stats.Sessions);
        Assert.Equal(0L, stats.ContextTokens);
    }
}
