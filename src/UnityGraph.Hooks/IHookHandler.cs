using UnityGraph.Core;

namespace UnityGraph.Hooks;

/// <summary>
/// Reacts to hook calls for one module. Handlers stay silent (return null) unless they have
/// something to say (design rule 3); whatever they return goes to Claude Code on stdout.
/// </summary>
public interface IHookHandler
{
    /// <summary>The config switch that turns this handler on or off (`modules` in unitygraph.json).</summary>
    string Module { get; }

    bool Handles(HookInput input);

    // cancel: signalled when the dispatcher's time budget runs out.
    string? Handle(HookInput input, UnityProject project, CancellationToken cancel);
}
