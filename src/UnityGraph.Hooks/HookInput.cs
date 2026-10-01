using System.Text.Json;

namespace UnityGraph.Hooks;

/// <summary>
/// One hook call from Claude Code: the event name from the command line and the JSON payload
/// from stdin (`session_id`, `cwd`, `hook_event_name`, `tool_name`, `tool_input`, ...).
/// </summary>
public sealed class HookInput
{
    public HookInput(string eventName, JsonElement payload)
    {
        EventName = eventName;
        Payload = payload;
    }

    public string EventName { get; }

    public JsonElement Payload { get; }

    /// <summary>Claude Code's id for the session; null when the payload has none.</summary>
    public string? SessionId => GetString("session_id");

    /// <summary>The session's working folder; null when the payload has none.</summary>
    public string? Cwd => GetString("cwd");

    /// <summary>Set for PreToolUse and PostToolUse.</summary>
    public string? ToolName => GetString("tool_name");

    /// <summary>The tool's arguments; an undefined element when the payload has none.</summary>
    public JsonElement ToolInput => Payload.TryGetProperty("tool_input", out JsonElement input) ? input : default;

    /// <exception cref="FormatException">Not a JSON object.</exception>
    public static HookInput Parse(string eventName, string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("hook payload is not a JSON object");
            }

            return new HookInput(eventName, doc.RootElement.Clone());
        }
        catch (JsonException e)
        {
            throw new FormatException("hook payload is not valid JSON: " + e.Message, e);
        }
    }

    private string? GetString(string name) =>
        Payload.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
