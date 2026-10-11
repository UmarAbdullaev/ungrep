using System.Text.Json;
using System.Text.RegularExpressions;

namespace UnityGraph.Bench;

/// <summary>A tool call read back from a transcript: the tool, and what an arm's `own` pattern is matched against.</summary>
public sealed record ToolUse(string Name, string Signature);

/// <summary>
/// Scores answers against a question's key, and reads the tool calls of an agent run for redo and
/// leak detection (SPEC-bench, Script mode and Agent mode).
/// </summary>
public static partial class Scoring
{
    /// <summary>
    /// The key items not found, as the suite wrote them: items in <paramref name="answers"/>,
    /// check items (marked `check:`) in <paramref name="check"/>.
    /// </summary>
    public static IReadOnlyList<string> Missing(AnswerKey key, string answers, string check) =>
        key.Items.Where(item => !item.IsMatch(answers)).Select(item => item.ToString())
            .Concat(key.CheckItems.Where(item => !item.IsMatch(check)).Select(item => "check: " + item))
            .ToList();

    /// <summary>The `not` items an agent's final answer holds.</summary>
    public static IReadOnlyList<string> Unwanted(AnswerKey key, string text) =>
        key.Not.Where(item => item.IsMatch(text)).Select(item => item.ToString()).ToList();

    /// <summary>
    /// The main thread's tool calls in transcript order. A shell call is signed by its command, a
    /// skill by its name, anything else (MCP tools included) by the tool name alone, so a search
    /// pattern that names another arm's tool is not a use of that tool.
    /// </summary>
    public static IReadOnlyList<ToolUse> ToolUses(IEnumerable<string> transcriptLines)
    {
        var uses = new List<ToolUse>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in transcriptLines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                JsonElement entry = doc.RootElement;
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("type", out JsonElement type) || type.GetString() != "assistant"
                    || (entry.TryGetProperty("isSidechain", out JsonElement side) && side.ValueKind == JsonValueKind.True)
                    || !entry.TryGetProperty("message", out JsonElement message) || message.ValueKind != JsonValueKind.Object
                    || !message.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object
                        || !block.TryGetProperty("type", out JsonElement blockType) || blockType.GetString() != "tool_use"
                        || !block.TryGetProperty("name", out JsonElement name) || name.GetString() is not { } tool)
                    {
                        continue;
                    }

                    // A message split over several lines repeats its earlier blocks.
                    if (block.TryGetProperty("id", out JsonElement id) && id.GetString() is { } useId && !seen.Add(useId))
                    {
                        continue;
                    }

                    JsonElement input = block.TryGetProperty("input", out JsonElement i) ? i : default;
                    uses.Add(new ToolUse(tool, Signature(tool, input)));
                }
            }
        }

        return uses;
    }

    /// <summary>
    /// Redo (spike S3's retry pattern): a text search after the arm's first own call. An arm with
    /// no own pattern (grep) never redoes: its own call is the search.
    /// </summary>
    public static bool IsRedo(IReadOnlyList<ToolUse> uses, Regex? own)
    {
        if (own is null)
        {
            return false;
        }

        int first = -1;
        for (int i = 0; i < uses.Count; i++)
        {
            if (own.IsMatch(uses[i].Signature))
            {
                first = i;
                break;
            }
        }

        return first >= 0 && uses.Skip(first + 1).Any(u => IsTextSearch(u) && !own.IsMatch(u.Signature));
    }

    /// <summary>Whether a call used another arm's tool, which the deny rules should have kept out.</summary>
    public static bool IsLeaked(IReadOnlyList<ToolUse> uses, Arm arm, IEnumerable<Arm> arms) =>
        arms.Where(a => a.Name != arm.Name && a.Own is not null)
            .Any(other => uses.Any(u => other.Own!.IsMatch(u.Signature) && !(arm.Own?.IsMatch(u.Signature) ?? false)));

    public static bool IsTextSearch(ToolUse use) =>
        use.Name is "Grep" or "Glob" || (use.Name is "Bash" or "PowerShell" && SearchCommand().IsMatch(use.Signature));

    private static string Signature(string tool, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return tool;
        }

        if (tool is "Bash" or "PowerShell" && input.TryGetProperty("command", out JsonElement command))
        {
            return command.GetString() ?? tool;
        }

        if (tool == "Skill" && input.TryGetProperty("skill", out JsonElement skill))
        {
            return "Skill(" + skill.GetString() + ")";
        }

        return tool;
    }

    [GeneratedRegex(@"(^|[\s;&|(`])(grep|egrep|rg|find|findstr|Select-String|sls)(\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex SearchCommand();
}
