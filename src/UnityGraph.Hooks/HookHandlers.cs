namespace UnityGraph.Hooks;

/// <summary>
/// The handlers `ug hook` runs. None yet: the step-3 modules (guards, unity-loop, reindex on
/// edit) register theirs here, each behind its config switch.
/// </summary>
public static class HookHandlers
{
    public static IReadOnlyList<IHookHandler> All { get; } = [];
}
