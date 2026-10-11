using System.Text.Encodings.Web;
using System.Text.Json;

namespace Ungrep.Stats;

/// <summary>
/// Context tokens and tool calls charged to one category.
/// </summary>
public sealed record CategoryRow(string Name, int Calls, long ContextTokens);

/// <summary>
/// Subagent runs of one agent type and the context they read in their own transcripts: what they
/// cost on top of the main thread.
/// </summary>
public sealed record SubagentRow(string Type, int Runs, long ContextTokens)
{
    /// <summary>One row per type, most context first.</summary>
    public static IReadOnlyList<SubagentRow> Merge(IEnumerable<SubagentRow> rows) =>
        rows.GroupBy(r => r.Type, StringComparer.Ordinal)
            .Select(g => new SubagentRow(g.Key, g.Sum(r => r.Runs), g.Sum(r => r.ContextTokens)))
            .OrderByDescending(r => r.ContextTokens)
            .ThenBy(r => r.Type, StringComparer.Ordinal)
            .ToList();
}

/// <summary>
/// What `ug stats --json` writes and `--baseline` reads back. Aggregates only: no paths, session
/// ids, prompts or code, so a report can be kept in a repository or shared. `Subagents` is null in
/// a report written before subagents were counted.
/// </summary>
public sealed record StatsReport(
    int Version,
    string Project,
    DateTimeOffset? Since,
    DateTimeOffset? Until,
    int Sessions,
    int Turns,
    long ContextTokens,
    int Compactions,
    long FirstTurnMedian,
    IReadOnlyList<CategoryRow> Categories,
    IReadOnlyList<SubagentRow>? Subagents = null)
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // Category names hold '+' and '/'; the report is read by people and tools, not a browser.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public int ToolCalls => Categories.Sum(c => c.Calls);

    public long ChargedContextTokens => Categories.Sum(c => c.ContextTokens);

    public static StatsReport From(string project, TimeWindow window, UsageStats stats)
    {
        var categories = stats.Categories
            .Select(c => new CategoryRow(c.Key, c.Value.Calls, (long)Math.Round(c.Value.ContextTokens)))
            .OrderByDescending(c => c.ContextTokens)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        return new StatsReport(
            CurrentVersion,
            project,
            window.Since?.ToUniversalTime(),
            window.Until?.ToUniversalTime(),
            stats.Sessions.Count,
            stats.Turns,
            stats.ContextTokens,
            stats.Compactions,
            stats.FirstTurnMedian,
            categories,
            stats.Subagents);
    }

    /// <summary>Indented JSON with '\n' line endings on every OS.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, _jsonOptions).ReplaceLineEndings("\n");

    /// <exception cref="FormatException">Not a stats report, or a version this build can't read.</exception>
    public static StatsReport FromJson(string json)
    {
        StatsReport? report;
        try
        {
            report = JsonSerializer.Deserialize<StatsReport>(json, _jsonOptions);
        }
        catch (JsonException e)
        {
            throw new FormatException("not a ug stats --json report: " + e.Message, e);
        }

        if (report is null || report.Categories is null || report.Project is null)
        {
            throw new FormatException("not a ug stats --json report");
        }

        if (report.Version != CurrentVersion)
        {
            throw new FormatException($"report version {report.Version} is not supported (expected {CurrentVersion})");
        }

        return report;
    }
}
