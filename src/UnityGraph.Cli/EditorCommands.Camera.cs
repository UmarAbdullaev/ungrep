using System.CommandLine;
using System.Globalization;

namespace UnityGraph.Cli;

/// <summary>
/// `ug shot` (SPEC-editor-ops, Endpoints and Viewpoints): the game view, the scene view or one camera,
/// and with bridge 0.6.0 a temporary camera placed with --from, --look and --rotation, the scene view
/// framed on an object, and contact sheets around a target (--angles, --orbit); with 0.7.0 UI Toolkit
/// documents in the game view, one document alone, and a UXML file (--panel).
/// </summary>
internal static partial class EditorCommands
{
    // A sheet renders up to 216 tiles: give it longer than one picture.
    private static readonly TimeSpan _sheetTimeout = TimeSpan.FromSeconds(60);

    private static Command ShotCommand()
    {
        var view = new Argument<string?>("view")
        {
            Description = "game (default), scene, a camera's object address, an object with a UIDocument (that document alone), or a .uxml path; with --angles or --orbit the object or point x,y,z to circle.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var width = new Option<int?>("--width") { Description = "Pixels, 64 to 2048: the picture, or one tile of a sheet. Default: 960 (sheets 320 for --angles, 240 for --orbit)." };
        var height = new Option<int?>("--height") { Description = "Pixels, 64 to 2048: the picture, or one tile of a sheet. Default: 540 (sheets 240, 180)." };
        var from = new Option<string?>("--from") { Description = "x,y,z: a temporary camera stands here. Default with --look: the source camera's place." };
        var look = new Option<string?>("--look") { Description = "An object or a point x,y,z the temporary camera looks at." };
        var rotation = new Option<string?>("--rotation") { Description = "Euler angles x,y,z for the temporary camera, instead of --look." };
        var fov = new Option<double?>("--fov") { Description = "Vertical field of view in degrees, 1 to 179. Default: the source camera's." };
        var camera = new Option<string?>("--camera") { Description = "The camera whose settings the temporary camera copies (lens, clear, culling mask, URP data). Default: the view's camera, else the main camera." };
        var frame = new Option<string?>("--frame") { Description = "With scene: frame this object in the Scene view as F does, take the picture, and put the view back." };
        var keep = new Option<bool>("--keep") { Description = "With scene --frame: leave the Scene view framed." };
        var angles = new Option<bool>("--angles") { Description = "One PNG of six views around the target: front, back, left, right, top, three-quarter." };
        var orbit = new Option<int?>("--orbit")
        {
            Description = "One PNG grid around the target: this many azimuths (1 to 36, default 8) times --elevations.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var elevations = new Option<string?>("--elevations") { Description = "With --orbit: the rows' elevations in degrees, -89 to 89, up to 6. Default: 0,30,-15." };
        var distance = new Option<double?>("--distance") { Description = "With --angles or --orbit: how far the camera stands from the target. Default: the target fills each tile." };
        var panel = new Option<string?>("--panel") { Description = "With a .uxml view: the PanelSettings asset to draw it with. Default: the project's first PanelSettings." };
        Option[] cameraOptions = [angles, orbit, frame, from, look, rotation, fov, camera, keep, elevations, distance];
        return Ops("shot", "shot", "Save a picture of the game view, the scene view, one camera, a viewpoint or a sheet of views around an object to Library/unitygraph/shots/ and print its path.",
            [view, width, height, from, look, rotation, fov, camera, frame, keep, angles, orbit, elevations, distance, panel], r =>
            {
                bool sheet = r.GetValue(angles) || r.GetResult(orbit) != null;
                Option? needs = cameraOptions.FirstOrDefault(option => r.GetResult(option) is { Implicit: false });
                bool uxml = r.GetValue(panel) != null || (r.GetValue(view)?.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase) ?? false);
                return new OpRequest(Query("/shot",
                    ("view", r.GetValue(view)),
                    ("width", Text(r.GetValue(width))),
                    ("height", Text(r.GetValue(height))),
                    ("from", r.GetValue(from)),
                    ("look", r.GetValue(look)),
                    ("rotation", r.GetValue(rotation)),
                    ("fov", Text(r.GetValue(fov))),
                    ("camera", r.GetValue(camera)),
                    ("frame", r.GetValue(frame)),
                    ("keep", r.GetValue(keep) ? "true" : null),
                    ("angles", r.GetValue(angles) ? "true" : null),
                    ("orbit", r.GetResult(orbit) == null ? null : Text(r.GetValue(orbit)) ?? ""),
                    ("elevations", r.GetValue(elevations)),
                    ("distance", Text(r.GetValue(distance))),
                    ("panel", r.GetValue(panel))))
                {
                    Camera = needs != null,
                    UiToolkit = uxml,
                    Option = needs?.Name ?? (uxml ? (r.GetValue(panel) != null ? "--panel" : r.GetValue(view)) : null),
                    Timeout = sheet ? _sheetTimeout : OpTimeout,
                };
            });

        static string? Text(IFormattable? number) => number?.ToString(null, CultureInfo.InvariantCulture);
    }

    // `ug camera` (specs/SPEC-camera.md): plain cameras and Cinemachine 3; every edit answers once the brain has picked.
    private static Command CameraCommand()
    {
        const string Camera = "The camera's object address in the loaded scenes: FollowCam, Rig/CloseUp, Main Camera.";
        const string Brain = "The camera holding the CinemachineBrain. Default: the only active brain, else the main camera's.";
        Option<bool> all = Flag("--all", "Every camera, not the first 30.");
        Command camera = Ops("camera", "camera", "Cameras and Cinemachine: each camera's priority, pipeline and targets, the brains, and which camera is live.", [all], r =>
            Request("/camera", null, ("all", True(r.GetValue(all)))));

        Option<bool> listAll = Flag("--all", "Every camera, not the first 30.");
        camera.Subcommands.Add(Ops("list", "camera list", "The same as ug camera.", [listAll], r =>
            Request("/camera", null, ("all", True(r.GetValue(listAll))))));

        Argument<string?> name = Optional("name", "Name of the new camera. Default: CinemachineCamera, or Camera for --preset plain.");
        Option<string?> preset = Text("--preset", "static (default with Cinemachine), follow, third-person, freelook, dolly, top-down, side-scroller, or plain (a Unity Camera; the default without Cinemachine).");
        Option<string?> follow = Text("--follow", "The object the camera follows (Target.TrackingTarget).");
        Option<string?> lookAt = Text("--look-at", "The object the camera looks at, when not the follow target.");
        var priority = new Option<int?>("--priority") { Description = "Cinemachine priority; a plain camera's depth." };
        var fov = new Option<double?>("--fov") { Description = "Vertical field of view in degrees, 1 to 179. Default: the main camera's." };
        Option<string?> parent = Text("--parent", "Create under this object. Default: the active scene's root.");
        Option<string?> at = Text("--at", "x,y,z world position. Default: where the main camera stands.");
        camera.Subcommands.Add(Ops("create", "camera create", "Create a camera from a preset (Cinemachine's GameObject menu); adds a CinemachineBrain to the main camera when there is none.",
            [name, preset, follow, lookAt, priority, fov, parent, at], r =>
            Request("/camera/create", r.GetValue(parent),
                ("name", r.GetValue(name)),
                ("preset", r.GetValue(preset)),
                ("follow", r.GetValue(follow)),
                ("look-at", r.GetValue(lookAt)),
                ("priority", Invariant(r.GetValue(priority))),
                ("fov", Invariant(r.GetValue(fov))),
                ("parent", r.GetValue(parent)),
                ("at", r.GetValue(at)))));

        Argument<string> prioritized = Required("camera", Camera);
        Argument<string> value = Required("priority", "A whole number: Cinemachine's Priority (higher is live), or a plain camera's depth (higher draws later).");
        camera.Subcommands.Add(Ops("priority", "camera priority", "Set a Cinemachine camera's priority or a plain camera's depth.", [prioritized, value], r =>
            Request("/camera/priority", r.GetValue(prioritized), ("camera", r.GetValue(prioritized)), ("priority", r.GetValue(value)))));

        camera.Subcommands.Add(Stage("body", "--follow", "The object to follow (Target.TrackingTarget), or none."));
        camera.Subcommands.Add(Stage("aim", "--look-at", "The object to look at, follow (the follow target), or none."));

        Argument<string> noisy = Required("camera", Camera);
        Argument<string?> profile = Optional("profile", "A noise profile's name (6D Shake, Handheld_normal_mild) or asset path; none removes the noise.");
        var amplitude = new Option<double?>("--amplitude") { Description = "AmplitudeGain, 0 to 1000." };
        var frequency = new Option<double?>("--frequency") { Description = "FrequencyGain, 0 to 1000." };
        Option<bool> profilesAll = Flag("--all", "With nothing else: every profile, not the first 12 (bridge 0.9.0).");
        camera.Subcommands.Add(Ops("noise", "camera noise", "Set a camera's noise (CinemachineBasicMultiChannelPerlin); with nothing, show it and the profiles.", [noisy, profile, amplitude, frequency, profilesAll], r =>
        {
            bool every = r.GetValue(profilesAll);
            return Request("/camera/noise", r.GetValue(noisy),
                ("camera", r.GetValue(noisy)),
                ("profile", r.GetValue(profile)),
                ("amplitude", Invariant(r.GetValue(amplitude))),
                ("frequency", Invariant(r.GetValue(frequency))),
                ("all", True(every))) with
            { Full = every, Option = every ? "--all" : null };
        }));

        Option<string?> style = Text("--style", "Cut, EaseInOut, EaseIn, EaseOut, HardIn, HardOut, Linear or Custom.");
        var time = new Option<double?>("--time") { Description = "Blend seconds, 0 to 3600." };
        Option<string?> from = Text("--from", "With --to: a custom blend from this camera's name (any: every camera).");
        Option<string?> to = Text("--to", "With --from: the camera blended to (any: every camera).");
        Option<string?> blendBrain = Text("--brain", Brain);
        camera.Subcommands.Add(Ops("blend", "camera blend", "Set the brain's default blend, or one entry of its custom blends asset; with nothing, show them.", [style, time, from, to, blendBrain], r =>
            Request("/camera/blend", null,
                ("style", r.GetValue(style)),
                ("time", Invariant(r.GetValue(time))),
                ("from", r.GetValue(from)),
                ("to", r.GetValue(to)),
                ("brain", r.GetValue(blendBrain)))));

        Argument<string> forced = Required("camera", Camera);
        Option<string?> forceBrain = Text("--brain", Brain);
        camera.Subcommands.Add(Ops("force", "camera force", "Make a camera live now, whatever the priorities, until ug camera release (changes no data).", [forced, forceBrain], r =>
            Request("/camera/force", null, ("camera", r.GetValue(forced)), ("brain", r.GetValue(forceBrain)))));

        Option<string?> releaseBrain = Text("--brain", Brain);
        camera.Subcommands.Add(Ops("release", "camera release", "End ug camera force: the brain goes back to priorities.", [releaseBrain], r =>
            Request("/camera/release", null, ("brain", r.GetValue(releaseBrain)))));

        Argument<string?> holder = Optional("camera", "The Unity camera for the brain. Default: the main camera.");
        Option<string?> brainStyle = Text("--style", "Set the default blend style.");
        var brainTime = new Option<double?>("--time") { Description = "Set the default blend seconds, 0 to 3600." };
        camera.Subcommands.Add(Ops("brain", "camera brain", "Make sure a CinemachineBrain is on the camera and print its settings.", [holder, brainStyle, brainTime], r =>
            Request("/camera/brain", r.GetValue(holder), ("camera", r.GetValue(holder)), ("style", r.GetValue(brainStyle)), ("time", Invariant(r.GetValue(brainTime))))));
        return camera;

        // body and aim: a stage type (or none), then field=value pairs, after the camera.
        static Command Stage(string stage, string targetOption, string targetDescription)
        {
            Argument<string> staged = Required("camera", Camera);
            var values = new Argument<string[]>("values")
            {
                Description = "The " + stage + " type (" + (stage == "body" ? "Follow, ThirdPersonFollow, OrbitalFollow, PositionComposer, ..." : "RotationComposer, HardLookAt, PanTilt, ...")
                    + ") or none, then field=value pairs in ug set's grammar: CameraDistance=5, ShoulderOffset=0.8,-0.4,0.",
                Arity = ArgumentArity.ZeroOrMore,
            };
            Option<string?> target = Text(targetOption, targetDescription);
            return Ops(stage, "camera " + stage, "Swap, remove or tune the camera's " + (stage == "body" ? "Body (position)" : "Aim (rotation)") + " stage; with nothing, show it, its fields and the types.",
                [staged, values, target], r =>
                {
                    string[] given = r.GetValue(values) ?? [];
                    string? type = given.Length > 0 && !given[0].Contains('=', StringComparison.Ordinal) ? given[0] : null;
                    string[] pairs = type == null ? given : given[1..];
                    var parameters = new List<(string Name, string? Value)> { ("camera", r.GetValue(staged)), ("type", type), ("n", pairs.Length.ToString(CultureInfo.InvariantCulture)) };
                    parameters.AddRange(pairs.Select((pair, i) => ("a" + i.ToString(CultureInfo.InvariantCulture), (string?)pair)));
                    parameters.Add((targetOption[2..], r.GetValue(target)));
                    return Request("/camera/" + stage, r.GetValue(staged), [.. parameters]);
                });
        }

        static OpRequest Request(string path, string? edit, params (string Name, string? Value)[] parameters) =>
            new(Query(path, parameters), edit) { Camera = true };

        static Argument<string> Required(string name, string description) => new(name) { Description = description };
        static Argument<string?> Optional(string name, string description) => new(name) { Description = description, Arity = ArgumentArity.ZeroOrOne };
        static Option<string?> Text(string name, string description) => new(name) { Description = description };
        static Option<bool> Flag(string name, string description) => new(name) { Description = description };
        static string? True(bool flag) => flag ? "true" : null;
        static string? Invariant(IFormattable? number) => number?.ToString(null, CultureInfo.InvariantCulture);
    }
}
