using UnityGraph.EditorLink;

namespace UnityGraph.Hooks;

/// <summary>
/// The handlers `ug hook` runs: the compile gate of unity-loop so far; the other step-3 modules
/// (guards, reindex on edit) register theirs here, each behind its config switch.
/// </summary>
public static class HookHandlers
{
    public static IReadOnlyList<IHookHandler> All { get; } = [new CompileGateHandler((root, deadline) => CompileGate.Check(root, deadline))];
}
