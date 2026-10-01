using System.CommandLine;

namespace UnityGraph.Cli;

/// <summary>
/// The play-input commands (SPEC-play-input, Commands): `ug ui` and `ug click` so far. They are
/// editor-ops steps with the module switch `play-input` and bridge 0.4.0 as their minimum.
/// </summary>
internal static partial class EditorCommands
{
    private static IEnumerable<Command> InputCommands()
    {
        var all = new Option<bool>("--all") { Description = "Also list disabled, blocked and off-screen elements (up to 150 lines)." };
        yield return Command("ui", "The interactable uGUI elements a player could use now: address, kind, label or value, screen position.", [all], (root, config, r) =>
            Op(root, config, "ui", new OpRequest(Query("/ui", ("all", r.GetValue(all) ? "true" : null))) { PlayInput = true }));

        var element = new Argument<string>("element") { Description = "An address from ug ui (Canvas/Menu/Play), a label (Play), or a screen point x,y." };
        var value = new Option<string?>("--value") { Description = "Operate the element's component instead of clicking: a slider or scrollbar number, input text, dropdown option, toggle on/off." };
        yield return Command("click", "Click an element through the EventSystem (pointer enter, down, up, click), as a mouse would; play mode only.", [element, value], (root, config, r) =>
            Op(root, config, "click", new OpRequest(Query("/ui/click", ("element", r.GetValue(element)), ("value", r.GetValue(value)))) { PlayInput = true }));
    }
}
