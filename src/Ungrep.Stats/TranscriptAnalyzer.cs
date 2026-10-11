using System.Text.Json;

namespace Ungrep.Stats;

/// <summary>
/// Inclusive time bounds on transcript entries. Entries without a timestamp are always kept.
/// Filtering by entry rather than by session is what lets an old report be reproduced after its
/// sessions were resumed and grew.
/// </summary>
public sealed record TimeWindow(DateTimeOffset? Since = null, DateTimeOffset? Until = null)
{
    public static TimeWindow All { get; } = new();

    public bool Contains(DateTimeOffset time) =>
        (Since is null || time >= Since) && (Until is null || time <= Until);
}

/// <summary>
/// Tool calls in one category and the context tokens charged to them: each model turn's context
/// is split evenly over the tool calls whose results that turn consumed.
/// </summary>
public sealed record CategoryTotals(int Calls, double ContextTokens);

/// <summary>
/// One transcript file: context tokens per main-thread model turn (input + cache read + cache
/// write) and where they went.
/// </summary>
public sealed record SessionStats(
    string Id,
    IReadOnlyList<long> TurnContext,
    IReadOnlyList<int> CompactionTurns,
    IReadOnlyDictionary<string, CategoryTotals> Categories)
{
    public long ContextTokens => TurnContext.Sum();
}

/// <summary>
/// Totals over a project's sessions. Sessions with no model turn inside the window are left out.
/// Subagents are counted apart: their turns run in their own context, not the main thread's.
/// </summary>
public sealed class UsageStats
{
    public UsageStats(IReadOnlyList<SessionStats> sessions, IReadOnlyList<SubagentRow>? subagents = null)
    {
        Sessions = sessions;
        Subagents = SubagentRow.Merge(subagents ?? []);
        Turns = sessions.Sum(s => s.TurnContext.Count);
        ContextTokens = sessions.Sum(s => s.ContextTokens);
        Compactions = sessions.Sum(s => s.CompactionTurns.Count);

        var firstTurns = sessions.Select(s => s.TurnContext[0]).Order().ToList();
        FirstTurnMedian = firstTurns.Count == 0 ? 0 : firstTurns[firstTurns.Count / 2];

        var categories = new SortedDictionary<string, CategoryTotals>(StringComparer.Ordinal);
        foreach (SessionStats session in sessions)
        {
            foreach ((string name, CategoryTotals totals) in session.Categories)
            {
                categories[name] = categories.TryGetValue(name, out CategoryTotals? sum)
                    ? new CategoryTotals(sum.Calls + totals.Calls, sum.ContextTokens + totals.ContextTokens)
                    : totals;
            }
        }

        Categories = categories;
    }

    public IReadOnlyList<SessionStats> Sessions { get; }

    public int Turns { get; }

    public long ContextTokens { get; }

    public int Compactions { get; }

    /// <summary>Median context of a session's first turn: system prompt, tools, CLAUDE.md.</summary>
    public long FirstTurnMedian { get; }

    public IReadOnlyDictionary<string, CategoryTotals> Categories { get; }

    /// <summary>Subagent runs and the context they read, one row per agent type.</summary>
    public IReadOnlyList<SubagentRow> Subagents { get; }
}

/// <summary>
/// Reads Claude Code transcripts (one .jsonl per session) and attributes context tokens to tool
/// categories. Ports the per-turn context sum of research/transcript-analysis/turns.js and the
/// attribution of calls.js.
/// </summary>
public static class TranscriptAnalyzer
{
    /// <summary>A drop of more than 40% between consecutive turns is taken as a compaction.</summary>
    public const double CompactionRatio = 0.6;

    /// <summary>The type of a subagent whose `.meta.json` is missing or names none.</summary>
    public const string UnknownAgentType = "unknown";

    public static UsageStats AnalyzeDirectory(string transcriptDirectory, TimeWindow window)
    {
        var sessions = new List<SessionStats>();
        if (!Directory.Exists(transcriptDirectory))
        {
            return new UsageStats(sessions);
        }

        var subagents = new List<SubagentRow>();
        var files = Directory.GetFiles(transcriptDirectory, "*.jsonl").Order(StringComparer.Ordinal);
        foreach (string file in files)
        {
            string id = Path.GetFileNameWithoutExtension(file);
            SessionStats? session = AnalyzeSession(id, ReadLines(file), window);
            if (session is not null)
            {
                sessions.Add(session);
            }

            // A background subagent can run on inside the window after its session's last turn.
            subagents.AddRange(AnalyzeSubagents(Path.Combine(transcriptDirectory, id, "subagents"), window));
        }

        return new UsageStats(sessions, subagents);
    }

    /// <summary>Returns null when no model turn falls inside the window.</summary>
    public static SessionStats? AnalyzeSession(string id, IEnumerable<string> lines, TimeWindow window) =>
        Analyze(id, lines, window, subagent: false);

