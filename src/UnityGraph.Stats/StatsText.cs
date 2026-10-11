using System.Globalization;
using System.Text;

namespace UnityGraph.Stats;

/// <summary>
/// Plain-text `ug stats` report, written for Claude and people alike: a summary line, subagents
/// when there were any, then where the context went by category, with deltas when a baseline
/// report is given. Stays under 30 lines whatever the transcripts hold.
/// </summary>
public static class StatsText
{
    public const int MaxCategoryRows = 20;

    private static readonly CultureInfo _inv = CultureInfo.InvariantCulture;

    public static string Render(StatsReport report, StatsReport? baseline = null)
    {
        var text = new StringBuilder();
        text.Append(report.Project).Append("  ").Append(Window(report));
        if (baseline is not null)
        {
            text.Append("  vs baseline ").Append(baseline.Project).Append("  ").Append(Window(baseline));
        }

        text.Append('\n');

        if (report.Sessions == 0)
        {
            text.Append("no sessions in this window\n");
            return text.ToString();
        }

        long avg = report.ContextTokens / report.Turns;
        text.Append(Invariant($"sessions {report.Sessions}{CountDelta(report.Sessions, baseline?.Sessions)}"))
            .Append(Invariant($" | turns {report.Turns:N0}{PercentDelta(report.Turns, baseline?.Turns)}"))
            .Append(Invariant($" | context {Tokens(report.ContextTokens)}{PercentDelta(report.ContextTokens, baseline?.ContextTokens)}"))
            .Append(Invariant($" | avg/turn {Tokens(avg)}{PercentDelta(avg, baseline is { Turns: > 0 } b ? b.ContextTokens / b.Turns : (long?)null)}"))
            .Append('\n');
        text.Append(Invariant($"first turn median {Tokens(report.FirstTurnMedian)}{PercentDelta(report.FirstTurnMedian, baseline?.FirstTurnMedian)}"))
            .Append(Invariant($" | compactions {report.Compactions}{CountDelta(report.Compactions, baseline?.Compactions)}"))
            .Append(Invariant($" | tool calls {report.ToolCalls:N0}{PercentDelta(report.ToolCalls, baseline?.ToolCalls)}"))
            .Append('\n');
        text.Append(Invariant($"context charged to tool calls {Tokens(report.ChargedContextTokens)} ({Share(report.ChargedContextTokens, report.ContextTokens)})"))
            .Append('\n');
        AppendSubagents(text, report, baseline);
        text.Append('\n');

        AppendCategories(text, report, baseline);
        return text.ToString();
    }

    /// <summary>
    /// One line, only when this report or its baseline ran subagents: what they read in their own
    /// context, which the main thread's figures above leave out, and the two summed.
    /// </summary>
    private static void AppendSubagents(StringBuilder text, StatsReport report, StatsReport? baseline)
    {
        IReadOnlyList<SubagentRow> now = report.Subagents ?? [];
        IReadOnlyList<SubagentRow>? before = baseline?.Subagents;
        if (now.Count == 0 && (before?.Count ?? 0) == 0)
        {
            return;
        }

        int runs = now.Sum(r => r.Runs);
        long context = now.Sum(r => r.ContextTokens);
        text.Append(Invariant($"subagents {runs} runs{CountDelta(runs, before?.Sum(r => r.Runs))}"))
            .Append(Invariant($" | context {Tokens(context)}{PercentDelta(context, before?.Sum(r => r.ContextTokens))}"))
            .Append(Invariant($" | main + subagents {Tokens(report.ContextTokens + context)}"));
        if (now.Count > 0)
        {
            text.Append(" | ").AppendJoin(", ", now.Select(r => Invariant($"{r.Type} {r.Runs} run{(r.Runs == 1 ? "" : "s")} {Tokens(r.ContextTokens)}")));
        }

        text.Append('\n');
    }

