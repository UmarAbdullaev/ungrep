using System.Globalization;

namespace Ungrep.Stats.Tests;

/// <summary>
/// Golden tests for the `ug stats` text and JSON reports, and the baseline round trip.
/// </summary>
public sealed class StatsReportTests
{
    private static readonly string _fixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static StatsReport Report(TimeWindow window) =>
        StatsReport.From("Game", window, TranscriptAnalyzer.AnalyzeDirectory(_fixtureDir, window));

    private static TimeWindow Until(string iso) =>
        new(Until: DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture));

    [Fact]
    public void Text_report() => Golden.Match(StatsText.Render(Report(TimeWindow.All)));

    [Fact]
    public void Text_report_against_a_baseline() =>
        Golden.Match(StatsText.Render(Report(TimeWindow.All), baseline: Report(Until("2026-09-21T00:00:00Z"))));

    [Fact]
    public void Text_report_with_no_sessions() =>
        Golden.Match(StatsText.Render(Report(Until("2000-01-01T00:00:00Z"))));

    [Fact]
    public void Json_report() => Golden.Match(Report(Until("2026-09-23T12:00:00Z")).ToJson(), "json");

    [Fact]
    public void Json_round_trips()
    {
        StatsReport report = Report(TimeWindow.All);

        StatsReport back = StatsReport.FromJson(report.ToJson());

        Assert.Equal(report.ToJson(), back.ToJson());
        Assert.Equal(report.Categories, back.Categories);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"version":1}""")]
    [InlineData("""{"version":99,"project":"Game","categories":[]}""")]
    public void Rejects_what_is_not_a_report(string json)
    {
        Assert.Throws<FormatException>(() => StatsReport.FromJson(json));
    }

    [Fact]
    public void Text_stays_within_budget_however_many_categories()
    {
        var categories = Enumerable.Range(0, 100).Select(i => new CategoryRow($"other: Tool{i:D3}", 1, 1000 - i)).ToList();
        var report = new StatsReport(StatsReport.CurrentVersion, "Game", null, null, 1, 1, 100_000, 0, 100_000, categories);

        string[] lines = StatsText.Render(report, report).TrimEnd('\n').Split('\n');

        Assert.True(lines.Length <= 30, $"{lines.Length} lines");
        Assert.Equal("other (80 categories)", lines[^2][..21]);
        Assert.Equal("  (the smallest, summed; ug stats --json lists each)", lines[^1]);
    }
}
