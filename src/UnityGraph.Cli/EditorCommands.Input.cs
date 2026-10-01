using System.CommandLine;
using System.Globalization;
using UnityGraph.Core;

namespace UnityGraph.Cli;

/// <summary>
/// The play-input commands (SPEC-play-input, Commands): `ug ui`, `ug click` and the device actions. They are
/// editor-ops steps with the module switch `play-input` and bridge 0.4.0 as their minimum. An action
/// finishes in the editor after its length, so its request waits that long plus ten seconds.
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
        Option<bool> clickShot = ShotOption();
        yield return Command("click", "Click an element through the EventSystem (pointer enter, down, up, click), as a mouse would; play mode only.", [element, value, clickShot], (root, config, r) =>
            Input(root, config, "click", Query("/ui/click", ("element", r.GetValue(element)), ("value", r.GetValue(value))), 0, r.GetValue(clickShot)));

        var key = new Argument<string>("key") { Description = "A key (Space, Enter, A, LeftShift, UpArrow, 1), a gamepad button (pad:south, start, lshoulder) or a mouse button (mouse:left)." };
        Option<double?> pressHold = HoldOption("0.1");
        Option<bool> pressShot = ShotOption();
        yield return Command("press", "Hold a key or button down and release it, through queued Input System events; play mode only.", [key, pressHold, pressShot], (root, config, r) =>
            Action(root, config, "press", "/input/press", [("key", r.GetValue(key))], ("hold", r.GetValue(pressHold)), 0.1, 0.05, 60, "--hold", r.GetValue(pressShot)));

        var side = new Argument<string>("side") { Description = "left or right." };
        var stickAt = new Argument<string>("x,y") { Description = "Deflection, each -1 to 1, y up: 1,0 is right, 0,1 is up." };
        Option<double?> stickHold = HoldOption("0.5");
        Option<bool> stickShot = ShotOption();
        yield return Command("stick", "Push a gamepad stick for a while, then centre it.", [side, stickAt, stickHold, stickShot], (root, config, r) =>
        {
            if (r.GetValue(side) is not ("left" or "right"))
            {
                return (1, "side is left or right\n");
            }

            if (!Pair(r.GetValue(stickAt), out double x, out double y) || Math.Abs(x) > 1 || Math.Abs(y) > 1)
            {
                return (1, "x,y must be -1 to 1, like 1,0\n");
            }

            return Action(root, config, "stick", "/input/stick", [("side", r.GetValue(side)), ("x", Num(x)), ("y", Num(y))], ("hold", r.GetValue(stickHold)), 0.5, 0.05, 60, "--hold", r.GetValue(stickShot));
        });

        var tapAt = new Argument<string>("x,y") { Description = "A Game view point in pixels, y down from the top-left: 640,360." };
        Option<double?> tapHold = HoldOption("0.1");
        var tapMouse = new Option<bool>("--mouse") { Description = "Click the left mouse button instead of touching." };
        Option<bool> tapShot = ShotOption();
        yield return Command("tap", "Touch (or click) a Game view point through the Input System; ug click is the way to use a UI.", [tapAt, tapHold, tapMouse, tapShot], (root, config, r) =>
        {
            if (!Pair(r.GetValue(tapAt), out double x, out double y))
            {
                return (1, PointHelp);
            }

            return Action(root, config, "tap", "/input/tap", [("x", Num(x)), ("y", Num(y)), ("mouse", r.GetValue(tapMouse) ? "true" : null)], ("hold", r.GetValue(tapHold)), 0.1, 0.05, 60, "--hold", r.GetValue(tapShot));
        });

        var from = new Argument<string>("from") { Description = "Start point x,y in Game view pixels, y down." };
        var to = new Argument<string>("to") { Description = "End point x,y." };
        var over = new Option<double?>("--over") { Description = "Seconds the move takes, 0.05 to 10 (default 0.3)." };
        var swipeMouse = new Option<bool>("--mouse") { Description = "Hold the left mouse button instead of touching." };
        Option<bool> swipeShot = ShotOption();
        yield return Command("swipe", "Touch (or hold the mouse) at one point, move to another over a short time, release.", [from, to, over, swipeMouse, swipeShot], (root, config, r) =>
        {
            if (!Pair(r.GetValue(from), out double x1, out double y1) || !Pair(r.GetValue(to), out double x2, out double y2))
            {
                return (1, PointHelp);
            }

            return Action(root, config, "swipe", "/input/swipe", [("x1", Num(x1)), ("y1", Num(y1)), ("x2", Num(x2)), ("y2", Num(y2)), ("mouse", r.GetValue(swipeMouse) ? "true" : null)], ("over", r.GetValue(over)), 0.3, 0.05, 10, "--over", r.GetValue(swipeShot));
        });

        var seconds = new Argument<double>("seconds") { Description = "Real seconds, 0.05 to 60." };
        Option<bool> waitShot = ShotOption();
        yield return Command("wait", "Let the game run for a while and print what changed meanwhile.", [seconds, waitShot], (root, config, r) =>
        {
            double length = r.GetValue(seconds);
            return length is < 0.05 or > 60 || double.IsNaN(length)
                ? (1, "seconds must be 0.05 to 60\n")
                : Input(root, config, "wait", Query("/input/wait", ("seconds", Num(length))), length, r.GetValue(waitShot));
        });
    }

    private const string PointHelp = "a point is x,y in Game view pixels, like 640,360 (y down from the top)\n";

    private static Option<bool> ShotOption() =>
        new("--shot") { Description = "Also take a picture of the game view afterwards (ug shot) and print its path." };

    private static Option<double?> HoldOption(string fallback) =>
        new("--hold") { Description = "Seconds to hold, 0.05 to 60 (default " + fallback + "); never fewer than two frames." };

    // One device action: its range check, then the request with the hold in its query.
    private static (int ExitCode, string Text) Action(string root, UnityGraphConfig config, string command, string path, (string Name, string? Value)[] query, (string Name, double? Value) length, double fallback, double min, double max, string option, bool shot)
    {
        if (length.Value is { } given && (given < min || given > max || double.IsNaN(given)))
        {
            return (1, option + " must be " + Num(min) + " to " + Num(max) + " seconds\n");
        }

        return Input(root, config, command, Query(path, [.. query, (length.Name, length.Value is { } value ? Num(value) : null)]), length.Value ?? fallback, shot);
    }

    // The request waits for the action to run in the editor: its length plus ten seconds. --shot adds the
    // game view picture's line; a picture that cannot be taken adds its refusal and leaves the action's result.
    private static (int ExitCode, string Text) Input(string root, UnityGraphConfig config, string command, string pathAndQuery, double length, bool shot = false)
    {
        (int exitCode, string text) = Op(root, config, command, new OpRequest(pathAndQuery, [], TimeSpan.FromSeconds(length + 10), TimeSpan.FromSeconds(60)) { PlayInput = true });
        if (!shot || _opFailed)
        {
            return (exitCode, text);
        }

        (_, string picture) = Op(root, config, "shot", new OpRequest(Query("/shot", ("view", "game"))));
        _opFailed = false;
        return (exitCode, text + picture);
    }

    // `640,360` as two numbers.
    private static bool Pair(string? text, out double x, out double y)
    {
        x = 0;
        y = 0;
        string[] parts = (text ?? "").Split(',');
        return parts.Length == 2
            && double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x)
            && double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y)
            && double.IsFinite(x) && double.IsFinite(y);
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
