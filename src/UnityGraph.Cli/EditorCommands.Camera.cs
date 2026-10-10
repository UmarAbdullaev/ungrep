using System.CommandLine;
using System.Globalization;

namespace UnityGraph.Cli;

/// <summary>
/// `ug shot` (SPEC-editor-ops, Endpoints and Viewpoints): the game view, the scene view or one camera,
/// and with bridge 0.6.0 a temporary camera placed with --from, --look and --rotation, the scene view
/// framed on an object, and contact sheets around a target (--angles, --orbit).
/// </summary>
internal static partial class EditorCommands
{
    // A sheet renders up to 216 tiles: give it longer than one picture.
    private static readonly TimeSpan _sheetTimeout = TimeSpan.FromSeconds(60);

    private static Command ShotCommand()
    {
        var view = new Argument<string?>("view")
        {
            Description = "game (default), scene, or a camera's object address; with --angles or --orbit the object or point x,y,z to circle.",
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
        Option[] cameraOptions = [angles, orbit, frame, from, look, rotation, fov, camera, keep, elevations, distance];
        return Ops("shot", "shot", "Save a picture of the game view, the scene view, one camera, a viewpoint or a sheet of views around an object to Library/unitygraph/shots/ and print its path.",
            [view, width, height, from, look, rotation, fov, camera, frame, keep, angles, orbit, elevations, distance], r =>
            {
                bool sheet = r.GetValue(angles) || r.GetResult(orbit) != null;
                Option? needs = cameraOptions.FirstOrDefault(option => r.GetResult(option) is { Implicit: false });
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
                    ("distance", Text(r.GetValue(distance)))))
                {
                    Camera = needs != null,
                    Option = needs?.Name,
                    Timeout = sheet ? _sheetTimeout : OpTimeout,
                };
            });

        static string? Text(IFormattable? number) => number?.ToString(null, CultureInfo.InvariantCulture);
    }
}
