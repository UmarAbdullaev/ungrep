using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// `ug shot` viewpoints (SPEC-editor-ops, Viewpoints): a temporary camera placed with `--from`,
    /// `--look` and `--rotation`; the Scene view framed on an object; and contact sheets around a
    /// target (`--angles`, `--orbit`), stitched into one PNG with Texture2D.ReadPixels.
    /// </summary>
    internal static partial class ShotOps
    {
        public const int MaxSheet = 4096;
        public const float PointDistance = 10f;

        private const int Gap = 2;
        private const int DefaultAzimuths = 8;
        private const int MaxAzimuths = 36;
        private const int MaxElevations = 6;

        // The parameters that ask for a temporary camera or the framed Scene view; a plain shot has none of them.
        private static readonly string[] _viewOptions = { "from", "look", "rotation", "fov", "frame", "keep", "angles", "orbit", "elevations", "distance", "camera" };

        private static readonly float[] _defaultElevations = { 0, 30, -15 };
        private static readonly Color32 _gapColor = new Color32(32, 32, 32, 255);

        private struct Pose
        {
            public Vector3 Position;
            public Quaternion Rotation;
        }

        // What a sheet circles or a viewpoint looks at: an object's centre and size, or a point (no size).
        private sealed class Target
        {
            public GameObject Object;
            public Vector3 Center;
            public float Radius;
            public bool Sized;
            public string Label;

            public Vector3 Front
            {
                get
                {
                    Vector3 forward = Object == null ? Vector3.forward : Vector3.ProjectOnPlane(Object.transform.forward, Vector3.up);
                    return forward.sqrMagnitude < 1e-4f ? Vector3.forward : forward.normalized;
                }
            }
        }

        private static Reply Views(Request request)
        {
            string view = Value(request, "view");
            string from = Value(request, "from");
            string look = Value(request, "look");
            string rotation = Value(request, "rotation");
            string frame = Value(request, "frame");
            string elevations = Value(request, "elevations");
            string distance = Value(request, "distance");
            string fov = Value(request, "fov");
            string cameraName = Value(request, "camera");
            bool angles = request.Get("angles") == "true";
            bool orbit = request.Get("orbit") != null;
            bool keep = request.Get("keep") == "true";
            bool sheet = angles || orbit;

            if (view == "scene")
            {
                if (from != null || look != null || rotation != null || sheet || fov != null || cameraName != null || elevations != null || distance != null)
                {
                    return Answer.Error("the Scene view moves only with --frame: drop scene for a viewpoint (ug shot --from x,y,z --look <object>) or a sheet (ug shot <object> --angles)");
                }

                return frame == null ? Answer.Error("--keep goes with --frame: ug shot scene --frame <object> --keep") : Frame(request, frame, keep);
            }

            string refusal = frame != null ? "--frame frames the Scene view: ug shot scene --frame " + frame
                : keep ? "--keep goes with ug shot scene --frame <object>"
                : angles && orbit ? "--angles and --orbit are two kinds of sheet: give one"
                : !orbit && elevations != null ? "--elevations goes with --orbit"
                : !sheet && distance != null ? "--distance goes with --angles or --orbit; place a single shot with --from"
                : sheet && (from != null || look != null || rotation != null) ? "--from, --look and --rotation place one shot; a sheet circles its target: ug shot <object> --angles"
                : look != null && rotation != null ? "--look and --rotation both aim the camera: give one"
                : sheet && (view == null || view == "game") ? (angles ? "--angles" : "--orbit") + " needs an object or a point x,y,z to circle: ug shot Player " + (angles ? "--angles" : "--orbit")
                : null;
            if (refusal != null)
            {
                return Answer.Error(refusal);
            }

            int height = sheet ? (angles ? 240 : 180) : DefaultHeight;
            float? fieldOfView = null;
            float? fixedDistance = null;
            Camera source = null;
            Reply refused = Size(request.Get("width"), sheet ? (angles ? 320 : 240) : DefaultWidth, "width", out int width);
            refused = refused ?? Size(request.Get("height"), height, "height", out height);
            refused = refused ?? Number(fov, "--fov", 1, 179, out fieldOfView);
            refused = refused ?? Number(distance, "--distance", 0.001f, 1e6f, out fixedDistance);
            refused = refused ?? Source(cameraName, sheet ? null : view, out source);
            if (refused != null)
            {
                return refused;
            }

            return sheet
                ? Sheet(request, view, angles, elevations, width, height, fieldOfView, fixedDistance, source)
                : Viewpoint(from, look, rotation, width, height, fieldOfView, source);
        }

        // `--from`, `--look`, `--rotation`: one picture from a temporary camera.
        private static Reply Viewpoint(string from, string look, string rotation, int width, int height, float? fov, Camera source)
        {
            Vector3? euler = null;
            Reply refused = Vector(from, "--from", out Vector3? position);
            refused = refused ?? Vector(rotation, "--rotation", out euler);
            Target target = null;
            if (refused == null && look != null)
            {
                target = Aim(look, out string error);
                refused = target == null ? Answer.Error(error) : null;
            }

            if (refused != null)
            {
                return refused;
            }

            // --look alone keeps the source's position; with no source it is the target's three-quarter view.
            Vector3 place = position ?? (source != null ? source.transform.position
                : target != null ? Around(target, 45, 30, Fit(target, FieldOfView(source, fov), width / (float)height, false)).Position
                : Vector3.zero);
            if (target != null && (target.Center - place).sqrMagnitude < 1e-6f)
            {
                return Answer.Error("the camera would stand on the point it looks at (" + Values.Numbers(place.x, place.y, place.z) + "): give --from somewhere else");
            }

            if (NoGraphics() is Reply none)
            {
                return none;
            }

            using (var shot = new ShotCamera(source))
            {
                Camera camera = shot.Camera;
                camera.aspect = width / (float)height;
                if (fov.HasValue)
                {
                    camera.fieldOfView = fov.Value;
                }

                Quaternion turn = euler.HasValue ? Quaternion.Euler(euler.Value)
                    : target != null ? Facing(target.Center - place, Vector3.forward)
                    : camera.transform.rotation;
                camera.transform.SetPositionAndRotation(place, turn);
                string what = "from " + Values.Numbers(place.x, place.y, place.z);
                if (target != null)
                {
                    Clip(camera, (target.Center - place).magnitude, target.Sized ? target.Radius : 1f);
                    what += " looking at " + target.Label;
                }
                else if (euler.HasValue)
                {
                    what += ", rotation " + Values.Numbers(euler.Value.x, euler.Value.y, euler.Value.z);
                }

                return Render(new List<Camera> { camera }, width, height, "view", what + ", " + ShotCamera.Settings(source, shot), false);
            }
        }

        // `--angles` and `--orbit`: tiles around a target from a temporary camera, in one PNG.
        private static Reply Sheet(Request request, string view, bool angles, string elevationText, int width, int height, float? fov, float? fixedDistance, Camera source)
        {
            int azimuths = DefaultAzimuths;
            float[] elevations = _defaultElevations;
            Reply refused = angles ? null : Azimuths(request.Get("orbit"), out azimuths);
            refused = refused ?? (angles || elevationText == null ? null : Elevations(elevationText, out elevations));
            if (refused != null)
            {
                return refused;
            }

            Target target = Aim(view, out string error);
            if (target == null)
            {
                return Answer.Error(error);
            }

            int columns = angles ? 3 : azimuths;
            int rows = angles ? 2 : elevations.Length;
            int sheetWidth = columns * width + (columns - 1) * Gap;
            int sheetHeight = rows * height + (rows - 1) * Gap;
            if (sheetWidth > MaxSheet || sheetHeight > MaxSheet)
            {
                int fitWidth = (MaxSheet - (columns - 1) * Gap) / columns;
                int fitHeight = (MaxSheet - (rows - 1) * Gap) / rows;
                string fits = sheetWidth > MaxSheet ? (fitWidth >= MinSize ? "--width " + fitWidth + " or less" : "fewer azimuths")
                    : (fitHeight >= MinSize ? "--height " + fitHeight + " or less" : "fewer elevations");
                return Answer.Error("the sheet would be " + sheetWidth + "x" + sheetHeight + ", over " + MaxSheet + " pixels a side: " + fits);
            }

            float aspect = width / (float)height;
            bool orthographic = source != null && source.orthographic;
            float distance = fixedDistance ?? (target.Sized ? Fit(target, FieldOfView(source, fov), aspect, orthographic) : PointDistance);
            Vector3 front = target.Front;
            var poses = new List<Pose>();
            List<string> legend;
            string layout;
            if (angles)
            {
                poses.Add(Around(target, 0, 0, distance));
                poses.Add(Around(target, 180, 0, distance));
                poses.Add(Around(target, 270, 0, distance));
                poses.Add(Around(target, 90, 0, distance));
                poses.Add(new Pose { Position = target.Center + Vector3.up * distance, Rotation = Facing(Vector3.down, front) });
                poses.Add(Around(target, 45, 30, distance));
                layout = "6 views of " + width + "x" + height;
                legend = new List<string> { "  rows: front, back, left | right, top, three-quarter" };
            }
            else
            {
                float step = 360f / azimuths;
                foreach (float elevation in elevations)
                {
                    for (int i = 0; i < azimuths; i++)
                    {
                        poses.Add(Around(target, i * step, elevation, distance));
                    }
                }

                layout = Answer.Count(azimuths, "azimuth", "azimuths") + " x " + Answer.Count(elevations.Length, "elevation", "elevations") + " of " + width + "x" + height;
                legend = new List<string>
                {
                    "  columns: azimuth " + string.Join(", ", Enumerable.Range(0, azimuths).Select(i => Degrees(i * step)))
                        + " (0 = front, 90 = right); rows: elevation " + string.Join(", ", elevations.Select(Degrees)),
                };
            }

            if (NoGraphics() is Reply none)
            {
                return none;
            }

            using (var shot = new ShotCamera(source))
            {
                Camera camera = shot.Camera;
                camera.aspect = aspect;
                if (fov.HasValue)
                {
                    camera.fieldOfView = fov.Value;
                }

                if (orthographic && target.Sized)
                {
                    camera.orthographicSize = target.Radius * 1.05f * Mathf.Max(1f, 1f / aspect);
                }

                Clip(camera, distance, target.Sized ? target.Radius : 1f);
                string what = target.Label + ", " + layout + ", distance " + Values.Number(Mathf.Round(distance * 10) / 10) + ", " + ShotCamera.Settings(source, shot);
                string name = Name(angles ? "angles-" : "orbit-", view);
                return RenderSheet(camera, poses, columns, rows, width, height, name, what, legend);
            }
        }

        // `ug shot scene --frame <object>`: the Scene view's camera posed as F would frame the object, everything put back after.
        private static Reply Frame(Request request, string address, bool keep)
        {
            int height = DefaultHeight;
            Reply refused = Size(request.Get("width"), DefaultWidth, "width", out int width);
            refused = refused ?? Size(request.Get("height"), height, "height", out height);
            if (refused != null)
            {
                return refused;
            }

            Target target = Aim(address, out string error);
            if (target == null || target.Object == null)
            {
                return Answer.Error(target == null ? error : "--frame takes an object, not a point: ug shot scene --frame Player");
            }

            SceneView sceneView = OpenSceneView();
            if (sceneView == null)
            {
                return Answer.Error(NoSceneView);
            }

            if (NoGraphics() is Reply none)
            {
                return none;
            }

            Camera camera = sceneView.camera;
            Transform held = camera.transform;
            Vector3 pivot = sceneView.pivot;
            Quaternion rotation = sceneView.rotation;
            float size = sceneView.size;
            Vector3 position = held.position;
            Quaternion turn = held.rotation;
            float near = camera.nearClipPlane;
            float far = camera.farClipPlane;
            float orthographicSize = camera.orthographicSize;
            try
            {
                float radius = target.Sized ? target.Radius : 0.5f;
                sceneView.Frame(new Bounds(target.Center, Vector3.one * radius * 2), true);
                held.SetPositionAndRotation(sceneView.pivot - sceneView.rotation * Vector3.forward * sceneView.cameraDistance, sceneView.rotation);
                if (camera.orthographic)
                {
                    camera.orthographicSize = radius * 1.05f * Mathf.Max(1f, height / (float)width);
                }

                Clip(camera, sceneView.cameraDistance, radius);
                return Render(new List<Camera> { camera }, width, height, "scene", "Scene view framed on " + target.Label + (keep ? ", left framed" : ""), false);
            }
            finally
            {
                held.SetPositionAndRotation(position, turn);
                camera.nearClipPlane = near;
                camera.farClipPlane = far;
                camera.orthographicSize = orthographicSize;
                if (!keep)
                {
                    sceneView.LookAtDirect(pivot, rotation, size);
                }

                sceneView.Repaint();
            }
        }

        // Each tile renders into one temporary texture and is read into the sheet at its place; row 1 is the top.
        private static Reply RenderSheet(Camera camera, List<Pose> poses, int columns, int rows, int width, int height, string name, string what, List<string> legend)
        {
            int sheetWidth = columns * width + (columns - 1) * Gap;
            int sheetHeight = rows * height + (rows - 1) * Gap;
            RenderTexture texture = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture previous = RenderTexture.active;
            var sheet = new Texture2D(sheetWidth, sheetHeight, TextureFormat.RGB24, false);
            try
            {
                var fill = new Color32[sheetWidth * sheetHeight];
                for (int i = 0; i < fill.Length; i++)
                {
                    fill[i] = _gapColor;
                }

                sheet.SetPixels32(fill);
                camera.targetTexture = texture;
                for (int i = 0; i < poses.Count; i++)
                {
                    camera.transform.SetPositionAndRotation(poses[i].Position, poses[i].Rotation);
                    camera.Render();
                    RenderTexture.active = texture;
                    sheet.ReadPixels(new Rect(0, 0, width, height), i % columns * (width + Gap), (rows - 1 - i / columns) * (height + Gap));
                }

                sheet.Apply();
                return Answer.Extend(Save(sheet, name, what), legend);
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(texture);
                UnityEngine.Object.DestroyImmediate(sheet);
            }
        }

        // The camera whose settings the temporary camera copies: --camera, the view's camera, the main camera, the last drawn.
        private static Reply Source(string option, string view, out Camera source)
        {
            source = null;
            Camera named = null;
            string error;
            if (option != null && (named = CameraAt(option, out error)) == null)
            {
                return Answer.Error(error);
            }

            if (view != null && view != "game")
            {
                Camera viewed = CameraAt(view, out error);
                if (viewed == null)
                {
                    return Answer.Error(error);
                }

                if (named != null && named != viewed)
                {
                    return Answer.Error("two cameras: " + view + " and --camera " + option + ": give one");
                }

                named = viewed;
            }

            source = named != null ? named : Camera.main != null ? Camera.main : Cameras().LastOrDefault();
            return null;
        }

        // An object in a loaded scene or a point x,y,z; null with why not.
        private static Target Aim(string text, out string error)
        {
            error = null;
            if (Values.TryNumbers(text, 3, out float[] n))
            {
                var point = new Vector3(n[0], n[1], n[2]);
                return new Target { Center = point, Label = "point " + Values.Numbers(n) };
            }

            GameObject found = Addresses.Find(text, out error);
            if (found == null)
            {
                return null;
            }

            if (EditorUtility.IsPersistent(found))
            {
                error = text.Trim() + " is in a prefab asset, not in a loaded scene: place it first (ug object create --prefab)";
                return null;
            }

            var target = new Target { Object = found, Label = Addresses.Of(found), Center = found.transform.position };
            if (Measure(found, out Bounds bounds))
            {
                target.Center = bounds.center;
                target.Radius = bounds.extents.magnitude;
                target.Sized = true;
            }

            return target;
        }

        // The bounds of the object's enabled renderers, else of its colliders; false when it has neither, or they have no size.
        private static bool Measure(GameObject target, out Bounds bounds)
        {
            bool found = Encapsulate(target.GetComponentsInChildren<Renderer>().Where(r => r.enabled).Select(r => r.bounds), out bounds)
                || Encapsulate(target.GetComponentsInChildren<Collider>().Where(c => c.enabled).Select(c => c.bounds)
                    .Concat(target.GetComponentsInChildren<Collider2D>().Where(c => c.enabled).Select(c => c.bounds)), out bounds);
            return found && bounds.extents.sqrMagnitude > 1e-8f;
        }

        private static bool Encapsulate(IEnumerable<Bounds> all, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (Bounds b in all)
            {
                if (any)
                {
                    bounds.Encapsulate(b);
                }
                else
                {
                    bounds = b;
                    any = true;
                }
            }

            return any;
        }

        // How far the camera stands so the target's bounding sphere fills the narrower field of view.
        private static float Fit(Target target, float verticalFov, float aspect, bool orthographic)
        {
            if (!target.Sized)
            {
                return PointDistance;
            }

            if (orthographic)
            {
                return target.Radius * 3;
            }

            float vertical = verticalFov * Mathf.Deg2Rad;
            float horizontal = 2 * Mathf.Atan(Mathf.Tan(vertical / 2) * aspect);
            return target.Radius / Mathf.Sin(Mathf.Min(vertical, horizontal) / 2) * 1.05f;
        }

        private static float FieldOfView(Camera source, float? fov) => fov ?? (source != null ? source.fieldOfView : 60f);

        // A camera on the target's sphere: azimuth from its front toward its right, elevation up from the horizontal.
        private static Pose Around(Target target, float azimuth, float elevation, float distance)
        {
            Vector3 front = target.Front;
            Vector3 right = Vector3.Cross(Vector3.up, front);
            float a = azimuth * Mathf.Deg2Rad;
            float e = elevation * Mathf.Deg2Rad;
            Vector3 direction = (Mathf.Cos(e) * (Mathf.Cos(a) * front + Mathf.Sin(a) * right) + Mathf.Sin(e) * Vector3.up).normalized;
            return new Pose { Position = target.Center + direction * distance, Rotation = Facing(-direction, front) };
        }

        // Looking along a direction with the world up, or with `front` up when looking straight up or down.
        private static Quaternion Facing(Vector3 direction, Vector3 front) =>
            Mathf.Abs(Vector3.Dot(direction.normalized, Vector3.up)) > 0.999f ? Quaternion.LookRotation(direction, front) : Quaternion.LookRotation(direction, Vector3.up);

        // Near plane in, far plane out, so a target `radius` big at `distance` is never cut.
        private static void Clip(Camera camera, float distance, float radius)
        {
            camera.nearClipPlane = Mathf.Clamp((distance - radius) * 0.5f, 0.01f, Mathf.Max(0.01f, camera.nearClipPlane));
            camera.farClipPlane = Mathf.Max(camera.farClipPlane, distance + radius * 2);
        }

        private static string Value(Request request, string name) => string.IsNullOrWhiteSpace(request.Get(name)) ? null : request.Get(name).Trim();

        private static Reply Vector(string text, string option, out Vector3? vector)
        {
            vector = null;
            if (text == null)
            {
                return null;
            }

            if (!Values.TryNumbers(text, 3, out float[] n))
            {
                return Answer.Error(option + " needs three numbers x,y,z, not " + text);
            }

            vector = new Vector3(n[0], n[1], n[2]);
            return null;
        }

        private static Reply Number(string text, string option, float min, float max, out float? value)
        {
            value = null;
            if (text == null)
            {
                return null;
            }

            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) || number < min || number > max || float.IsNaN(number))
            {
                return Answer.Error(option + " must be a number from " + Values.Number(min) + " to " + Values.Number(max) + ", not " + text);
            }

            value = number;
            return null;
        }

        private static Reply Azimuths(string text, out int count)
        {
            count = DefaultAzimuths;
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out count) && count >= 1 && count <= MaxAzimuths
                ? null
                : Answer.Error("--orbit takes a number of azimuths from 1 to " + MaxAzimuths + ", not " + text);
        }

        private static Reply Elevations(string text, out float[] elevations)
        {
            elevations = null;
            string[] parts = text.Split(',');
            var values = new List<float>();
            foreach (string part in parts)
            {
                if (!float.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float angle) || angle < -89 || angle > 89)
                {
                    values = null;
                    break;
                }

                values.Add(angle);
            }

            if (values == null || values.Count == 0 || values.Count > MaxElevations)
            {
                return Answer.Error("--elevations takes 1 to " + MaxElevations + " angles from -89 to 89, like 0,30,-15, not " + text);
            }

            elevations = values.ToArray();
            return null;
        }

        private static string Degrees(float angle) => angle.ToString("0.#", CultureInfo.InvariantCulture);
    }
}
