using System.Text.Json;

namespace UnityGraph.Stats;

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
/// write), where they went, and the chores handed to the worker subagent.
/// </summary>
public sealed record SessionStats(
    string Id,
    IReadOnlyList<long> TurnContext,
    IReadOnlyList<int> CompactionTurns,
    IReadOnlyDictionary<string, CategoryTotals> Categories,
    WorkerTotals Worker)
{
    public long ContextTokens => TurnContext.Sum();
}

/// <summary>
/// Totals over a project's sessions. Sessions with no model turn inside the window are left out.
/// </summary>
public sealed class UsageStats
{
    public UsageStats(IReadOnlyList<SessionStats> sessions)
    {
        Sessions = sessions;
        Turns = sessions.Sum(s => s.TurnContext.Count);
        ContextTokens = sessions.Sum(s => s.ContextTokens);
        Compactions = sessions.Sum(s => s.CompactionTurns.Count);
        Worker = sessions.Aggregate(WorkerTotals.None, (sum, s) => sum.Add(s.Worker));

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

    public WorkerTotals Worker { get; }
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

    public static UsageStats AnalyzeDirectory(string transcriptDirectory, TimeWindow window)
    {
        var sessions = new List<SessionStats>();
        if (!Directory.Exists(transcriptDirectory))
        {
            return new UsageStats(sessions);
        }

        var files = Directory.GetFiles(transcriptDirectory, "*.jsonl").Order(StringComparer.Ordinal);
        foreach (string file in files)
        {
            string id = Path.GetFileNameWithoutExtension(file);
            string workers = Path.Combine(transcriptDirectory, id, "subagents");
            SessionStats? session = AnalyzeSession(id, ReadLines(file), window, agentId => WorkerTranscript(workers, agentId));
            if (session is not null)
            {
                sessions.Add(session);
            }
        }

        return new UsageStats(sessions);
    }

    /// <summary>
    /// Returns null when no model turn falls inside the window. <c>workerTranscript</c> gives a
    /// worker's own transcript by agent id, or null when it is gone.
    /// </summary>
    public static SessionStats? AnalyzeSession(
        string id,
        IEnumerable<string> lines,
        TimeWindow window,
        Func<string, IEnumerable<string>?>? workerTranscript = null)
    {
        var turns = new List<long>();
        var seenMessages = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<string>();
        var calls = new Dictionary<string, int>(StringComparer.Ordinal);
        var context = new Dictionary<string, double>(StringComparer.Ordinal);
        var worker = new WorkerDelegations();

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
            if (IsMainThread(entry, "user"))
            {
                worker.Read(entry);
                continue;
            }

            if (!IsAssistantTurn(entry, window, out JsonElement message, out JsonElement content))
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
                if (WorkerDelegations.IsWorker(name, input)
                    && block.TryGetProperty("id", out JsonElement callId)
                    && callId.ValueKind == JsonValueKind.String)
                {
                    worker.Started(callId.GetString()!);
                }

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

        return new SessionStats(
            id,
            turns,
            compactions,
            categories,
            worker.Totals(agentId => workerTranscript?.Invoke(agentId) is { } workerLines ? WorkerContext(workerLines) : 0));
    }

    /// <summary>
    /// What a worker read over its turns, summed like the main thread's context. Every entry of a
    /// worker's transcript is a sidechain entry; a delegation started inside the window counts whole.
    /// </summary>
    public static long WorkerContext(IEnumerable<string> lines)
    {
        long sum = 0;
        var seenMessages = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using JsonDocument? doc = TryParse(line);
            if (doc is null
                || doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("type", out JsonElement type)
                || !type.ValueEquals("assistant")
                || !doc.RootElement.TryGetProperty("message", out JsonElement message)
                || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("usage", out JsonElement usage)
                || !message.TryGetProperty("id", out JsonElement idElement)
                || idElement.ValueKind != JsonValueKind.String
                || !seenMessages.Add(idElement.GetString()!))
            {
                continue;
            }

            sum += Tokens(usage, "input_tokens")
                + Tokens(usage, "cache_read_input_tokens")
                + Tokens(usage, "cache_creation_input_tokens");
        }

        return sum;
    }

    private static bool IsAssistantTurn(JsonElement entry, TimeWindow window, out JsonElement message, out JsonElement content)
    {
        message = default;
        content = default;

        if (!IsMainThread(entry, "assistant"))
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

    /// <summary>Subagent turns run in their own context; they are not the main thread's cost.</summary>
    private static bool IsMainThread(JsonElement entry, string type) =>
        !(entry.TryGetProperty("isSidechain", out JsonElement sidechain) && sidechain.ValueKind == JsonValueKind.True)
        && entry.TryGetProperty("type", out JsonElement t)
        && t.ValueKind == JsonValueKind.String
        && t.ValueEquals(type);

    private static long Tokens(JsonElement usage, string property) =>
        usage.ValueKind == JsonValueKind.Object
        && usage.TryGetProperty(property, out JsonElement value)
        && value.TryGetInt64(out long tokens)
            ? tokens
            : 0;

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

    /// <summary>
    /// `&lt;session&gt;/subagents/agent-&lt;id&gt;.jsonl`. The id comes from the transcript, so anything but
    /// letters, digits, `-` and `_` is not looked up.
    /// </summary>
    private static IEnumerable<string>? WorkerTranscript(string directory, string agentId)
    {
        if (agentId.Length == 0 || !agentId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return null;
        }

        string path = Path.Combine(directory, "agent-" + agentId + ".jsonl");
        return File.Exists(path) ? ReadLines(path) : null;
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
