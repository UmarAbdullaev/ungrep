using System.Text.Encodings.Web;
using System.Text.Json;
using UnityGraph.Core;

namespace UnityGraph.Hooks;

/// <summary>
/// At session start in a Unity project, one line saying which questions `ug` answers in one call
/// (B1f, SPEC-query, Session hint): the skill listing alone left Claude on Grep in most agent runs.
/// </summary>
public sealed class SessionHintHandler : IHookHandler
{
    public const string Hint =
        "unitygraph: ug indexes this Unity project's code and assets. `ug refs|explain|impact <X>` answers who calls, "
        + "uses, subscribes to or implements X, what a scene or prefab wires, and what breaks if X changes, in one call "
        + "with file:line, where Grep and Read take several. Editor commands are in the `unity` skill.";

    private static readonly JsonSerializerOptions _json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string Module => "query";

    public bool Handles(HookInput input) => input.EventName == "SessionStart";

    public string? Handle(HookInput input, UnityProject project, CancellationToken cancel) =>
        JsonSerializer.Serialize(new { hookSpecificOutput = new { hookEventName = "SessionStart", additionalContext = Hint } }, _json);
}
