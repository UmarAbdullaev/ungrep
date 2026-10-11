using System.Globalization;

namespace Ungrep.Stats.Tests;

/// <summary>
/// T2 acceptance: the Evidence table in SPEC.md, reproduced from purge-squad's real transcripts.
/// The Evidence snapshot is every entry up to 2026-09-28T11:00Z (34 sessions, 754M context
/// tokens); later entries, including resumed old sessions, fall outside the window. Runs only on
/// a machine that has the transcripts: set UG_PURGE_SQUAD_TRANSCRIPTS to their folder.
/// </summary>
public sealed class PurgeSquadAcceptanceTests
{
    private const double EvidenceContextTokens = 754e6;

    [Fact]
    public void Matches_the_evidence_table()
    {
        string? dir = Environment.GetEnvironmentVariable("UG_PURGE_SQUAD_TRANSCRIPTS");
        if (string.IsNullOrEmpty(dir))
        {
            Assert.Skip("UG_PURGE_SQUAD_TRANSCRIPTS not set");
        }

        var window = new TimeWindow(Until: DateTimeOffset.Parse("2026-09-28T11:00:00Z", CultureInfo.InvariantCulture));
        UsageStats stats = TranscriptAnalyzer.AnalyzeDirectory(dir, window);

        Assert.Equal(34, stats.Sessions.Count);
        Assert.InRange(stats.ContextTokens, EvidenceContextTokens * 0.99, EvidenceContextTokens * 1.01);

        var evidence = new Dictionary<string, double>
        {
            ["unity: eval/run_script"] = 16.6,
            ["shell: search/read"] = 15.9,
            ["write: .cs"] = 10.2,
            ["shell: git"] = 6.5,
            ["read/search: unity yaml"] = 5.4,
            ["unity: compile/refresh/status"] = 5.3,
            ["unity: play/screenshot"] = 4.0,
            ["unity: console/logs"] = 3.7,
            ["unity: tests"] = 2.2,
            ["read: .cs"] = 2.1,
        };
        foreach ((string category, double percent) in evidence)
        {
            double actual = 100 * stats.Categories[category].ContextTokens / stats.ContextTokens;
            Assert.True(Math.Abs(actual - percent) <= 2, $"{category}: {actual:F1}% vs {percent}%");
        }
    }
}