    /// <summary>
    /// A session's subagents, one row per agent type: every `agent-&lt;id&gt;.jsonl` in its
    /// `subagents` folder, its turns summed like the main thread's. The type is `agentType` in the
    /// `.meta.json` beside the transcript. A subagent with no turn inside the window is left out.
    /// Claude Code's recorded `totalTokens` is only a subagent's last context, not what it read.
    /// </summary>
    public static IReadOnlyList<SubagentRow> AnalyzeSubagents(string subagentDirectory, TimeWindow window)
    {
        var rows = new List<SubagentRow>();
        if (!Directory.Exists(subagentDirectory))
        {
            return rows;
        }

        foreach (string file in Directory.GetFiles(subagentDirectory, "agent-*.jsonl").Order(StringComparer.Ordinal))
        {
            if (Analyze(Path.GetFileNameWithoutExtension(file), ReadLines(file), window, subagent: true) is { } run)
            {
                rows.Add(new SubagentRow(AgentType(Path.ChangeExtension(file, ".meta.json")), 1, run.ContextTokens));
            }
        }

        return SubagentRow.Merge(rows);
    }

    private static SessionStats? Analyze(string id, IEnumerable<string> lines, TimeWindow window, bool subagent)
    {
        var turns = new List<long>();
        var seenMessages = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<string>();
        var calls = new Dictionary<string, int>(StringComparer.Ordinal);
        var context = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using JsonDocument? doc = TryParse(line);
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            JsonElement entry = doc.RootElement;
            if (!IsAssistantTurn(entry, window, subagent, out JsonElement message, out JsonElement content))
            {
                continue;
            }

            // Claude Code writes one line per content block, each repeating the message's usage,
            // so a turn is counted on the first line of its message id.
            if (message.TryGetProperty("usage", out JsonElement usage)
                && message.TryGetProperty("id", out JsonElement idElement)
                && idElement.ValueKind == JsonValueKind.String
                && idElement.GetString() is { Length: > 0 } messageId
                && seenMessages.Add(messageId))
            {
                long turnContext = Tokens(usage, "input_tokens")
                    + Tokens(usage, "cache_read_input_tokens")
                    + Tokens(usage, "cache_creation_input_tokens");
                turns.Add(turnContext);

                // This turn consumed the results of the calls made since the previous turn.
                if (pending.Count > 0)
                {
                    double share = (double)turnContext / pending.Count;
                    foreach (string category in pending)
                    {
                        context[category] = context.GetValueOrDefault(category) + share;
                    }

                    pending.Clear();
                }
            }

            foreach (JsonElement block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object
                    || !block.TryGetProperty("type", out JsonElement type)
                    || type.ValueKind != JsonValueKind.String
                    || !type.ValueEquals("tool_use"))
                {
                    continue;
                }

                string name = block.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()!
                    : "";
                block.TryGetProperty("input", out JsonElement input);
                string category = Categorizer.Categorize(name, input);
                pending.Add(category);
                calls[category] = calls.GetValueOrDefault(category) + 1;
            }
        }

        if (turns.Count == 0)
        {
            return null;
        }

        var compactions = new List<int>();
        for (int i = 1; i < turns.Count; i++)
        {
            if (turns[i] < turns[i - 1] * CompactionRatio)
            {
                compactions.Add(i);
            }
        }

        var categories = new SortedDictionary<string, CategoryTotals>(StringComparer.Ordinal);
        foreach ((string category, int count) in calls)
        {
            categories[category] = new CategoryTotals(count, context.GetValueOrDefault(category));
        }

        return new SessionStats(id, turns, compactions, categories);
    }

    private static bool IsAssistantTurn(JsonElement entry, TimeWindow window, bool subagent, out JsonElement message, out JsonElement content)
    {
        message = default;
        content = default;

        // Subagent turns run in their own context; they are not the main thread's cost.
        if (!subagent && entry.TryGetProperty("isSidechain", out JsonElement sidechain) && sidechain.ValueKind == JsonValueKind.True)
        {
            return false;
        }

        if (!entry.TryGetProperty("type", out JsonElement type)
            || type.ValueKind != JsonValueKind.String
            || !type.ValueEquals("assistant"))
        {
            return false;
        }

        if (entry.TryGetProperty("timestamp", out JsonElement ts)
            && ts.ValueKind == JsonValueKind.String
            && ts.TryGetDateTimeOffset(out DateTimeOffset time)
            && !window.Contains(time))
        {
            return false;
        }

        return entry.TryGetProperty("message", out message)
            && message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("content", out content)
            && content.ValueKind == JsonValueKind.Array;
    }

    private static long Tokens(JsonElement usage, string property) =>
        usage.ValueKind == JsonValueKind.Object
        && usage.TryGetProperty(property, out JsonElement value)
        && value.TryGetInt64(out long tokens)
            ? tokens
            : 0;

    private static string AgentType(string metaPath)
    {
        try
        {
            using JsonDocument meta = JsonDocument.Parse(File.ReadAllText(metaPath));
            return meta.RootElement.ValueKind == JsonValueKind.Object
                && meta.RootElement.TryGetProperty("agentType", out JsonElement type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() is { Length: > 0 } name
                    ? name
                    : UnknownAgentType;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return UnknownAgentType;
        }
    }

    private static JsonDocument? TryParse(string line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // A session still being written can end in a partial line.
            return null;
        }
    }

    /// <summary>Claude Code may be appending to the file; share it rather than lock it.</summary>
    private static IEnumerable<string> ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}