    private static void AppendCategories(StringBuilder text, StatsReport report, StatsReport? baseline)
    {
        var current = report.Categories.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var before = baseline?.Categories.ToDictionary(c => c.Name, StringComparer.Ordinal)
            ?? new Dictionary<string, CategoryRow>(StringComparer.Ordinal);

        var names = current.Keys.Union(before.Keys)
            .OrderByDescending(n => current.GetValueOrDefault(n)?.ContextTokens ?? 0)
            .ThenByDescending(n => before.GetValueOrDefault(n)?.ContextTokens ?? 0)
            .ThenBy(n => n, StringComparer.Ordinal)
            .ToList();

        string header = Invariant($"{"category",-34} {"calls",6} {"context",8} {"share",6}");
        text.Append(header).Append(baseline is null ? "\n" : Invariant($" {"ctx chg",9} {"share chg",9}\n"));

        foreach (string name in names.Take(MaxCategoryRows))
        {
            AppendRow(text, name, current.GetValueOrDefault(name), before.GetValueOrDefault(name), report, baseline);
        }

        if (names.Count > MaxCategoryRows)
        {
            var rest = names.Skip(MaxCategoryRows).ToList();
            var now = new CategoryRow("", rest.Sum(n => current.GetValueOrDefault(n)?.Calls ?? 0), rest.Sum(n => current.GetValueOrDefault(n)?.ContextTokens ?? 0));
            var then = new CategoryRow("", rest.Sum(n => before.GetValueOrDefault(n)?.Calls ?? 0), rest.Sum(n => before.GetValueOrDefault(n)?.ContextTokens ?? 0));
            AppendRow(text, Invariant($"other ({rest.Count} categories)"), now, baseline is null ? null : then, report, baseline);
            text.Append("  (the smallest, summed; ug stats --json lists each)\n");
        }
    }

    private static void AppendRow(StringBuilder text, string name, CategoryRow? now, CategoryRow? then, StatsReport report, StatsReport? baseline)
    {
        long context = now?.ContextTokens ?? 0;
        text.Append(Invariant($"{name,-34} {now?.Calls ?? 0,6} {Tokens(context),8} {Share(context, report.ContextTokens),6}"));
        if (baseline is not null)
        {
            long before = then?.ContextTokens ?? 0;
            string contextDelta = before == 0 ? "new" : Percent(context, before);
            double sharePp = SharePercent(context, report.ContextTokens) - SharePercent(before, baseline.ContextTokens);
            text.Append(Invariant($" {contextDelta,9} {sharePp.ToString("+0.0;-0.0;0.0", _inv) + "pp",9}"));
        }

        text.Append('\n');
    }

    private static string Window(StatsReport r) => (r.Since, r.Until) switch
    {
        (null, null) => "all time",
        ({ } since, null) => "since " + Time(since),
        (null, { } until) => "until " + Time(until),
        ({ } since, { } until) => Time(since) + " .. " + Time(until),
    };

    private static string Time(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", _inv);

    private static string Tokens(long n) => n switch
    {
        >= 1_000_000 => (n / 1e6).ToString("0.0", _inv) + "M",
        >= 1_000 => (n / 1e3).ToString("0.0", _inv) + "k",
        _ => n.ToString(_inv),
    };

    private static double SharePercent(long part, long total) => total == 0 ? 0 : 100.0 * part / total;

    private static string Share(long part, long total) => SharePercent(part, total).ToString("0.0", _inv) + "%";

    private static string Percent(long now, long before) =>
        (100.0 * (now - before) / before).ToString("+0.0;-0.0;0.0", _inv) + "%";

    private static string PercentDelta(long now, long? before) =>
        before is null ? "" : before == 0 ? " (new)" : " (" + Percent(now, before.Value) + ")";

    private static string CountDelta(int now, int? before) =>
        before is null ? "" : " (" + (now - before.Value).ToString("+0;-0;0", _inv) + ")";

    private static string Invariant(FormattableString s) => s.ToString(_inv);
}
