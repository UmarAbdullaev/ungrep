using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnityGraph.Stats;

/// <summary>
/// Chores the main thread handed to the plugin's `unity-worker` subagent (SPEC-worker-agent,
/// Measuring). A delegation is short when the worker made fewer than <see cref="WorkerDelegations.ShortBelow"/>
/// tool calls: a chore that small costs more delegated than done inline. A verdict is over budget
/// past <see cref="WorkerDelegations.VerdictBudget"/> tokens (bytes / 4). ContextTokens is what the
/// workers themselves read: their turns' context summed the way the main thread's is, from their
/// own transcripts (0 for a report written before it was counted).
/// </summary>
public sealed record WorkerTotals(int Delegations, int Short, int OverBudget, int NoResult, long VerdictTokens, long ContextTokens = 0)
{
    public static WorkerTotals None { get; } = new(0, 0, 0, 0, 0);

    /// <summary>Delegations whose result arrived; the verdict average is taken over these.</summary>
    [JsonIgnore]
    public int Finished => Delegations - NoResult;

    public WorkerTotals Add(WorkerTotals other) => new(
        Delegations + other.Delegations,
        Short + other.Short,
        OverBudget + other.OverBudget,
        NoResult + other.NoResult,
        VerdictTokens + other.VerdictTokens,
        ContextTokens + other.ContextTokens);
}

/// <summary>
/// Finds worker delegations in one main-thread transcript. Claude Code records the Agent call's
/// result in two shapes: a foreground run answers in its tool result, whose `toolUseResult`
/// carries `totalToolUseCount`; a background run answers `async_launched` there and its verdict
/// comes later as a `&lt;task-notification&gt;` naming the call's `&lt;tool-use-id&gt;` with
/// `&lt;tool_uses&gt;N&lt;/tool_uses&gt;`. A resumed worker notifies again under the same id: the
/// largest count and the largest verdict are kept. Both shapes name the worker's agent id, which
/// names its transcript; the `totalTokens` and `&lt;subagent_tokens&gt;` they carry are the
/// worker's last context, not what it read over all its turns.
/// </summary>
public sealed class WorkerDelegations
{
    public const string AgentName = "unity-worker";

    public const int ShortBelow = 3;

    public const int VerdictBudget = 800;

    private const string NotificationTag = "<task-notification>";

    private readonly Dictionary<string, Delegation> _byCallId = new(StringComparer.Ordinal);

    /// <summary>`unity-worker`, or a plugin-qualified name such as `unitygraph:unity-worker`.</summary>
    public static bool IsWorker(string toolName, JsonElement input) =>
        toolName is "Agent" or "Task"
        && input.ValueKind == JsonValueKind.Object
        && input.TryGetProperty("subagent_type", out JsonElement type)
        && type.ValueKind == JsonValueKind.String
        && type.GetString() is { } name
        && (name == AgentName || name.EndsWith(":" + AgentName, StringComparison.Ordinal));

    public void Started(string callId) => _byCallId.TryAdd(callId, new Delegation());

    /// <summary>A main-thread user entry: tool results and task notifications.</summary>
    public void Read(JsonElement entry)
    {
        if (_byCallId.Count == 0
            || !entry.TryGetProperty("message", out JsonElement message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out JsonElement content))
        {
            return;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            ReadNotifications(content.GetString()!);
            return;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out JsonElement type))
            {
                continue;
            }

