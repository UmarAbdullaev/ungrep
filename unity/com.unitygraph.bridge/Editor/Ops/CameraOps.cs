using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityGraph.Bridge
{
    /// <summary>The Cinemachine 3 side of `ug camera`, set by the Cinemachine assembly when com.unity.cinemachine 3.x is present.</summary>
    internal interface ICinemachineRig
    {
        /// <summary>One line per Cinemachine camera in the loaded scenes, highest priority first.</summary>
        List<string> CameraLines();

        /// <summary>The line of an object holding a Cinemachine camera, else null; without <paramref name="status"/> no live, standby or disabled (before the brain has run).</summary>
        string CameraLine(GameObject target, bool status);

        /// <summary>Whether the Unity camera holds a CinemachineBrain.</summary>
        bool HasBrain(Camera camera);

        /// <summary>One live line per active brain; each brain's output camera goes into <paramref name="outputs"/>.</summary>
        List<string> LiveLines(HashSet<Camera> outputs);

        /// <summary>Builds a preset on a new, placed object; lines worth telling go to <paramref name="notes"/>. Null, or the error.</summary>
        string Build(GameObject made, CameraPreset preset, List<string> notes);

        /// <summary>Sets a Cinemachine camera's priority; null when the object holds none.</summary>
        Reply Priority(GameObject target, int priority);

        /// <summary>`ug camera body` (or `aim`): shows, swaps, removes or tunes the stage's component, and sets its target.</summary>
        Reply Stage(GameObject target, Request request, bool aim);

        Reply Noise(GameObject target, Request request);

        Reply Blend(Request request);

        Reply Force(GameObject target, Request request);

        Reply Release(Request request);

        /// <summary>`ug camera brain`: a brain on the camera (null: the main camera), then its settings.</summary>
        Reply Brain(GameObject target, Request request);

        /// <summary>Forgets the forced cameras: a play mode change clears every brain's overrides.</summary>
        void Forget();
    }

    /// <summary>What `ug camera create` asks of a Cinemachine preset (specs/SPEC-camera.md, Presets).</summary>
    internal sealed class CameraPreset
    {
        public string Name { get; set; }

        public GameObject Follow { get; set; }

        public GameObject LookAt { get; set; }

        public int? Priority { get; set; }

        public float? FieldOfView { get; set; }

        /// <summary>The main camera when the request came, else the only enabled one: for the place, the lens and the brain.</summary>
        public Camera Main { get; set; }
    }

    /// <summary>
    /// `ug camera` (specs/SPEC-camera.md): plain Unity cameras, the refusals and the wait for the
    /// brain live here; Cinemachine 3 goes through <see cref="Rig"/>. Each edit is one undo group,
    /// reverted when it fails, and stays unsaved. Every answer but `list` comes after the brain has
    /// picked its camera and ends with the live lines.
    /// </summary>
    internal static class CameraOps
    {
        private const int MaxLines = 30;
        private const int SettleFrames = 2;
        private const double SettleSeconds = 2;
        private const string Paused = "  (paused: the brain picks on the next frame)";
        private const string PrefabRefusal = "ug camera works on the loaded scenes: ug set and ug component edit a prefab's camera";

        private static readonly string[] _presets = { "static", "follow", "third-person", "freelook", "dolly", "top-down", "side-scroller", "plain" };

        // What body, aim, noise, blend and brain requests set; one with none of them only shows.
        private static readonly string[] _settings = { "type", "follow", "look-at", "profile", "amplitude", "frequency", "style", "time", "from", "to" };

        internal static ICinemachineRig Rig { get; set; }

        public static Reply List(Request request)
        {
            List<string> cinemachine = Rig?.CameraLines() ?? new List<string>();
            List<Camera> cameras = UnityCameras();
            List<string> all = cinemachine.Concat(cameras.Select(PlainLine)).ToList();
            bool everything = request.Get("all") == "true";
            var lines = new List<string> { Header(cinemachine.Count, cameras.Count) };
            lines.AddRange(everything ? all : all.Take(MaxLines));
            if (!everything && all.Count > MaxLines)
            {
                lines.Add("... " + (all.Count - MaxLines).ToString(CultureInfo.InvariantCulture) + " more: ug camera list --all");
            }

            lines.AddRange(LiveLines(false));
            return Answer.Lines(lines);
        }

        public static Func<Reply> Create(Request request) => Settled(() => MakeCamera(request));

        public static Func<Reply> Priority(Request request) => Settled(() => SetPriority(request));

        public static Func<Reply> Body(Request request) => Settled(() => OnCamera(request, "body", target => Rig.Stage(target, request, false)));

        public static Func<Reply> Aim(Request request) => Settled(() => OnCamera(request, "aim", target => Rig.Stage(target, request, true)));

        public static Func<Reply> Noise(Request request) => Settled(() => OnCamera(request, "noise", target => Rig.Noise(target, request)));

        public static Func<Reply> Blend(Request request) =>
            Settled(() => NeedCinemachine("ug camera blend") ?? (Shows(request) ? Rig.Blend(request) : Edit("blend", () => Rig.Blend(request))));

        // Forcing changes no data, so it is no undo step.
        public static Func<Reply> Force(Request request) => Settled(() =>
        {
            Reply refused = NeedCinemachine("ug camera force");
            if (refused != null)
            {
                return refused;
            }

            GameObject target = Target(request.Get("camera"), out string error);
            return target == null ? Answer.Error(error) : Rig.Force(target, request);
        });

        public static Func<Reply> Release(Request request) => Settled(() => NeedCinemachine("ug camera release") ?? Rig.Release(request));

        public static Func<Reply> Brain(Request request) => Settled(() =>
        {
            Reply refused = NeedCinemachine("ug camera brain");
            if (refused != null)
            {
                return refused;
            }

            GameObject target = null;
            string error = null;
            if (Value(request, "camera") != null && (target = Target(request.Get("camera"), out error)) == null)
            {
                return Answer.Error(error);
            }

            Camera holder = target != null ? target.GetComponent<Camera>() : Camera.main != null ? Camera.main : OnlyEnabledCamera();
            return Shows(request) && holder != null && Rig.HasBrain(holder) ? Rig.Brain(target, request) : Edit("brain", () => Rig.Brain(target, request));
        });

        public static void OnPlayModeChanged() => Rig?.Forget();

        /// <summary>A camera's object in the loaded scenes, or null with the error; prefab assets are refused.</summary>
        internal static GameObject Target(string address, out string error)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                error = "no camera given: ug camera lists them";
                return null;
            }

            if (Addresses.IsAssetPath(address.Trim()))
            {
                error = PrefabRefusal;
                return null;
            }

            return Addresses.Find(address, out error);
        }

        /// <summary>An option's trimmed text, or null when it is absent or blank.</summary>
        internal static string Value(Request request, string name) =>
            string.IsNullOrWhiteSpace(request.Get(name)) ? null : request.Get(name).Trim();

        /// <summary>An optional number option in min..max; false with the error when it is something else.</summary>
        internal static bool Number(Request request, string name, float min, float max, out float? value, out Reply error)
        {
            value = null;
            error = null;
            string text = Value(request, name);
            if (text == null)
            {
                return true;
            }

            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) || float.IsNaN(number) || number < min || number > max)
            {
                error = Answer.Error("--" + name + " must be a number from " + Values.Number(min) + " to " + Values.Number(max) + ", not " + text);
                return false;
            }

            value = number;
            return true;
        }

        /// <summary>The `field=value` arguments (`n`, `a0`, `a1`, ...); false with the error for one without `=`.</summary>
        internal static bool Pairs(Request request, out List<KeyValuePair<string, string>> pairs, out string error)
        {
            pairs = new List<KeyValuePair<string, string>>();
            error = null;
            int.TryParse(request.Get("n"), NumberStyles.None, CultureInfo.InvariantCulture, out int n);
            for (int i = 0; i < n; i++)
            {
                string pair = request.Get("a" + i.ToString(CultureInfo.InvariantCulture)) ?? "";
                int equals = pair.IndexOf('=');
                if (equals <= 0)
                {
                    error = "not field=value: " + pair + " (like CameraDistance=5)";
                    return false;
                }

                pairs.Add(new KeyValuePair<string, string>(pair.Substring(0, equals).Trim(), pair.Substring(equals + 1)));
            }

            return true;
        }

        /// <summary>The live lines that end every answer: each brain's camera, then the camera drawing display 1 when no brain drives it.</summary>
        internal static List<string> LiveLines(bool paused)
        {
            var outputs = new HashSet<Camera>();
            List<string> lines = Rig?.LiveLines(outputs) ?? new List<string>();
            Camera last = UnityCameras()
                .Where(c => c.isActiveAndEnabled && c.targetTexture == null && c.targetDisplay == 0 && c.cameraType == CameraType.Game)
                .OrderBy(c => c.depth)
                .LastOrDefault();
            if (last == null)
            {
                if (lines.Count == 0)
                {
                    lines.Add("live: no camera draws display 1");
                }
            }
            else if (!outputs.Contains(last))
            {
                lines.Add("live: " + Addresses.Of(last.gameObject) + " (Camera, depth " + Round(last.depth) + ", draws last on display 1)");
            }

            if (paused && lines.Count > 0)
            {
                lines[lines.Count - 1] += Paused;
            }

            return lines;
        }

        /// <summary>The enabled camera when there is only one, else null.</summary>
        internal static Camera OnlyEnabledCamera()
        {
            List<Camera> enabled = UnityCameras().Where(c => c.isActiveAndEnabled).ToList();
            return enabled.Count == 1 ? enabled[0] : null;
        }

        internal static string Round(float value) => Values.Number((float)Math.Round(value, 2));

        /// <summary>Why ug camera cannot drive this Cinemachine: not 3.x, or 3.x whose bridge part did not compile.</summary>
        internal static string Unsupported(string version) =>
            version.StartsWith("3.", StringComparison.Ordinal)
                ? "Cinemachine " + version + " is installed but the bridge's Cinemachine part did not compile: ug compile shows why"
                : "Cinemachine " + version + " is installed; ug camera knows Cinemachine 3.x only: ug set and ug component edit " + version.Split('.')[0] + ".x cameras";

        // Answers once the brain has run: a brain picks its camera in LateUpdate, so the edit is
        // followed by queued player-loop frames (edit mode runs them too), then the live lines.
        private static Func<Reply> Settled(Func<Reply> edit)
        {
            Reply reply = edit();
            if (reply.Lines == null)
            {
                return () => reply;
            }

            int start = Time.frameCount;
            double deadline = EditorApplication.timeSinceStartup + SettleSeconds;
            return () =>
            {
                bool paused = EditorApplication.isPlaying && EditorApplication.isPaused;
                if (!paused && Time.frameCount < start + SettleFrames && EditorApplication.timeSinceStartup < deadline)
                {
                    EditorApplication.QueuePlayerLoopUpdate();
                    return null;
                }

                return Answer.Extend(reply, LiveLines(paused));
            };
        }

        // A Cinemachine edit of one camera.
        private static Reply OnCamera(Request request, string action, Func<GameObject, Reply> edit)
        {
            Reply refused = NeedCinemachine("ug camera " + action);
            if (refused != null)
            {
                return refused;
            }

            GameObject target = Target(request.Get("camera"), out string error);
            return target == null ? Answer.Error(error) : Shows(request) ? edit(target) : Edit(action, () => edit(target));
        }

        // One undo group named `ug camera <action>`; a failed edit leaves nothing behind.
        private static Reply Edit(string action, Func<Reply> edit) => Answer.Undoable("camera " + action, edit);

        // A request that sets nothing opens no undo group: an empty one would list under its name.
        private static bool Shows(Request request) => _settings.All(name => Value(request, name) == null) && (Value(request, "n") ?? "0") == "0";

        private static Reply MakeCamera(Request request)
        {
            string name = Value(request, "name");
            if (name != null && name.IndexOf('/') >= 0)
            {
                return Answer.Error(EditOps.SlashName);
            }

            string asked = Value(request, "preset") ?? (Rig != null ? "static" : "plain");
            string preset = _presets.FirstOrDefault(p => Squash(p) == Squash(asked));
            if (preset == null)
            {
                return Answer.Error("no preset " + asked + " (" + string.Join(", ", _presets) + ")");
            }

            string follow = Value(request, "follow");
            string lookAt = Value(request, "look-at");
            string needs = preset != "plain" ? "--preset " + preset : follow != null ? "--follow" : lookAt != null ? "--look-at" : null;
            if (needs != null)
            {
                Reply refused = NeedCinemachine(needs);
                if (refused != null)
                {
                    return refused;
                }

                if (preset == "plain")
                {
                    return Answer.Error(needs + " sets a Cinemachine camera's target: --preset plain makes a Unity camera, which has none");
                }
            }

            if (!Whole(request, "priority", out int? priority, out Reply bad) || !Number(request, "fov", 1, 179, out float? fov, out bad) || !Point(request, out Vector3? at, out bad))
            {
                return bad;
            }

            var setup = new CameraPreset { Name = preset, Priority = priority, FieldOfView = fov, Main = Camera.main != null ? Camera.main : OnlyEnabledCamera() };
            GameObject parent = null;
            string error = null;
            if ((Value(request, "parent") != null && (parent = Target(request.Get("parent"), out error)) == null)
                || (follow != null && (setup.Follow = Target(follow, out error)) == null)
                || (lookAt != null && (setup.LookAt = Target(lookAt, out error)) == null))
            {
                return Answer.Error(error);
            }

            // Where the main camera stands, so making the new camera live does not jump.
            Transform main = setup.Main != null ? setup.Main.transform : null;
            Vector3 position = at ?? (main != null ? main.position : Vector3.zero);
            Quaternion rotation = preset == "top-down" ? Quaternion.Euler(90, 0, 0)
                : preset == "side-scroller" ? Quaternion.identity
                : main != null ? main.rotation : Quaternion.identity;

            return Edit("create", () =>
            {
                var made = new GameObject(name ?? (preset == "plain" ? "Camera" : "CinemachineCamera"));
                if (parent != null)
                {
                    made.transform.SetParent(parent.transform, false);
                }

                made.transform.SetPositionAndRotation(position, rotation);
                GameObjectUtility.EnsureUniqueNameForSibling(made);
                Undo.RegisterCreatedObjectUndo(made, Answer.GroupName);
                var notes = new List<string>();
                if (preset == "plain")
                {
                    Camera camera = made.AddComponent<Camera>();
                    camera.depth = priority ?? camera.depth;
                    camera.fieldOfView = fov ?? camera.fieldOfView;
                }
                else
                {
                    string failed = Rig.Build(made, setup, notes);
                    if (failed != null)
                    {
                        return Answer.Error(failed);
                    }
                }

                var lines = new List<string> { "created " + Addresses.Of(made) + " (" + preset + ")" + Answer.Unsaved(made), CameraLine(made) };
                lines.AddRange(notes);
                return Answer.Lines(lines);
            });
        }

        private static Reply SetPriority(Request request)
        {
            string text = Value(request, "priority");
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int priority))
            {
                return Answer.Error("the priority is a whole number, not " + (text ?? "nothing"));
            }

            GameObject target = Target(request.Get("camera"), out string error);
            if (target == null)
            {
                return Answer.Error(error);
            }

            return Edit("priority", () =>
            {
                Reply set = Rig?.Priority(target, priority);
                if (set != null)
                {
                    return set;
                }

                if (!target.TryGetComponent(out Camera camera))
                {
                    string version = Rig == null ? CinemachineVersion() : null;
                    return Answer.Error(version != null ? Unsupported(version) : Addresses.Of(target) + " is not a camera: ug camera lists them");
                }

                Undo.RegisterCompleteObjectUndo(camera, Answer.GroupName);
                float before = camera.depth;
                camera.depth = priority;
                if (PrefabUtility.IsPartOfPrefabInstance(camera))
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(camera);
                }

                return Answer.Line(Addresses.Of(target) + "  depth " + Round(before) + " -> " + Round(camera.depth) + Answer.Unsaved(target));
            });
        }

        private static string CameraLine(GameObject target) =>
            Rig?.CameraLine(target, false) ?? (target.TryGetComponent(out Camera camera) ? PlainLine(camera) : Addresses.Of(target));

        private static string PlainLine(Camera camera)
        {
            var line = new StringBuilder(Addresses.Of(camera.gameObject)).Append("  Camera  depth ").Append(Round(camera.depth));
            line.Append(camera.orthographic ? "  ortho " + Round(camera.orthographicSize) : "  fov " + Round(camera.fieldOfView));
            if (camera.targetDisplay != 0)
            {
                line.Append("  display ").Append((camera.targetDisplay + 1).ToString(CultureInfo.InvariantCulture));
            }

            if (camera.targetTexture != null)
            {
                string path = AssetDatabase.GetAssetPath(camera.targetTexture);
                line.Append("  texture ").Append(string.IsNullOrEmpty(path) ? camera.targetTexture.name : path);
            }

            if (Rig != null && Rig.HasBrain(camera))
            {
                line.Append("  brain");
            }

            if (!camera.isActiveAndEnabled)
            {
                line.Append("  disabled");
            }

            return line.ToString();
        }

        // Unity cameras in the loaded scenes, drawn last first; hidden ones (ug shot's) are left out.
        private static List<Camera> UnityCameras() =>
            Addresses.LoadedScenes()
                .SelectMany(s => s.GetRootGameObjects())
                .SelectMany(root => root.GetComponentsInChildren<Camera>(true))
                .Where(c => (c.gameObject.hideFlags & HideFlags.HideInHierarchy) == 0)
                .OrderByDescending(c => c.depth)
                .ToList();

        private static string Header(int cinemachine, int cameras)
        {
            string version = CinemachineVersion();
            string counts = Answer.Count(cameras, "camera", "cameras");
            if (Rig != null)
            {
                return "Cinemachine " + (version ?? "3") + "  " + Answer.Count(cinemachine, "Cinemachine camera", "Cinemachine cameras") + ", " + counts;
            }

            return version == null ? "no Cinemachine  " + counts : "Cinemachine " + version + " (ug camera knows 3.x only)  " + counts;
        }

        // Null when Cinemachine 3 is here; else why `what` cannot run.
        private static Reply NeedCinemachine(string what)
        {
            if (Rig != null)
            {
                return null;
            }

            string version = CinemachineVersion();
            return Answer.Error(version == null
                ? what + " needs Cinemachine 3, which is not in this project (ug package add com.unity.cinemachine)"
                : Unsupported(version));
        }

        private static string CinemachineVersion() =>
            UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.unity.cinemachine")?.version;

        // `third_person`, `Third-Person` and `thirdperson` all name third-person.
        private static string Squash(string preset) => preset.Replace("-", "").Replace("_", "").ToLowerInvariant();

        private static bool Whole(Request request, string name, out int? value, out Reply error)
        {
            value = null;
            error = null;
            string text = Value(request, name);
            if (text == null)
            {
                return true;
            }

            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            {
                error = Answer.Error("--" + name + " must be a whole number, not " + text);
                return false;
            }

            value = number;
            return true;
        }

        private static bool Point(Request request, out Vector3? at, out Reply error)
        {
            at = null;
            error = null;
            string text = Value(request, "at");
            if (text == null)
            {
                return true;
            }

            string[] parts = text.Split(',');
            var xyz = new float[3];
            if (parts.Length != 3 || Enumerable.Range(0, 3).Any(i => !float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out xyz[i])))
            {
                error = Answer.Error("--at is x,y,z in world units, like 0,2,-10, not " + text);
                return false;
            }

            at = new Vector3(xyz[0], xyz[1], xyz[2]);
            return true;
        }
    }
}
