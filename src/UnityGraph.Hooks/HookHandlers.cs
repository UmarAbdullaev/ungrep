using UnityGraph.EditorLink;

namespace UnityGraph.Hooks;

/// <summary>
/// The handlers `ug hook` runs: the guards, the compile gate of unity-loop and the session hint
/// of query; other step-3 modules (reindex on edit) register theirs here, each behind its config switch.
/// </summary>
public static class HookHandlers
{
    public static IReadOnlyList<IHookHandler> All { get; } =
    [
        new GuardsHandler(),
        new CompileGateHandler((root, deadline) => CompileGate.Check(root, deadline)),
        new SessionHintHandler(),
    ];
}
