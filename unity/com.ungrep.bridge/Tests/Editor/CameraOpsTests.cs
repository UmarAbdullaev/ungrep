using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ungrep.Bridge.Tests
{
    /// <summary>
    /// `/camera*` without Cinemachine (BridgeHost has none): plain Unity cameras, and the refusals of
    /// everything that needs Cinemachine 3. CinemachineOpsTests cover a project that has it.
    /// </summary>
    public class CameraOpsTests : OpsFixture
    {
        private const string Unsaved = "  (Main.unity modified, not saved)";
        private const string NoCinemachine = " needs Cinemachine 3, which is not in this project (ug package add com.unity.cinemachine)";
        private const string PrefabRefusal = "ug camera works on the loaded scenes: ug set and ug component edit a prefab's camera";

        [SetUp]
        public void WithoutCinemachine()
        {
            if (CameraOps.Rig != null)
            {
                Assert.Ignore("Cinemachine 3 is in this project: CinemachineOpsTests cover ug camera with it");
            }
        }

        [Test]
        public void With_no_camera_the_list_says_nothing_draws()
        {
            Assert.AreEqual(Lines("no Cinemachine  0 cameras", "live: no camera draws display 1"), Ask("/camera"));
        }

        [Test]
        public void Plain_cameras_are_listed_by_depth_with_the_one_drawing_display_1()
        {
            Find("Camera").AddComponent<Camera>().depth = -1;
            Camera overlay = new GameObject("Overlay").AddComponent<Camera>();
            overlay.depth = 5;
            overlay.targetDisplay = 1;
            Camera mini = new GameObject("Mini").AddComponent<Camera>();
            mini.depth = 9;
            mini.orthographic = true;
            mini.orthographicSize = 3;
            mini.enabled = false;

            Assert.AreEqual(
                Lines(
                    "no Cinemachine  3 cameras",
                    "Mini  Camera  depth 9  ortho 3  disabled",
                    "Overlay  Camera  depth 5  fov 60  display 2",
                    "Camera  Camera  depth -1  fov 60",
                    "live: Camera (Camera, depth -1, draws last on display 1)"),
                Ask("/camera"));
        }

        [Test]
        public void Thirty_cameras_are_listed_then_a_count_unless_all_are_asked()
        {
            for (int i = 0; i < 32; i++)
            {
                new GameObject("Cam" + i).AddComponent<Camera>().depth = i;
            }

            IReadOnlyList<string> lines = Start("/camera")().Lines;
            Assert.AreEqual(33, lines.Count);
            Assert.AreEqual("Cam31  Camera  depth 31  fov 60", lines[1]);
            Assert.AreEqual("... 2 more: ug camera list --all", lines[31]);
            Assert.AreEqual("live: Cam31 (Camera, depth 31, draws last on display 1)", lines[32]);
            Assert.AreEqual(34, Start("/camera", "all", "true")().Lines.Count);
        }

        [UnityTest]
        public IEnumerator Create_makes_a_plain_camera_where_the_main_camera_stands()
        {
            Camera main = Find("Camera").AddComponent<Camera>();
            main.tag = "MainCamera";
            main.depth = -1;
            main.transform.SetPositionAndRotation(new Vector3(0, 2, -10), Quaternion.Euler(10, 0, 0));
            var answer = new Reply[1];

            yield return Await(Start("/camera/create"), answer);

            Assert.AreEqual(
                Lines("created Camera (1) (plain)" + Unsaved, "Camera (1)  Camera  depth 0  fov 60", "live: Camera (1) (Camera, depth 0, draws last on display 1)"),
                answer[0].Body);
            GameObject made = Find("Camera (1)");
            Assert.AreEqual(new Vector3(0, 2, -10), made.transform.position);
            Assert.Less(Quaternion.Angle(Quaternion.Euler(10, 0, 0), made.transform.rotation), 0.01f);
            Assert.IsFalse(made.TryGetComponent(out AudioListener _));
            Assert.AreEqual("Untagged", made.tag);
        }

        [UnityTest]
        public IEnumerator Create_takes_a_name_parent_position_depth_and_field_of_view_and_undoes_in_one_step()
        {
            var answer = new Reply[1];

            yield return Await(Start("/camera/create", "name", "Eye", "parent", "Level", "at", "1,2,3", "priority", "3", "fov", "40"), answer);

            Assert.AreEqual(
                Lines("created Level/Eye (plain)" + Unsaved, "Level/Eye  Camera  depth 3  fov 40", "live: Level/Eye (Camera, depth 3, draws last on display 1)"),
                answer[0].Body);
            Assert.AreEqual(new Vector3(1, 2, 3), Find("Level/Eye").transform.position);
            Assert.AreEqual(Lines("undid ug camera create"), Ask("/undo"));
            StringAssert.StartsWith("not found: Eye", Error("Level/Eye"));
        }

        [UnityTest]
        public IEnumerator Priority_sets_a_plain_cameras_depth_with_undo()
        {
            Find("Camera").AddComponent<Camera>().depth = -1;
            var answer = new Reply[1];

            yield return Await(Start("/camera/priority", "camera", "Camera", "priority", "4"), answer);

            Assert.AreEqual(Lines("Camera  depth -1 -> 4" + Unsaved, "live: Camera (Camera, depth 4, draws last on display 1)"), answer[0].Body);
            Assert.AreEqual(Lines("undid ug camera priority"), Ask("/undo"));
            Assert.AreEqual(-1, Find("Camera").GetComponent<Camera>().depth);
        }

        [UnityTest]
        public IEnumerator A_refused_camera_edit_leaves_the_undo_history_as_it_was()
        {
            var answer = new Reply[1];
            yield return Await(Start("/camera/create", "name", "Eye"), answer);

            Assert.AreEqual(Err("Level is not a camera: ug camera lists them"), Ask("/camera/priority", "camera", "Level", "priority", "2"));

            Assert.AreEqual(Lines("undid ug camera create"), Ask("/undo"));
            StringAssert.StartsWith("not found: Eye", Error("Eye"));
        }

        [Test]
        public void Wrong_cameras_and_values_are_refused()
        {
            Find("Camera").AddComponent<Camera>();

            Assert.AreEqual(Err("the priority is a whole number, not high"), Ask("/camera/priority", "camera", "Camera", "priority", "high"));
            Assert.AreEqual(Err("no camera given: ug camera lists them"), Ask("/camera/priority", "priority", "2"));
            Assert.AreEqual(Err("not found: Nothing in the loaded scenes"), Ask("/camera/priority", "camera", "Nothing", "priority", "2"));
            Assert.AreEqual(Err(PrefabRefusal), Ask("/camera/priority", "camera", PrefabPath + ":Barrel", "priority", "2"));
            Assert.AreEqual(Err("no preset spinny (static, follow, third-person, freelook, dolly, top-down, side-scroller, plain)"), Ask("/camera/create", "preset", "spinny"));
            Assert.AreEqual(Err(EditOps.SlashName), Ask("/camera/create", "name", "Rig/Eye"));
            Assert.AreEqual(Err("--fov must be a number from 1 to 179, not 500"), Ask("/camera/create", "fov", "500"));
            Assert.AreEqual(Err("--priority must be a whole number, not 1.5"), Ask("/camera/create", "priority", "1.5"));
            Assert.AreEqual(Err("--at is x,y,z in world units, like 0,2,-10, not 1,2"), Ask("/camera/create", "at", "1,2"));
            Assert.AreEqual(Err(PrefabRefusal), Ask("/camera/create", "parent", PrefabPath));
            Assert.AreEqual(Err("not found: Nothing in the loaded scenes"), Ask("/camera/create", "parent", "Nothing"));
            Assert.AreEqual(3, Start("/camera")().Lines.Count, "a refused create made no camera");
        }

        [TestCase("/camera/body", "ug camera body")]
        [TestCase("/camera/aim", "ug camera aim")]
        [TestCase("/camera/noise", "ug camera noise")]
        [TestCase("/camera/blend", "ug camera blend")]
        [TestCase("/camera/force", "ug camera force")]
        [TestCase("/camera/release", "ug camera release")]
        [TestCase("/camera/brain", "ug camera brain")]
        public void Cinemachine_commands_are_refused_without_it(string path, string what)
        {
            Assert.AreEqual(Err(what + NoCinemachine), Ask(path, "camera", "Camera"));
        }

        [TestCase("preset", "follow", "--preset follow")]
        [TestCase("preset", "Third_Person", "--preset third-person")]
        [TestCase("preset", "static", "--preset static")]
        [TestCase("follow", "Level/Player", "--follow")]
        [TestCase("look-at", "Level/Player", "--look-at")]
        public void Cinemachine_presets_and_targets_are_refused_without_it(string name, string value, string what)
        {
            Assert.AreEqual(Err(what + NoCinemachine), Ask("/camera/create", name, value));
        }

        [Test]
        public void Other_Cinemachine_versions_are_named_in_the_refusal()
        {
            Assert.AreEqual(
                "Cinemachine 2.10.7 is installed; ug camera knows Cinemachine 3.x only: ug set and ug component edit 2.x cameras",
                CameraOps.Unsupported("2.10.7"));
            Assert.AreEqual(
                "Cinemachine 3.1.7 is installed but the bridge's Cinemachine part did not compile: ug compile shows why",
                CameraOps.Unsupported("3.1.7"));
        }

        private static Func<Reply> Start(string path, params string[] query) => Routes.Handle(Request(path, query), new ConsoleBuffer());

        // Polls on editor updates until the brain has had its frames, for at most five seconds.
        private static IEnumerator Await(Func<Reply> poll, Reply[] answer)
        {
            var clock = Stopwatch.StartNew();
            while ((answer[0] = poll()) == null && clock.Elapsed.TotalSeconds < 5)
            {
                yield return null;
            }

            Assert.IsNotNull(answer[0], "no answer within 5 s");
        }
    }
}