            if (type.ValueEquals("text") && block.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
            {
                ReadNotifications(text.GetString()!);
            }
            else if (type.ValueEquals("tool_result")
                && block.TryGetProperty("tool_use_id", out JsonElement id)
                && id.ValueKind == JsonValueKind.String
                && _byCallId.TryGetValue(id.GetString()!, out Delegation? delegation))
            {
                ReadToolResult(entry, block, delegation);
            }
        }
    }

    /// <param name="workerContext">Context a worker read over its turns, by agent id.</param>
    public WorkerTotals Totals(Func<string, long>? workerContext = null)
    {
        int shortOnes = 0, overBudget = 0, noResult = 0;
        long verdictTokens = 0, contextTokens = 0;
        foreach (Delegation d in _byCallId.Values)
        {
            // A worker still running, or one whose session ended first, read context all the same.
            if (d.AgentId is { } agentId && workerContext is not null)
            {
                contextTokens += workerContext(agentId);
            }

            if (d.ToolCalls is not { } calls)
            {
                noResult++;
                continue;
            }

            long tokens = (d.VerdictBytes + 3) / 4;
            verdictTokens += tokens;
            shortOnes += calls < ShortBelow ? 1 : 0;
            overBudget += tokens > VerdictBudget ? 1 : 0;
        }

        return new WorkerTotals(_byCallId.Count, shortOnes, overBudget, noResult, verdictTokens, contextTokens);
    }

    private static void ReadToolResult(JsonElement entry, JsonElement block, Delegation delegation)
    {
        if (!entry.TryGetProperty("toolUseResult", out JsonElement result) || result.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (result.TryGetProperty("agentId", out JsonElement agentId) && agentId.ValueKind == JsonValueKind.String)
        {
            delegation.Ran(agentId.GetString()!);
        }

        if (result.TryGetProperty("status", out JsonElement status) && status.ValueEquals("async_launched"))
        {
            return;
        }

        if (!result.TryGetProperty("totalToolUseCount", out JsonElement count) || !count.TryGetInt32(out int calls))
        {
            return;
        }

        // A hand-back delivers the report as a message; the tool result then only points at it.
        // Otherwise the tool result wraps the verdict in Claude Code's own frame and indents it;
        // `toolUseResult.content` holds the worker's message as written, which the budget is on.
        string verdict = result.TryGetProperty("handbackReport", out JsonElement handback)
            && handback.ValueKind == JsonValueKind.Object
            && handback.TryGetProperty("text", out JsonElement report)
            && report.ValueKind == JsonValueKind.String
                ? report.GetString()!
                : result.TryGetProperty("content", out JsonElement written) && written.ValueKind == JsonValueKind.Array
                    ? Text(written)
                    : ResultText(block);
        delegation.Saw(calls, Encoding.UTF8.GetByteCount(verdict));
    }

    private void ReadNotifications(string text)
    {
        int start = text.IndexOf(NotificationTag, StringComparison.Ordinal);
        while (start >= 0)
        {
            int next = text.IndexOf(NotificationTag, start + NotificationTag.Length, StringComparison.Ordinal);
            string note = next < 0 ? text[start..] : text[start..next];
            start = next;

            if (Between(note, "<tool-use-id>", "</tool-use-id>") is not { } id
                || !_byCallId.TryGetValue(id, out Delegation? delegation)
                || Between(note, "<tool_uses>", "</tool_uses>") is not { } count
                || !int.TryParse(count, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int calls))
            {
                continue;
            }

            if (Between(note, "<task-id>", "</task-id>") is { Length: > 0 } agentId)
            {
                delegation.Ran(agentId);
            }

            int open = note.IndexOf("<result>", StringComparison.Ordinal);
            int close = note.LastIndexOf("</result>", StringComparison.Ordinal);
            string verdict = open >= 0 && close > open ? note[(open + "<result>".Length)..close] : "";
            delegation.Saw(calls, Encoding.UTF8.GetByteCount(verdict));
        }
    }

    private static string? Between(string text, string open, string close)
    {
        int a = text.IndexOf(open, StringComparison.Ordinal);
        if (a < 0)
        {
            return null;
        }

        a += open.Length;
        int b = text.IndexOf(close, a, StringComparison.Ordinal);
        return b < 0 ? null : text[a..b].Trim();
    }

    private static string ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out JsonElement content))
        {
            return "";
        }

        return content.ValueKind == JsonValueKind.String ? content.GetString()! : Text(content);
    }

    /// <summary>The text parts of a content array, joined.</summary>
    private static string Text(JsonElement content)
    {
        var text = new StringBuilder();
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement part in content.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object
                    && part.TryGetProperty("text", out JsonElement t)
                    && t.ValueKind == JsonValueKind.String)
                {
                    text.Append(t.GetString());
                }
            }
        }

        return text.ToString();
    }

    /// <summary>What was seen of one delegation; no tool count yet means no result yet.</summary>
    private sealed class Delegation
    {
        public string? AgentId { get; private set; }

        public int? ToolCalls { get; private set; }

        public int VerdictBytes { get; private set; }

        public void Saw(int toolCalls, int verdictBytes)
        {
            ToolCalls = Math.Max(ToolCalls ?? 0, toolCalls);
            VerdictBytes = Math.Max(VerdictBytes, verdictBytes);
        }

        public void Ran(string agentId) => AgentId ??= agentId;
    }
}
