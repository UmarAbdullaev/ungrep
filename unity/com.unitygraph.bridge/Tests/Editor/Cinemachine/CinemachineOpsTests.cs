using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using Unity.Cinemachine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Splines;
using UnityEngine.TestTools;

namespace UnityGraph.Bridge.Tests
{
    /// <summary>
    /// `/camera*` with Cinemachine 3 (specs/SPEC-camera.md, Testing): compiled only when the project has
    /// it, as the end to end's BridgeHost copy does. Each test starts from an untitled scene with a Main
    /// Camera (depth -1, no brain), a Player and an Enemy, and answers come after the brain's frames.
    /// </summary>
    public class CinemachineOpsTests
    {
        private const string Unsaved = "  (the untitled scene modified, not saved)";
        private const string Folder = "Assets/_UgCameraTests";
        private const string BlendsPath = Folder + "/Blends.asset";

        private Camera _main;

        [SetUp]
        public void NewScene()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _main = new GameObject("Main Camera").AddComponent<Camera>();
            _main.tag = "MainCamera";
            _main.depth = -1;
            _main.transform.position = new Vector3(0, 1, -10);
            new GameObject("Player");
            new GameObject("Enemy").transform.position = new Vector3(3, 0, 0);
        }

        [TearDown]
        public void CloseScene()
        {
            Routes.Handle(Request("/camera/release"), new ConsoleBuffer())();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Folder);
        }

        [UnityTest]
        public IEnumerator Create_follow_adds_its_stages_a_brain_and_answers_with_the_live_camera()
        {
            var answer = new Reply[1];

            yield return Await(answer, "/camera/create", "name", "Cam", "preset", "follow", "follow", "Player", "fov", "40");

            Assert.AreEqual(
                Lines(
                    "created Cam (follow)" + Unsaved,
                    "Cam  priority 0  body Follow  aim RotationComposer  follow Player",
                    "added CinemachineBrain to Main Camera" + Unsaved,
                    "live: Cam (brain Main Camera)"),
                answer[0].Body);
            var made = GameObject.Find("Cam").GetComponent<CinemachineCamera>();
            Assert.AreEqual(40, made.Lens.FieldOfView, 0.001);
            Assert.AreEqual(_main.transform.position, made.transform.position);
            Assert.IsTrue(_main.TryGetComponent(out CinemachineBrain _));
            Assert.IsTrue(SceneManager.GetActiveScene().isDirty);

            Assert.AreEqual(Lines("undid ug camera create"), Ask("/undo"));
            Assert.IsNull(GameObject.Find("Cam"));
            Assert.IsFalse(_main.TryGetComponent(out CinemachineBrain _), "the brain the create added goes with it");
            Assert.IsTrue(_main.gameObject.activeSelf, "and leaves its camera's object active");
            Assert.AreEqual(new Vector3(0, 1, -10), _main.transform.position, "where the brain had moved it from");
            Assert.AreEqual(60, _main.fieldOfView, 0.001, "with the lens the brain had changed");

            yield return null;
            Assert.AreEqual(Lines("redid ug camera create"), Ask("/redo"));
            yield return null;
            Transform follow = GameObject.Find("Cam").GetComponent<CinemachineCamera>().Target.TrackingTarget;
            Assert.AreEqual("Player", follow != null ? follow.name : "none", "redo brings the targets back");
        }

        [UnityTest]
        public IEnumerator Create_without_a_main_camera_starts_from_the_only_enabled_camera()
        {
            _main.tag = "Untagged";
            _main.fieldOfView = 50;
            var answer = new Reply[1];

            yield return Await(answer, "/camera/create", "name", "Cam");

            var made = GameObject.Find("Cam").GetComponent<CinemachineCamera>();
            Assert.AreEqual(50, made.Lens.FieldOfView, 0.001);
            Assert.AreEqual(new Vector3(0, 1, -10), made.transform.position);
            StringAssert.Contains("added CinemachineBrain to Main Camera", answer[0].Body);
        }

        [UnityTest]
        public IEnumerator Every_preset_builds_the_components_of_Cinemachines_menu()
        {
            var answer = new Reply[1];
            foreach (string preset in new[] { "static", "follow", "third_person", "freelook", "dolly", "top-down", "side-scroller" })
            {
                yield return Await(answer, "/camera/create", "name", preset, "preset", preset);
                StringAssert.StartsWith("{\"lines\":[\"created " + preset + " (", answer[0].Body);
            }

            Assert.IsEmpty(GameObject.Find("static").GetComponents<CinemachineComponentBase>());
            Assert.IsNotNull(GameObject.Find("follow").GetComponent<CinemachineFollow>());
            Assert.IsNotNull(GameObject.Find("follow").GetComponent<CinemachineRotationComposer>());
            var arm = GameObject.Find("third_person").GetComponent<CinemachineThirdPersonFollow>();
            Assert.AreEqual(4, arm.CameraDistance, 0.001);
            Assert.AreEqual(new Vector3(0.8f, -0.4f, 0f), arm.ShoulderOffset);
#if UG_PHYSICS
            Assert.IsNotNull(GameObject.Find("third_person").GetComponent<CinemachineThirdPersonAim>());
#endif
            GameObject freelook = GameObject.Find("freelook");
            Assert.AreEqual(CinemachineOrbitalFollow.OrbitStyles.ThreeRing, freelook.GetComponent<CinemachineOrbitalFollow>().OrbitStyle);
            Assert.IsNotNull(freelook.GetComponent<CinemachineRotationComposer>());
            Assert.IsNotNull(freelook.GetComponent<CinemachineInputAxisController>());
            Assert.IsFalse(freelook.GetComponent<CinemachineFreeLookModifier>().enabled);
            var dolly = GameObject.Find("dolly").GetComponent<CinemachineSplineDolly>();
            Assert.AreSame(GameObject.Find("Dolly Spline").GetComponent<SplineContainer>(), dolly.Spline);
            Assert.AreEqual(2, dolly.Spline.Spline.Count);
            Assert.IsNotNull(GameObject.Find("dolly").GetComponent<CinemachineRotationComposer>());
            Assert.IsNotNull(GameObject.Find("top-down").GetComponent<CinemachinePositionComposer>());
            Assert.Less(Quaternion.Angle(Quaternion.Euler(90, 0, 0), GameObject.Find("top-down").transform.rotation), 0.01f);
            Assert.Less(Quaternion.Angle(Quaternion.identity, GameObject.Find("side-scroller").transform.rotation), 0.01f);
            Assert.AreEqual(1, Resources.FindObjectsOfTypeAll<CinemachineBrain>().Count(b => b.gameObject.scene.IsValid()), "one brain for all of them");

            Assert.AreEqual(Lines("undid ug camera create"), Ask("/undo"));
            Assert.IsNull(GameObject.Find("side-scroller"));
            for (int i = 0; i < 2; i++)
            {
                Ask("/undo");
            }

            Assert.IsNull(GameObject.Find("dolly"));
            Assert.IsNull(GameObject.Find("Dolly Spline"), "the dolly's spline is undone with it");
        }

        [UnityTest]
        public IEnumerator The_list_and_priority_follow_the_brains_choice()
        {
            Brain();
            Cinemachine("A", 10);
            Cinemachine("B", 5);
            var answer = new Reply[1];

            yield return Await(answer, "/camera/priority", "camera", "B", "priority", "20");

            Assert.AreEqual(Lines("B  priority 5 -> 20" + Unsaved, "live: B (brain Main Camera)"), answer[0].Body);
            Assert.AreEqual(
                Lines(
                    "Cinemachine " + Version() + "  2 Cinemachine cameras, 1 camera",
                    "B  priority 20  live",
                    "A  priority 10  standby",
                    "Main Camera  Camera  depth -1  fov 40  brain",
                    "live: B (brain Main Camera)"),
                Ask("/camera"));
            yield return Await(answer, "/camera/noise", "camera", "B");
            yield return Await(answer, "/camera/blend");
            Assert.AreEqual(Lines("undid ug camera priority"), Ask("/undo"), "showing opens no undo step");
            Assert.AreEqual(5, GameObject.Find("B").GetComponent<CinemachineCamera>().Priority.Value);
        }

        [UnityTest]
        public IEnumerator Body_swaps_tunes_and_targets_and_undoes_in_one_step()
        {
            Brain();
            CinemachineCamera cam = Cinemachine("Cam", 0);
            cam.gameObject.AddComponent<CinemachineFollow>();
            cam.gameObject.AddComponent<CinemachineRotationComposer>();
            var answer = new Reply[1];

            yield return Await(answer, "/camera/body", "camera", "Cam");
            StringAssert.StartsWith("{\"lines\":[\"Cam  body Follow  follow none  (body types: Follow, HardLockToTarget, OrbitalFollow, PositionComposer, SplineDolly, ThirdPersonFollow", answer[0].Body);

            yield return Await(answer, "/camera/body", "camera", "Cam", "type", "ThirdPersonFollow", "n", "1", "a0", "CameraDistance=5", "follow", "Player");

            StringAssert.StartsWith("{\"lines\":[\"Cam  body Follow -> ThirdPersonFollow" + Unsaved + "\",\"Cam  CinemachineThirdPersonFollow.", answer[0].Body);
            StringAssert.Contains("\"Cam  follow none -> Player" + Unsaved + "\",\"live: Cam (brain Main Camera)\"]}", answer[0].Body);
            Assert.AreEqual(5, cam.GetComponent<CinemachineThirdPersonFollow>().CameraDistance, 0.001);
            Assert.IsNull(cam.GetComponent<CinemachineFollow>());
            Assert.AreEqual("Player", cam.Target.TrackingTarget.name);

            Assert.AreEqual(Lines("undid ug camera body"), Ask("/undo"));
            Assert.IsNotNull(cam.GetComponent<CinemachineFollow>());
            Assert.IsNull(cam.GetComponent<CinemachineThirdPersonFollow>());
            Assert.IsNull(cam.Target.TrackingTarget);
            Assert.IsTrue(cam.gameObject.activeSelf, "undoing an added stage leaves the camera's object active");

            yield return Await(answer, "/camera/body", "camera", "Cam", "type", "follow");
            Assert.AreEqual(Lines("Cam  body Follow (unchanged)", "live: Cam (brain Main Camera)"), answer[0].Body);
            Assert.AreEqual(Lines("undid ug camera body"), Ask("/undo"), "an unchanged stage still names its undo step");
        }

        [UnityTest]
        public IEnumerator Aim_removes_and_looks_at_and_undo_brings_it_back()
        {
            Brain();
            CinemachineCamera cam = Cinemachine("Cam", 0);
            cam.gameObject.AddComponent<CinemachineRotationComposer>();
            var answer = new Reply[1];

            yield return Await(answer, "/camera/aim", "camera", "Cam", "type", "none");
            Assert.AreEqual(Lines("Cam  aim RotationComposer -> none" + Unsaved, "live: Cam (brain Main Camera)"), answer[0].Body);
            Assert.IsNull(cam.GetComponent<CinemachineRotationComposer>());
            Assert.AreEqual(Lines("undid ug camera aim"), Ask("/undo"));
            Assert.IsNotNull(cam.GetComponent<CinemachineRotationComposer>());

            yield return Await(answer, "/camera/aim", "camera", "Cam", "look-at", "Enemy");
            Assert.AreEqual(Lines("Cam  look-at follow -> Enemy" + Unsaved, "live: Cam (brain Main Camera)"), answer[0].Body);
            StringAssert.Contains("Cam  priority 0  live  aim RotationComposer  look-at Enemy", Ask("/camera"));

            yield return Await(answer, "/camera/aim", "camera", "Cam", "look-at", "follow");
            Assert.AreEqual(Lines("Cam  look-at Enemy -> follow" + Unsaved, "live: Cam (brain Main Camera)"), answer[0].Body);
            Assert.IsFalse(cam.Target.CustomLookAtTarget);
        }

        [Test]
        public void Stage_edits_refuse_what_they_cannot_do()
        {
            Brain();
            Cinemachine("Cam", 0);
            new GameObject("Shots").AddComponent<CinemachineClearShot>();

            StringAssert.StartsWith("{\"error\":\"no body type Spinny (Follow, ", Ask("/camera/body", "camera", "Cam", "type", "Spinny"));
            Assert.AreEqual(Err("not field=value: oops (like CameraDistance=5)"), Ask("/camera/body", "camera", "Cam", "n", "1", "a0", "oops"));
            StringAssert.StartsWith("{\"error\":\"Cam has no aim to set fields on: ug camera aim Cam <type> adds one (", Ask("/camera/aim", "camera", "Cam", "n", "1", "a0", "Damping=1,1"));
            Assert.AreEqual(Err("Shots is a CinemachineClearShot, which has no body: its child cameras do"), Ask("/camera/body", "camera", "Shots", "type", "Follow"));
            Assert.AreEqual(
                Err("Main Camera is not a Cinemachine camera: it is a Unity Camera (ug camera create makes Cinemachine cameras)"),
                Ask("/camera/noise", "camera", "Main Camera", "profile", "none"));
            Assert.AreEqual(Err("Player is not a Cinemachine camera: ug camera lists the cameras"), Ask("/camera/aim", "camera", "Player", "type", "none"));
            Assert.AreEqual(Err("ug camera works on the loaded scenes: ug set and ug component edit a prefab's camera"), Ask("/camera/body", "camera", "Assets/Cam.prefab"));
            Assert.IsEmpty(GameObject.Find("Cam").GetComponents<CinemachineComponentBase>(), "a refused edit leaves nothing behind");
        }

        [UnityTest]
        public IEnumerator Noise_takes_a_profile_by_name_and_gains_and_is_removed_by_none()
        {
            Brain();
            CinemachineCamera cam = Cinemachine("Cam", 0);
            var answer = new Reply[1];

            yield return Await(answer, "/camera/noise", "camera", "Cam");
            StringAssert.StartsWith("{\"lines\":[\"Cam  noise none\",\"profiles: 6D Shake, ", answer[0].Body);
            int profiles = AssetDatabase.FindAssets("t:NoiseSettings", new[] { "Assets", "Packages" }).Distinct().Select(g => AssetDatabase.LoadAssetAtPath<NoiseSettings>(AssetDatabase.GUIDToAssetPath(g))).Where(p => p != null).Select(p => p.name).Distinct().Count();
            if (profiles > 12)
            {
                StringAssert.EndsWith(" ... " + (profiles - 12) + " more: ug camera noise Cam --all (or any NoiseSettings asset path)\"]}", answer[0].Body);
            }

            yield return Await(answer, "/camera/noise", "camera", "Cam", "all", "true");
            StringAssert.DoesNotContain(" more", answer[0].Body);
            Assert.AreEqual(profiles, answer[0].Body.Split(new[] { ", " }, StringSplitOptions.None).Length, answer[0].Body);

            yield return Await(answer, "/camera/noise", "camera", "Cam", "profile", "6d_shake", "amplitude", "2");
            Assert.AreEqual(Lines("Cam  noise none -> 6D Shake  amplitude 2  frequency 1" + Unsaved, "live: Cam (brain Main Camera)"), answer[0].Body);
            Assert.AreEqual("6D Shake", cam.GetComponent<CinemachineBasicMultiChannelPerlin>().NoiseProfile.name);
            StringAssert.Contains("Cam  priority 0  live  noise 6D Shake", Ask("/camera"));

            yield return Await(answer, "/camera/noise", "camera", "Cam", "profile", "none");
            Assert.AreEqual(Lines("Cam  noise 6D Shake  amplitude 2  frequency 1 -> none" + Unsaved, "live: Cam (brain Main Camera)"), answer[0].Body);
            Assert.IsNull(cam.GetComponent<CinemachineBasicMultiChannelPerlin>());
            Assert.AreEqual(Lines("undid ug camera noise"), Ask("/undo"));
            Assert.IsNotNull(cam.GetComponent<CinemachineBasicMultiChannelPerlin>());

            StringAssert.StartsWith("{\"error\":\"no noise profile Wobble", Ask("/camera/noise", "camera", "Cam", "profile", "Wobble"));
            Assert.AreEqual(Err("--amplitude must be a number from 0 to 1000, not -1"), Ask("/camera/noise", "camera", "Cam", "amplitude", "-1"));
        }

        [UnityTest]
        public IEnumerator Blend_sets_the_default_and_custom_entries()
        {
            CinemachineBrain brain = Brain();
            Cinemachine("Cam", 0);
            var answer = new Reply[1];

            yield return Await(answer, "/camera/blend", "style", "Linear", "time", "0.5");
            Assert.AreEqual(Lines("Main Camera  default blend EaseInOut 2 s -> Linear 0.5 s" + Unsaved, "live: Cam (brain Main Camera)"), answer[0].Body);
            yield return Await(answer, "/camera/blend");
            Assert.AreEqual(Lines("Main Camera  default blend Linear 0.5 s", "custom blends: none", "live: Cam (brain Main Camera)"), answer[0].Body);
            Assert.AreEqual(Lines("undid ug camera blend"), Ask("/undo"));
            Assert.AreEqual(CinemachineBlendDefinition.Styles.EaseInOut, brain.DefaultBlend.Style);

            Assert.AreEqual(
                Err("Main Camera's brain has no custom blends asset: ug asset create Assets/CameraBlends.asset CinemachineBlenderSettings, then ug set \"Main Camera\" CustomBlends Assets/CameraBlends.asset"),
                Ask("/camera/blend", "from", "any", "to", "Cam"));
            Assert.AreEqual(Err("--from and --to go together: a custom blend runs from one camera to another (any: every camera)"), Ask("/camera/blend", "from", "Cam"));
            StringAssert.StartsWith("{\"error\":\"no blend style Wobbly (Cut, EaseInOut, ", Ask("/camera/blend", "style", "Wobbly"));

            AssetDatabase.CreateFolder("Assets", "_UgCameraTests");
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<CinemachineBlenderSettings>(), BlendsPath);
            brain.CustomBlends = AssetDatabase.LoadAssetAtPath<CinemachineBlenderSettings>(BlendsPath);

            yield return Await(answer, "/camera/blend", "from", "any", "to", "Cam", "style", "Cut");
            Assert.AreEqual(Lines(BlendsPath + "  any -> Cam  new -> Cut  (" + BlendsPath + " modified, not saved)", "live: Cam (brain Main Camera)"), answer[0].Body);
            yield return Await(answer, "/camera/blend", "from", "Cam", "to", "Nobody", "time", "1");
            Assert.AreEqual(
                Lines(BlendsPath + "  Cam -> Nobody  new -> EaseInOut 1 s  (" + BlendsPath + " modified, not saved)  (no Cinemachine camera is named Nobody in the loaded scenes)", "live: Cam (brain Main Camera)"),
                answer[0].Body);
            CinemachineBlenderSettings.CustomBlend first = brain.CustomBlends.CustomBlends[0];
            Assert.AreEqual("**ANY CAMERA**", first.From);
            Assert.AreEqual(CinemachineBlendDefinition.Styles.Cut, first.Blend.Style);
            Assert.AreEqual(2, brain.CustomBlends.CustomBlends.Length);
        }

        [UnityTest]
        public IEnumerator Force_makes_a_camera_live_until_release()
        {
            Brain();
            Cinemachine("A", 10);
            Cinemachine("B", 5);
            var answer = new Reply[1];

            yield return Await(answer, "/camera/force", "camera", "B");
            Assert.AreEqual(
                Lines("forced B live on Main Camera (until ug camera release, a play mode change or a script reload)", "live: B (brain Main Camera, forced)"),
                answer[0].Body);

            yield return Await(answer, "/camera/release");
            Assert.AreEqual(Lines("released Main Camera: the brain goes back to priorities", "live: A (brain Main Camera)"), answer[0].Body);
            yield return Await(answer, "/camera/release");
            Assert.AreEqual(Lines("nothing forced on Main Camera", "live: A (brain Main Camera)"), answer[0].Body);

            Assert.AreEqual(
                Err("Main Camera is not a Cinemachine camera: it is a Unity Camera (ug camera create makes Cinemachine cameras)"),
                Ask("/camera/force", "camera", "Main Camera"));
            Assert.AreEqual(Err("Player has no CinemachineBrain: ug camera brain Player adds one"), Ask("/camera/force", "camera", "A", "brain", "Player"));
        }

        [Test]
        public void Without_a_brain_force_and_blend_say_how_to_add_one()
        {
            Cinemachine("A", 10);

            Assert.AreEqual(Err("no CinemachineBrain: ug camera brain adds one to the main camera"), Ask("/camera/force", "camera", "A"));
            Assert.AreEqual(Err("no CinemachineBrain: ug camera brain adds one to the main camera"), Ask("/camera/blend", "style", "Cut"));
        }

        [UnityTest]
        public IEnumerator Brain_adds_one_to_the_main_camera_and_prints_its_settings()
        {
            var answer = new Reply[1];

            yield return Await(answer, "/camera/brain", "style", "Cut");

            Assert.AreEqual(
                Lines(
                    "added CinemachineBrain to Main Camera" + Unsaved,
                    "Main Camera  default blend EaseInOut 2 s -> Cut" + Unsaved,
                    "Main Camera  CinemachineBrain",
                    "  update SmartUpdate, blend update LateUpdate",
                    "  default blend Cut",
                    "  custom blends none",
                    "  channels everything",
                    "  ignore time scale false",
                    "  world up y (no override)",
                    "live: none (brain Main Camera: no enabled Cinemachine camera on its channels)"),
                answer[0].Body);
            Assert.AreEqual(Lines("undid ug camera brain"), Ask("/undo"));
            Assert.IsFalse(_main.TryGetComponent(out CinemachineBrain _));

            Assert.AreEqual(Err("Player has no Camera: a CinemachineBrain drives the Unity camera it sits on"), Ask("/camera/brain", "camera", "Player"));
        }

        private CinemachineBrain Brain() => _main.gameObject.AddComponent<CinemachineBrain>();

        private static CinemachineCamera Cinemachine(string name, int priority)
        {
            var camera = new GameObject(name).AddComponent<CinemachineCamera>();
            camera.Priority = priority;
            return camera;
        }

        private static string Version() => UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.unity.cinemachine").version;

        private static Request Request(string path, params string[] query)
        {
            var request = new Request { Path = path };
            for (int i = 0; i + 1 < query.Length; i += 2)
            {
                request.Query[query[i]] = query[i + 1];
            }

            return request;
        }

        // An answer that comes at once: a refusal, a list, an undo.
        private static string Ask(string path, params string[] query) => Routes.Handle(Request(path, query), new ConsoleBuffer())().Body;

        // An edit's answer, after the brain's frames: polled on editor updates for at most five seconds.
        private static IEnumerator Await(Reply[] answer, string path, params string[] query)
        {
            Func<Reply> poll = Routes.Handle(Request(path, query), new ConsoleBuffer());
            var clock = Stopwatch.StartNew();
            while ((answer[0] = poll()) == null && clock.Elapsed.TotalSeconds < 5)
            {
                yield return null;
            }

            Assert.IsNotNull(answer[0], "no answer within 5 s");
        }

        private static string Lines(params string[] lines) => Answer.Lines(lines).Body;

        private static string Err(string message) => Answer.Error(message).Body;
    }
}
