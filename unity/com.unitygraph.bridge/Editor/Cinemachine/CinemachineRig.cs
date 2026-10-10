using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Unity.Cinemachine;
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// `ug camera` for Cinemachine 3 (specs/SPEC-camera.md): camera and live lines, the presets of
    /// Cinemachine's GameObject menu, the Body, Aim and Noise stages, blends, forcing and the brain.
    /// Edits run inside the undo group CameraOps opens, which is reverted when one fails.
    /// </summary>
    [InitializeOnLoad]
    internal sealed class CinemachineRig : ICinemachineRig
    {
        private const string AnyCamera = "**ANY CAMERA**";
        private const int MaxProfiles = 12;

        // The override id each brain was forced with, until ug camera release or a play mode change.
        private readonly Dictionary<CinemachineBrain, int> _forced = new Dictionary<CinemachineBrain, int>();

        static CinemachineRig()
        {
            CameraOps.Rig = new CinemachineRig();
        }

        public List<string> CameraLines() =>
            Addresses.LoadedScenes()
                .SelectMany(s => s.GetRootGameObjects())
                .SelectMany(root => root.GetComponentsInChildren<CinemachineVirtualCameraBase>(true))
                .OrderByDescending(c => c.Priority.Value)
                .Select(c => Line(c, true))
                .ToList();

        public string CameraLine(GameObject target, bool status) =>
            target.TryGetComponent(out CinemachineVirtualCameraBase camera) ? Line(camera, status) : null;

        public bool HasBrain(Camera camera) => camera.TryGetComponent(out CinemachineBrain _);

        public List<string> LiveLines(HashSet<Camera> outputs)
        {
            var lines = new List<string>();
            for (int i = 0; i < CinemachineBrain.ActiveBrainCount; i++)
            {
                CinemachineBrain brain = CinemachineBrain.GetActiveBrain(i);
                if (brain.OutputCamera != null)
                {
                    outputs.Add(brain.OutputCamera);
                }

                string where = "brain " + Addresses.Of(brain.gameObject) + (_forced.ContainsKey(brain) ? ", forced" : "")
                    + (CinemachineCore.SoloCamera != null ? ", solo from the inspector" : "");
                ICinemachineCamera live = brain.ActiveVirtualCamera;
                if (live == null)
                {
                    lines.Add("live: none (" + where + ": no enabled Cinemachine camera on its channels)");
                    continue;
                }

                string line = "live: " + Name(live) + " (" + where + ")";
                CinemachineBlend blend = brain.IsBlending ? brain.ActiveBlend : null;
                if (blend != null)
                {
                    line += ", blending from " + Name(blend.CamA) + " " + blend.TimeInBlend.ToString("0.0", CultureInfo.InvariantCulture)
                        + " of " + CameraOps.Round(blend.Duration) + " s";
                }

                lines.Add(line);
            }

            return lines;
        }

        public string Build(GameObject made, CameraPreset preset, List<string> notes)
        {
            var camera = Add<CinemachineCamera>(made);
            camera.Lens = preset.Main != null ? LensSettings.FromCamera(preset.Main) : LensSettings.Default;
            if (preset.FieldOfView.HasValue)
            {
                camera.Lens.FieldOfView = preset.FieldOfView.Value;
            }

            if (preset.Priority.HasValue)
            {
                camera.Priority = preset.Priority.Value;
            }

            switch (preset.Name)
            {
                case "follow":
                    Add<CinemachineFollow>(made);
                    Add<CinemachineRotationComposer>(made);
                    break;
                case "third-person":
                    var arm = Add<CinemachineThirdPersonFollow>(made);
                    arm.ShoulderOffset = new Vector3(0.8f, -0.4f, 0f);
                    arm.CameraSide = 1;
                    arm.VerticalArmLength = 1.2f;
                    arm.CameraDistance = 4f;
#if UG_PHYSICS
                    Add<CinemachineThirdPersonAim>(made);
#else
                    notes.Add("no CinemachineThirdPersonAim: it needs the physics module (com.unity.modules.physics)");
#endif
                    break;
                case "freelook":
                    Add<CinemachineOrbitalFollow>(made).OrbitStyle = CinemachineOrbitalFollow.OrbitStyles.ThreeRing;
                    Add<CinemachineRotationComposer>(made);
                    Add<CinemachineInputAxisController>(made);
                    Add<CinemachineFreeLookModifier>(made).enabled = false;
                    break;
                case "dolly":
                    Add<CinemachineRotationComposer>(made);
                    SplineContainer track = DollyTrack(made, notes);
                    Add<CinemachineSplineDolly>(made).Spline = track;
                    break;
                case "top-down":
                case "side-scroller":
                    Add<CinemachinePositionComposer>(made);
                    break;
            }

            // Recorded again after the stage components: without it ug redo brings the camera back with no targets.
            Undo.RegisterCompleteObjectUndo(camera, Answer.GroupName);
            if (preset.Follow != null)
            {
                camera.Target.TrackingTarget = preset.Follow.transform;
            }

            if (preset.LookAt != null)
            {
                camera.Target.CustomLookAtTarget = true;
                camera.Target.LookAtTarget = preset.LookAt.transform;
            }

            EnsureBrain(preset.Main, notes);
            return null;
        }

        public Reply Priority(GameObject target, int priority)
        {
            if (!target.TryGetComponent(out CinemachineVirtualCameraBase camera))
            {
                return null;
            }

            Undo.RegisterCompleteObjectUndo(camera, Answer.GroupName);
            int before = camera.Priority.Value;
            camera.Priority = priority;
            Recorded(camera);
            return Answer.Line(Addresses.Of(target) + "  priority " + Whole(before) + " -> " + Whole(priority) + Answer.Unsaved(target));
        }

        public Reply Stage(GameObject target, Request request, bool aim)
        {
            string what = aim ? "aim" : "body";
            CinemachineCore.Stage stage = aim ? CinemachineCore.Stage.Aim : CinemachineCore.Stage.Body;
            if (!Pipeline(target, what, out CinemachineCamera camera, out Reply refused))
            {
                return refused;
            }

            if (!CameraOps.Pairs(request, out List<KeyValuePair<string, string>> pairs, out string error))
            {
                return Answer.Error(error);
            }

            string address = Addresses.Of(target);
            string type = CameraOps.Value(request, "type");
            string aimAt = CameraOps.Value(request, aim ? "look-at" : "follow");
            List<Type> types = StageTypes(stage);
            CinemachineComponentBase current = StageComponent(camera, stage);
            if (type == null && pairs.Count == 0 && aimAt == null)
            {
                string targets = aim ? "look-at " + LookAtText(camera) : "follow " + Shown(camera.Target.TrackingTarget);
                var shown = new List<string> { address + "  " + what + " " + Short(current) + "  " + targets + "  (" + what + " types: " + string.Join(", ", types.Select(Short)) + ")" };
                if (current != null)
                {
                    shown.AddRange(ReadOps.Fields(current, Addresses.ComponentName(current)));
                }

                return Answer.Lines(shown);
            }

            var lines = new List<string>();
            if (type != null)
            {
                Type wanted = null;
                if (!string.Equals(type, "none", StringComparison.OrdinalIgnoreCase))
                {
                    wanted = types.FirstOrDefault(t => string.Equals(t.Name, type, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(t.Name, "Cinemachine" + type, StringComparison.OrdinalIgnoreCase));
                    if (wanted == null)
                    {
                        return Answer.Error("no " + what + " type " + type + " (" + string.Join(", ", types.Select(Short)) + ")");
                    }
                }

                if (current?.GetType() == wanted)
                {
                    lines.Add(address + "  " + what + " " + Short(current) + " (unchanged)");
                }
                else
                {
                    string before = Short(current);
                    foreach (CinemachineComponentBase old in target.GetComponents<CinemachineComponentBase>().Where(c => c.Stage == stage))
                    {
                        string name = Addresses.ComponentName(old);
                        Undo.DestroyObjectImmediate(old);
                        if (old != null)
                        {
                            return Answer.Error("Unity kept " + name + " on " + address + ": ug console says why");
                        }
                    }

                    if (wanted != null && Add(target, wanted) == null)
                    {
                        return Answer.Error("Unity did not add " + wanted.Name + " to " + address + ": ug console says why");
                    }

                    lines.Add(address + "  " + what + " " + before + " -> " + (wanted == null ? "none" : Short(wanted)) + Answer.Unsaved(target));
                }
            }

            if (pairs.Count > 0)
            {
                CinemachineComponentBase component = StageComponent(camera, stage);
                if (component == null)
                {
                    return Answer.Error(address + " has no " + what + " to set fields on: ug camera " + what + " " + address + " <type> adds one ("
                        + string.Join(", ", types.Select(Short)) + ")");
                }

                foreach (KeyValuePair<string, string> pair in pairs)
                {
                    Reply set = EditOps.SetSerialized(component, pair.Key, pair.Value, address + "  " + Addresses.ComponentName(component) + ".");
                    if (set.Lines == null)
                    {
                        return set;
                    }

                    lines.AddRange(set.Lines);
                }
            }

            if (aimAt != null)
            {
                string line = SetTarget(camera, aimAt, aim, out error);
                if (line == null)
                {
                    return Answer.Error(error);
                }

                lines.Add(line);
            }

            Named(camera);
            return Answer.Lines(lines);
        }

        public Reply Noise(GameObject target, Request request)
        {
            if (!Pipeline(target, "noise", out CinemachineCamera camera, out Reply refused))
            {
                return refused;
            }

            if (!CameraOps.Number(request, "amplitude", 0, 1000, out float? amplitude, out Reply bad) || !CameraOps.Number(request, "frequency", 0, 1000, out float? frequency, out bad))
            {
                return bad;
            }

            string address = Addresses.Of(target);
            string asked = CameraOps.Value(request, "profile");
            List<NoiseSettings> profiles = Profiles();
            target.TryGetComponent(out CinemachineBasicMultiChannelPerlin perlin);
            if (asked == null && amplitude == null && frequency == null)
            {
                var shown = new List<string> { address + "  noise " + NoiseText(perlin) };
                List<string> names = profiles.Select(p => p.name).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                shown.Add("profiles: " + string.Join(", ", names.Take(MaxProfiles))
                    + (names.Count > MaxProfiles ? " ... " + (names.Count - MaxProfiles).ToString(CultureInfo.InvariantCulture) + " more (or any NoiseSettings asset path)" : ""));
                return Answer.Lines(shown);
            }

            CinemachineComponentBase other = StageComponent(camera, CinemachineCore.Stage.Noise);
            if (perlin == null && other != null)
            {
                return Answer.Error(address + "'s noise is " + Addresses.ComponentName(other) + ": ug camera noise sets CinemachineBasicMultiChannelPerlin; ug set edits " + Addresses.ComponentName(other));
            }

            string before = NoiseText(perlin);
            if (asked != null && string.Equals(asked, "none", StringComparison.OrdinalIgnoreCase))
            {
                if (perlin != null)
                {
                    Undo.DestroyObjectImmediate(perlin);
                }

                Named(camera);
                return Answer.Line(address + "  noise " + before + " -> none" + Answer.Unsaved(target));
            }

            NoiseSettings profile = null;
            if (asked != null && (profile = Profile(asked, profiles, out string error)) == null)
            {
                return Answer.Error(error);
            }

            if (perlin == null)
            {
                perlin = Add<CinemachineBasicMultiChannelPerlin>(target);
            }
            else
            {
                Undo.RegisterCompleteObjectUndo(perlin, Answer.GroupName);
            }

            perlin.NoiseProfile = profile != null ? profile : perlin.NoiseProfile;
            perlin.AmplitudeGain = amplitude ?? perlin.AmplitudeGain;
            perlin.FrequencyGain = frequency ?? perlin.FrequencyGain;
            Recorded(perlin);
            return Answer.Line(address + "  noise " + before + " -> " + NoiseText(perlin) + Answer.Unsaved(target));
        }

        public Reply Blend(Request request)
        {
            CinemachineBrain brain = FindBrain(request, out Reply refused);
            if (brain == null)
            {
                return refused;
            }

            if (!Style(request, out CinemachineBlendDefinition.Styles? style, out refused) || !CameraOps.Number(request, "time", 0, 3600, out float? time, out refused))
            {
                return refused;
            }

            string from = CameraOps.Value(request, "from");
            string to = CameraOps.Value(request, "to");
            string address = Addresses.Of(brain.gameObject);
            if ((from == null) != (to == null))
            {
                return Answer.Error("--from and --to go together: a custom blend runs from one camera to another (any: every camera)");
            }

            CinemachineBlenderSettings custom = brain.CustomBlends;
            if (from == null && style == null && time == null)
            {
                var shown = new List<string> { address + "  default blend " + BlendText(brain.DefaultBlend) };
                shown.Add(custom == null ? "custom blends: none" : "custom blends: " + AssetDatabase.GetAssetPath(custom));
                foreach (CinemachineBlenderSettings.CustomBlend entry in custom?.CustomBlends ?? new CinemachineBlenderSettings.CustomBlend[0])
                {
                    shown.Add("  " + Label(entry.From) + " -> " + Label(entry.To) + "  " + BlendText(entry.Blend));
                }

                return Answer.Lines(shown);
            }

            if (from == null)
            {
                Undo.RegisterCompleteObjectUndo(brain, Answer.GroupName);
                string before = BlendText(brain.DefaultBlend);
                brain.DefaultBlend = Changed(brain.DefaultBlend, style, time);
                Recorded(brain);
                return Answer.Line(address + "  default blend " + before + " -> " + BlendText(brain.DefaultBlend) + Answer.Unsaved(brain.gameObject));
            }

            if (custom == null)
            {
                return Answer.Error(address + "'s brain has no custom blends asset: ug asset create Assets/CameraBlends.asset CinemachineBlenderSettings, then ug set "
                    + Quoted(address) + " CustomBlends Assets/CameraBlends.asset");
            }

            from = Unlabel(from);
            to = Unlabel(to);
            Undo.RegisterCompleteObjectUndo(custom, Answer.GroupName);
            List<CinemachineBlenderSettings.CustomBlend> entries = (custom.CustomBlends ?? new CinemachineBlenderSettings.CustomBlend[0]).ToList();
            int index = entries.FindIndex(e => e.From == from && e.To == to);
            string was = index < 0 ? "new" : BlendText(entries[index].Blend);
            if (index < 0)
            {
                entries.Add(new CinemachineBlenderSettings.CustomBlend
                {
                    From = from,
                    To = to,
                    Blend = new CinemachineBlendDefinition(CinemachineBlendDefinition.Styles.EaseInOut, 2f),
                });
                index = entries.Count - 1;
            }

            CinemachineBlenderSettings.CustomBlend changed = entries[index];
            changed.Blend = Changed(changed.Blend, style, time);
            entries[index] = changed;
            custom.CustomBlends = entries.ToArray();
            EditorUtility.SetDirty(custom);
            string unknown = string.Join(", ", new[] { from, to }.Where(n => n != AnyCamera && !CameraNamed(n)).Distinct());
            return Answer.Line(AssetDatabase.GetAssetPath(custom) + "  " + Label(from) + " -> " + Label(to) + "  " + was + " -> " + BlendText(changed.Blend)
                + Answer.UnsavedAsset(custom) + (unknown.Length == 0 ? "" : "  (no Cinemachine camera is named " + unknown + " in the loaded scenes)"));
        }

        public Reply Force(GameObject target, Request request)
        {
            if (!target.TryGetComponent(out CinemachineVirtualCameraBase camera))
            {
                return Answer.Error(NotCinemachine(target));
            }

            CinemachineBrain brain = FindBrain(request, out Reply refused);
            if (brain == null)
            {
                return refused;
            }

            if (!brain.isActiveAndEnabled)
            {
                return Answer.Error("the CinemachineBrain on " + Addresses.Of(brain.gameObject) + " is disabled, so it shows nothing: ug set " + Quoted(Addresses.Of(brain.gameObject)) + " --component CinemachineBrain enabled true");
            }

            int id = _forced.TryGetValue(brain, out int known) ? known : -1;
            _forced[brain] = brain.SetCameraOverride(id, int.MaxValue, camera, camera, 1f, -1f);
            return Answer.Line("forced " + Addresses.Of(target) + " live on " + Addresses.Of(brain.gameObject) + " (until ug camera release, a play mode change or a script reload)");
        }

        public Reply Release(Request request)
        {
            CinemachineBrain brain = FindBrain(request, out Reply refused);
            if (brain == null)
            {
                return refused;
            }

            if (!_forced.TryGetValue(brain, out int id))
            {
                return Answer.Line("nothing forced on " + Addresses.Of(brain.gameObject));
            }

            brain.ReleaseCameraOverride(id);
            _forced.Remove(brain);
            return Answer.Line("released " + Addresses.Of(brain.gameObject) + ": the brain goes back to priorities");
        }

        public Reply Brain(GameObject target, Request request)
        {
            if (!Style(request, out CinemachineBlendDefinition.Styles? style, out Reply refused) || !CameraOps.Number(request, "time", 0, 3600, out float? time, out refused))
            {
                return refused;
            }

            Camera camera = null;
            if (target != null && !target.TryGetComponent(out camera))
            {
                return Answer.Error(Addresses.Of(target) + " has no Camera: a CinemachineBrain drives the Unity camera it sits on");
            }

            camera = camera != null ? camera : Camera.main != null ? Camera.main : CameraOps.OnlyEnabledCamera();
            if (camera == null)
            {
                return Answer.Error("no main camera and not exactly one enabled camera: ug camera brain <camera>");
            }

            var lines = new List<string>();
            GameObject holder = camera.gameObject;
            if (!holder.TryGetComponent(out CinemachineBrain brain))
            {
                brain = AddBrain(camera);
                lines.Add("added CinemachineBrain to " + Addresses.Of(holder) + Answer.Unsaved(holder));
            }

            if (style != null || time != null)
            {
                Undo.RegisterCompleteObjectUndo(brain, Answer.GroupName);
                string before = BlendText(brain.DefaultBlend);
                brain.DefaultBlend = Changed(brain.DefaultBlend, style, time);
                Recorded(brain);
                lines.Add(Addresses.Of(holder) + "  default blend " + before + " -> " + BlendText(brain.DefaultBlend) + Answer.Unsaved(holder));
            }

            int mask = (int)brain.ChannelMask;
            lines.Add(Addresses.Of(holder) + "  CinemachineBrain" + (brain.enabled ? "" : "  disabled"));
            lines.Add("  update " + brain.UpdateMethod + ", blend update " + brain.BlendUpdateMethod);
            lines.Add("  default blend " + BlendText(brain.DefaultBlend));
            lines.Add("  custom blends " + (brain.CustomBlends == null ? "none" : AssetDatabase.GetAssetPath(brain.CustomBlends)));
            lines.Add("  channels " + (mask == -1 ? "everything" : brain.ChannelMask.ToString()));
            lines.Add("  ignore time scale " + (brain.IgnoreTimeScale ? "true" : "false"));
            lines.Add("  world up " + (brain.WorldUpOverride == null ? "y (no override)" : Addresses.Of(brain.WorldUpOverride.gameObject)));
            return Answer.Lines(lines);
        }

        public void Forget() => _forced.Clear();

        private static string Line(CinemachineVirtualCameraBase camera, bool status)
        {
            var line = new StringBuilder(Addresses.Of(camera.gameObject));
            if (!(camera is CinemachineCamera))
            {
                line.Append("  ").Append(Short(camera.GetType()));
            }

            line.Append("  priority ").Append(Whole(camera.Priority.Value));
            if (status)
            {
                line.Append(camera.IsLive ? "  live" : camera.isActiveAndEnabled ? "  standby" : "  disabled");
            }

            if (camera is CinemachineCamera cm)
            {
                AppendStage(line, "body", StageComponent(cm, CinemachineCore.Stage.Body));
                AppendStage(line, "aim", StageComponent(cm, CinemachineCore.Stage.Aim));
                CinemachineComponentBase noise = StageComponent(cm, CinemachineCore.Stage.Noise);
                if (noise != null)
                {
                    line.Append("  noise ").Append(noise is CinemachineBasicMultiChannelPerlin perlin
                        ? perlin.NoiseProfile != null ? perlin.NoiseProfile.name : "(no profile)"
                        : Short(noise));
                }

                Transform follow = cm.Target.TrackingTarget;
                Transform lookAt = cm.Target.CustomLookAtTarget ? cm.Target.LookAtTarget : null;
                if (follow != null)
                {
                    line.Append("  follow ").Append(Addresses.Of(follow.gameObject));
                }

                if (lookAt != null && lookAt != follow)
                {
                    line.Append("  look-at ").Append(Addresses.Of(lookAt.gameObject));
                }
            }

            List<string> extensions = camera.GetComponents<CinemachineExtension>().Where(e => e != null).Select(e => Short(e.GetType())).ToList();
            if (extensions.Count > 0)
            {
                line.Append("  ext ").Append(string.Join(", ", extensions));
            }

            if (camera.OutputChannel != OutputChannels.Default)
            {
                line.Append("  channel ").Append(camera.OutputChannel);
            }

            return line.ToString();
        }

        private static void AppendStage(StringBuilder line, string what, CinemachineComponentBase component)
        {
            if (component != null)
            {
                line.Append("  ").Append(what).Append(' ').Append(Short(component)).Append(component.enabled ? "" : " (disabled)");
            }
        }

        // The pipeline's first component for a stage, as CinemachineCamera picks it.
        private static CinemachineComponentBase StageComponent(CinemachineCamera camera, CinemachineCore.Stage stage) =>
            camera.GetComponents<CinemachineComponentBase>().FirstOrDefault(c => c != null && c.Stage == stage);

        // The components that can fill a stage, the project's own too; Cinemachine's deprecated 2.x-style ones are left out.
        private static List<Type> StageTypes(CinemachineCore.Stage stage) =>
            TypeCache.GetTypesWithAttribute<CameraPipelineAttribute>()
                .Where(t => !t.IsAbstract && typeof(CinemachineComponentBase).IsAssignableFrom(t) && !Attribute.IsDefined(t, typeof(ObsoleteAttribute)))
                .Where(t => ((CameraPipelineAttribute)Attribute.GetCustomAttribute(t, typeof(CameraPipelineAttribute))).Stage == stage)
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToList();

        // body, aim and noise need a CinemachineCamera: the manager cameras (ClearShot and the rest) have no pipeline.
        private static bool Pipeline(GameObject target, string what, out CinemachineCamera camera, out Reply refused)
        {
            camera = null;
            refused = null;
            if (!target.TryGetComponent(out CinemachineVirtualCameraBase any))
            {
                refused = Answer.Error(NotCinemachine(target));
                return false;
            }

            camera = any as CinemachineCamera;
            if (camera == null)
            {
                refused = Answer.Error(Addresses.Of(target) + " is a " + any.GetType().Name + ", which has no " + what + ": its child cameras do");
                return false;
            }

            return true;
        }

        private static string NotCinemachine(GameObject target) =>
            Addresses.Of(target) + " is not a Cinemachine camera" + (target.TryGetComponent(out Camera _)
                ? ": it is a Unity Camera (ug camera create makes Cinemachine cameras)"
                : ": ug camera lists the cameras");

        private static string SetTarget(CinemachineCamera camera, string text, bool lookAt, out string error)
        {
            error = null;
            Transform to = null;
            bool custom = true;
            if (lookAt && string.Equals(text, "follow", StringComparison.OrdinalIgnoreCase))
            {
                custom = false;
            }
            else if (!string.Equals(text, "none", StringComparison.OrdinalIgnoreCase))
            {
                GameObject found = CameraOps.Target(text, out error);
                if (found == null)
                {
                    return null;
                }

                to = found.transform;
            }

            Undo.RegisterCompleteObjectUndo(camera, Answer.GroupName);
            string before = lookAt ? LookAtText(camera) : Shown(camera.Target.TrackingTarget);
            if (lookAt)
            {
                camera.Target.CustomLookAtTarget = custom;
                camera.Target.LookAtTarget = custom ? to : null;
            }
            else
            {
                camera.Target.TrackingTarget = to;
            }

            Recorded(camera);
            string after = lookAt ? LookAtText(camera) : Shown(camera.Target.TrackingTarget);
            return Addresses.Of(camera.gameObject) + "  " + (lookAt ? "look-at " : "follow ") + before + " -> " + after + Answer.Unsaved(camera.gameObject);
        }

        private static string LookAtText(CinemachineCamera camera) =>
            !camera.Target.CustomLookAtTarget ? "follow" : Shown(camera.Target.LookAtTarget);

        private static string Shown(Transform target) => target == null ? "none" : Addresses.Of(target.gameObject);

        // A brain for the new camera to drive, as Cinemachine's own menu makes sure of.
        private static void EnsureBrain(Camera main, List<string> notes)
        {
            if (CinemachineBrain.ActiveBrainCount > 0)
            {
                return;
            }

            Camera holder = main != null ? main : CameraOps.OnlyEnabledCamera();
            if (holder == null)
            {
                notes.Add("no camera holds a CinemachineBrain, so nothing shows this camera: ug camera brain <camera> adds one");
                return;
            }

            if (holder.TryGetComponent(out CinemachineBrain _))
            {
                notes.Add("the CinemachineBrain on " + Addresses.Of(holder.gameObject) + " is disabled, so nothing shows this camera");
                return;
            }

            AddBrain(holder);
            notes.Add("added CinemachineBrain to " + Addresses.Of(holder.gameObject) + Answer.Unsaved(holder.gameObject));
        }

        // The menu's Dolly Spline: a two-knot auto-smooth spline where the camera stands, beside it.
        private static SplineContainer DollyTrack(GameObject camera, List<string> notes)
        {
            var track = new GameObject("Dolly Spline");
            track.transform.SetParent(camera.transform.parent, false);
            track.transform.position = camera.transform.position;
            GameObjectUtility.EnsureUniqueNameForSibling(track);
            Undo.RegisterCreatedObjectUndo(track, Answer.GroupName);
            var container = Add<SplineContainer>(track);
            Add<CinemachineSplineSmoother>(track);
            container.Spline.SetTangentMode(TangentMode.AutoSmooth);
            container.Spline.Add(new BezierKnot(Vector3.zero));
            container.Spline.Add(new BezierKnot(Vector3.right));
            notes.Add("created " + Addresses.Of(track) + " (the dolly's spline, knots at 0 and 1 m right)" + Answer.Unsaved(track));
            return container;
        }

        // --brain, else the only active brain, else the main camera's.
        private static CinemachineBrain FindBrain(Request request, out Reply refused)
        {
            refused = null;
            string address = CameraOps.Value(request, "brain");
            if (address != null)
            {
                GameObject holder = CameraOps.Target(address, out string error);
                if (holder == null)
                {
                    refused = Answer.Error(error);
                    return null;
                }

                if (!holder.TryGetComponent(out CinemachineBrain named))
                {
                    refused = Answer.Error(Addresses.Of(holder) + " has no CinemachineBrain: ug camera brain " + Quoted(Addresses.Of(holder)) + " adds one");
                }

                return named;
            }

            if (CinemachineBrain.ActiveBrainCount == 1)
            {
                return CinemachineBrain.GetActiveBrain(0);
            }

            if (Camera.main != null && Camera.main.TryGetComponent(out CinemachineBrain main))
            {
                return main;
            }

            refused = Answer.Error(CinemachineBrain.ActiveBrainCount == 0
                ? "no CinemachineBrain: ug camera brain adds one to the main camera"
                : "several brains (" + string.Join(", ", Enumerable.Range(0, CinemachineBrain.ActiveBrainCount).Select(i => Addresses.Of(CinemachineBrain.GetActiveBrain(i).gameObject)))
                    + ") and none on the main camera: give --brain");
            return null;
        }

        private static bool Style(Request request, out CinemachineBlendDefinition.Styles? style, out Reply refused)
        {
            style = null;
            refused = null;
            string text = CameraOps.Value(request, "style");
            if (text == null)
            {
                return true;
            }

            string[] names = Enum.GetNames(typeof(CinemachineBlendDefinition.Styles));
            string known = names.FirstOrDefault(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
            if (known == null)
            {
                refused = Answer.Error("no blend style " + text + " (" + string.Join(", ", names) + ")");
                return false;
            }

            style = (CinemachineBlendDefinition.Styles)Enum.Parse(typeof(CinemachineBlendDefinition.Styles), known);
            return true;
        }

        private static CinemachineBlendDefinition Changed(CinemachineBlendDefinition blend, CinemachineBlendDefinition.Styles? style, float? time) =>
            new CinemachineBlendDefinition(style ?? blend.Style, time ?? blend.Time) { CustomCurve = blend.CustomCurve };

        private static string BlendText(CinemachineBlendDefinition blend) =>
            blend.Style == CinemachineBlendDefinition.Styles.Cut ? "Cut" : blend.Style + " " + CameraOps.Round(blend.Time) + " s";

        private static string Label(string name) => name == AnyCamera ? "any" : name;

        private static string Unlabel(string name) => string.Equals(name, "any", StringComparison.OrdinalIgnoreCase) ? AnyCamera : name;

        private static bool CameraNamed(string name) =>
            Addresses.LoadedScenes()
                .SelectMany(s => s.GetRootGameObjects())
                .SelectMany(root => root.GetComponentsInChildren<CinemachineVirtualCameraBase>(true))
                .Any(c => c.Name == name);

        private static List<NoiseSettings> Profiles() =>
            AssetDatabase.FindAssets("t:NoiseSettings", new[] { "Assets", "Packages" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Distinct()
                .Select(AssetDatabase.LoadAssetAtPath<NoiseSettings>)
                .Where(p => p != null)
                .ToList();

        // A profile by asset path or by name (`6D Shake`; `_` and spaces are the same).
        private static NoiseSettings Profile(string asked, List<NoiseSettings> profiles, out string error)
        {
            error = null;
            if (Addresses.IsAssetPath(asked))
            {
                NoiseSettings atPath = AssetDatabase.LoadAssetAtPath<NoiseSettings>(asked);
                if (atPath == null)
                {
                    error = "no NoiseSettings asset at " + asked;
                }

                return atPath;
            }

            // The project's own profile wins over a package preset of the same name.
            List<NoiseSettings> named = profiles.Where(p => Same(p.name, asked)).ToList();
            List<NoiseSettings> own = named.Where(p => !AssetDatabase.GetAssetPath(p).StartsWith("Packages/", StringComparison.Ordinal)).ToList();
            List<NoiseSettings> candidates = own.Count > 0 ? own : named;
            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            error = candidates.Count > 1
                ? "ambiguous: " + asked + " is " + string.Join(", ", candidates.Select(AssetDatabase.GetAssetPath)) + ": give the path"
                : "no noise profile " + asked + Addresses.Near(asked, profiles.Select(p => p.name));
            return null;
        }

        private static bool Same(string a, string b) =>
            string.Equals(a.Replace('_', ' '), b.Replace('_', ' '), StringComparison.OrdinalIgnoreCase);

        private static string NoiseText(CinemachineBasicMultiChannelPerlin perlin) =>
            perlin == null
                ? "none"
                : (perlin.NoiseProfile != null ? perlin.NoiseProfile.name : "(no profile)") + "  amplitude " + CameraOps.Round(perlin.AmplitudeGain)
                    + "  frequency " + CameraOps.Round(perlin.FrequencyGain);

        // A prefab instance keeps a direct field change only when it is recorded as an override.
        private static void Recorded(UnityEngine.Object edited)
        {
            if (PrefabUtility.IsPartOfPrefabInstance(edited))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(edited);
            }
        }

        private static string Name(ICinemachineCamera camera) =>
            camera is CinemachineVirtualCameraBase based && based != null ? Addresses.Of(based.gameObject) : camera?.Name ?? "nothing";

        private static string Short(CinemachineComponentBase component) => component == null ? "none" : Short(component.GetType());

        // `CinemachineFollow` as `Follow`: every Cinemachine type carries the prefix.
        private static string Short(Type type) =>
            type.Name.StartsWith("Cinemachine", StringComparison.Ordinal) && type.Name.Length > "Cinemachine".Length ? type.Name.Substring("Cinemachine".Length) : type.Name;

        // Undo.AddComponent names its record Add X, so a full record of the new component names the group after the
        // request. Not AddComponent with RegisterCreatedObjectUndo: undoing that deactivates the object it was added to.
        private static T Add<T>(GameObject target)
            where T : Component => (T)Add(target, typeof(T));

        private static Component Add(GameObject target, Type type)
        {
            Component added = Undo.AddComponent(target, type);
            if (added != null)
            {
                Undo.RegisterCompleteObjectUndo(added, Answer.GroupName);
            }

            return added;
        }

        // A new brain moves its camera and sets its lens on the next frame, outside Undo: a full record of both first
        // lets undoing the brain put them back.
        private static CinemachineBrain AddBrain(Camera holder)
        {
            Undo.RegisterCompleteObjectUndo(new UnityEngine.Object[] { holder.transform, holder }, Answer.GroupName);
            return Add<CinemachineBrain>(holder.gameObject);
        }

        // A group whose last record is a removal is named Destroy Object, and an empty one (a field set to the value it
        // had) lists under its name while ug undo would undo the edit before: a full record of the camera names the
        // group after the request. Edits record whole objects (RegisterCompleteObjectUndo) for the same reason.
        private static void Named(CinemachineCamera camera)
        {
            Undo.FlushUndoRecordObjects();
            Undo.RegisterCompleteObjectUndo(camera, Answer.GroupName);
        }

        private static string Whole(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Quoted(string address) => address.IndexOf(' ') >= 0 ? "\"" + address + "\"" : address;
    }
}
