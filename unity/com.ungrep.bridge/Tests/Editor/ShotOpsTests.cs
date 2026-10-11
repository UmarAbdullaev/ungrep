using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Ungrep.Bridge.Tests
{
    /// <summary>
    /// `/shot`. A batch-mode editor started with -nographics has no graphics device, so it refuses
    /// (tested here); the PNG itself is written and read back only where a device exists.
    /// </summary>
    public class ShotOpsTests : OpsFixture
    {
        private const string NoDevice = "cannot take a screenshot: this Unity runs without a graphics device (started with -batchmode -nographics); open the editor normally";

        private static bool HasGraphics => SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        private static string ShotFile(string name) =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "ungrep", "shots", name);

        [Test]
        public void Without_a_graphics_device_game_and_scene_views_are_refused_not_blank()
        {
            if (HasGraphics)
            {
                Assert.Ignore("this Unity has a graphics device");
            }

            new GameObject("Eye").AddComponent<Camera>();
            Directory.CreateDirectory(Path.GetDirectoryName(ShotFile("game.png")));
            File.Delete(ShotFile("game.png"));
            File.Delete(ShotFile("camera-Eye.png"));

            Assert.AreEqual(Err(NoDevice), Ask("/shot", "view", "game"));
            Assert.AreEqual(Err(NoDevice), Ask("/shot"));
            Assert.AreEqual(Err(NoDevice), Ask("/shot", "view", "scene"));
            Assert.AreEqual(Err(NoDevice), Ask("/shot", "view", "Eye"));
            Assert.IsFalse(File.Exists(ShotFile("game.png")));
            Assert.IsFalse(File.Exists(ShotFile("camera-Eye.png")));
        }

        [TestCase("width", "10", "width must be a whole number from 64 to 2048, not 10")]
        [TestCase("width", "4096", "width must be a whole number from 64 to 2048, not 4096")]
        [TestCase("height", "wide", "height must be a whole number from 64 to 2048, not wide")]
        [TestCase("height", "-5", "height must be a whole number from 64 to 2048, not -5")]
        public void Sizes_outside_the_limits_are_refused_before_rendering(string name, string value, string message)
        {
            Assert.AreEqual(Err(message), Ask("/shot", "view", "game", name, value));
        }

        [Test]
        public void A_camera_address_is_resolved_like_any_object_address()
        {
            Assert.AreEqual(Err("not found: Nothing in the loaded scenes"), Ask("/shot", "view", "Nothing"));
            if (!HasGraphics)
            {
                // Resolved before the device check: a wrong address is the more useful answer.
                Assert.AreEqual(Err("Level has no Camera component"), Ask("/shot", "view", "Level"));
            }
        }

        [Test]
        public void File_names_for_views_are_safe()
        {
            Assert.AreEqual("game", ShotOps.Name("game"));
            Assert.AreEqual("scene", ShotOps.Name("scene"));
            Assert.AreEqual("camera-Level-Cam-2", ShotOps.Name("Level/Cam#2"));
            Assert.AreEqual("camera-Main-Rig-Eye", ShotOps.Name("Main:Rig/Eye"));
            Assert.LessOrEqual(ShotOps.Name(new string('a', 300)).Length, 60);
        }

        [Test]
        public void The_game_view_is_a_readable_png_of_the_asked_size()
        {
            if (!HasGraphics)
            {
                Assert.Ignore("no graphics device (-nographics): the PNG is tested where one exists");
            }

            Camera camera = new GameObject("Eye").AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(1, 0, 0, 1);

            string body = Ask("/shot", "view", "game", "width", "128", "height", "96");

            StringAssert.StartsWith("{\"lines\":[\"Library/ungrep/shots/game.png  128x96  ", body);
            StringAssert.Contains("(Game view, 1 camera)", body);
            byte[] png = File.ReadAllBytes(ShotFile("game.png"));
            Assert.AreEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png.Take(8).ToArray());
            Assert.AreEqual(128, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]);
            Assert.AreEqual(96, (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);
            var image = new Texture2D(2, 2);
            Assert.IsTrue(image.LoadImage(png));
            Assert.AreEqual(128, image.width);
            Assert.AreEqual(96, image.height);
            Assert.Greater(image.GetPixel(64, 48).r, 0.9f, "the camera's red background is in the middle of the picture");
            UnityEngine.Object.DestroyImmediate(image);
            Assert.IsNull(camera.targetTexture, "the camera gets its own target back");
        }

        [Test]
        public void An_overlay_canvas_is_put_back_after_the_game_shot()
        {
            if (!HasGraphics)
            {
                Assert.Ignore("no graphics device (-nographics): the PNG is tested where one exists");
            }

            new GameObject("Eye").AddComponent<Camera>();
            var canvas = new GameObject("Hud", typeof(Canvas)).GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
            UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();

            StringAssert.Contains("(Game view, 1 camera)", Ask("/shot", "view", "game", "width", "128", "height", "96"));

            Assert.AreEqual(RenderMode.ScreenSpaceOverlay, canvas.renderMode);
            Assert.IsNull(canvas.worldCamera);
            Assert.AreEqual(100f, canvas.planeDistance);
            Assert.IsFalse(canvas.gameObject.scene.isDirty, "a shot leaves the scene as it was");
        }

        [Test]
        public void A_camera_address_renders_that_camera_only()
        {
            if (!HasGraphics)
            {
                Assert.Ignore("no graphics device (-nographics): the PNG is tested where one exists");
            }

            new GameObject("Eye").AddComponent<Camera>();
            new GameObject("Other").AddComponent<Camera>();

            StringAssert.Contains("(camera Eye)", Ask("/shot", "view", "Eye"));
            Assert.IsTrue(File.Exists(ShotFile("camera-Eye.png")));
            StringAssert.Contains("960x540", Ask("/shot", "view", "Eye"));
        }

        [Test]
        public void The_game_view_without_a_camera_says_so()
        {
            if (!HasGraphics)
            {
                Assert.Ignore("no graphics device (-nographics)");
            }

            StringAssert.StartsWith("{\"error\":\"no enabled camera or UI Toolkit document in the loaded scenes", Ask("/shot", "view", "game"));
        }

        // Viewpoints and sheets (SPEC-editor-ops, Viewpoints): refusals come before the device check.

        [TestCase("the Scene view moves only with --frame: drop scene for a viewpoint (ug shot --from x,y,z --look <object>) or a sheet (ug shot <object> --angles)", "view", "scene", "from", "1,2,3")]
        [TestCase("the Scene view moves only with --frame: drop scene for a viewpoint (ug shot --from x,y,z --look <object>) or a sheet (ug shot <object> --angles)", "view", "scene", "angles", "true")]
        [TestCase("--keep goes with --frame: ug shot scene --frame <object> --keep", "view", "scene", "keep", "true")]
        [TestCase("--frame frames the Scene view: ug shot scene --frame Player", "frame", "Player")]
        [TestCase("--keep goes with ug shot scene --frame <object>", "from", "1,2,3", "keep", "true")]
        [TestCase("--angles and --orbit are two kinds of sheet: give one", "view", "Player", "angles", "true", "orbit", "")]
        [TestCase("--elevations goes with --orbit", "view", "Player", "angles", "true", "elevations", "0,30")]
        [TestCase("--distance goes with --angles or --orbit; place a single shot with --from", "from", "1,2,3", "distance", "4")]
        [TestCase("--from, --look and --rotation place one shot; a sheet circles its target: ug shot <object> --angles", "view", "Player", "angles", "true", "from", "1,2,3")]
        [TestCase("--look and --rotation both aim the camera: give one", "look", "Player", "rotation", "0,90,0")]
        [TestCase("--angles needs an object or a point x,y,z to circle: ug shot Player --angles", "angles", "true")]
        [TestCase("--orbit needs an object or a point x,y,z to circle: ug shot Player --orbit", "view", "game", "orbit", "")]
        [TestCase("--fov must be a number from 1 to 179, not 0", "look", "Player", "fov", "0")]
        [TestCase("--distance must be a number from 0.001 to 1000000, not -2", "view", "Player", "angles", "true", "distance", "-2")]
        [TestCase("--from needs three numbers x,y,z, not 1,2", "from", "1,2")]
        [TestCase("--rotation needs three numbers x,y,z, not up", "from", "1,2,3", "rotation", "up")]
        [TestCase("--orbit takes a number of azimuths from 1 to 36, not 40", "view", "Player", "orbit", "40")]
        [TestCase("--elevations takes 1 to 6 angles from -89 to 89, like 0,30,-15, not 0,95", "view", "Player", "orbit", "", "elevations", "0,95")]
        [TestCase("width must be a whole number from 64 to 2048, not 10", "view", "Player", "angles", "true", "width", "10")]
        [TestCase("the sheet would be 7270x544, over 4096 pixels a side: --width 111 or less", "view", "Level/Player", "orbit", "36", "width", "200")]
        [TestCase("the sheet would be 482x4330, over 4096 pixels a side: --height 681 or less", "view", "Level/Player", "orbit", "2", "elevations", "0,10,20,30,40,50", "height", "720")]
        [TestCase("not found: Nothing in the loaded scenes", "view", "Nothing", "angles", "true")]
        [TestCase("not found: Nothing in the loaded scenes", "look", "Nothing")]
        [TestCase("Level has no Camera component", "look", "Player", "camera", "Level")]
        [TestCase("Level has no Camera component", "view", "Level", "from", "0,1,0")]
        [TestCase("the camera would stand on the point it looks at (1, 2, 3): give --from somewhere else", "from", "1,2,3", "look", "1,2,3")]
        [TestCase("--frame takes an object, not a point: ug shot scene --frame Player", "view", "scene", "frame", "1,2,3")]
        public void Viewpoint_and_sheet_mistakes_are_refused_before_rendering(string message, params string[] query)
        {
            Assert.AreEqual(Err(message), Ask("/shot", query));
            Assert.IsFalse(LeftOver(), "no shot camera is made for a refused request");
        }

        [Test]
        public void A_prefab_asset_is_not_a_target_and_two_cameras_are_one_too_many()
        {
            Assert.AreEqual(Err(PrefabPath + " is in a prefab asset, not in a loaded scene: place it first (ug object create --prefab)"), Ask("/shot", "view", PrefabPath, "angles", "true"));
            new GameObject("Eye").AddComponent<Camera>();
            new GameObject("Other").AddComponent<Camera>();
            Assert.AreEqual(Err("two cameras: Eye and --camera Other: give one"), Ask("/shot", "view", "Eye", "camera", "Other", "look", "Player"));
        }

        [Test]
        public void Without_a_graphics_device_viewpoints_and_sheets_are_refused_after_their_checks()
        {
            if (HasGraphics)
            {
                Assert.Ignore("this Unity has a graphics device");
            }

            Assert.AreEqual(Err(NoDevice), Ask("/shot", "from", "0,1,-5", "look", "Level/Player"));
            Assert.AreEqual(Err(NoDevice), Ask("/shot", "view", "Level/Player", "angles", "true"));
            Assert.AreEqual(Err(NoDevice), Ask("/shot", "view", "0,0,0", "orbit", "4"));
            Assert.IsFalse(LeftOver());
        }

        [Test]
        public void A_viewpoint_renders_from_where_it_is_asked_with_the_source_cameras_settings()
        {
            if (!HasGraphics)
            {
                Assert.Ignore("no graphics device (-nographics): the PNG is tested where one exists");
            }

            Camera eye = RedEye();
            Cube("Box", Vector3.zero);

            string body = Ask("/shot", "from", "0,0,-5", "look", "Box", "width", "128", "height", "96");

            StringAssert.StartsWith("{\"lines\":[\"Library/ungrep/shots/view.png  128x96  ", body);
            StringAssert.Contains("(from 0, 0, -5 looking at Box, settings of Eye)", body);
            Texture2D image = Load("view.png");
            Assert.Less(image.GetPixel(64, 48).r, 0.9f, "the box is in the middle");
            Assert.Greater(image.GetPixel(2, 2).r, 0.9f, "the eye's red background is around it");
            UnityEngine.Object.DestroyImmediate(image);

            StringAssert.Contains("(from 0, 0, -5, rotation 0, 180, 0, settings of Eye)", Ask("/shot", "from", "0,0,-5", "rotation", "0,180,0", "width", "128", "height", "96"));
            image = Load("view.png");
            Assert.Greater(image.GetPixel(64, 48).r, 0.9f, "looking away from the box shows only the background");
            UnityEngine.Object.DestroyImmediate(image);

            Assert.AreEqual(new Vector3(0, 0, -10), eye.transform.position, "the source camera does not move");
            Assert.IsNull(eye.targetTexture);
            Assert.IsFalse(LeftOver());
        }

        [Test]
        public void A_source_that_draws_no_layer_gives_a_viewpoint_of_every_layer_and_says_so()
        {
            if (!HasGraphics)
            {
                Assert.Ignore("no graphics device (-nographics): the PNG is tested where one exists");
            }

            Camera eye = RedEye();
            eye.cullingMask = 0;
            Cube("Box", Vector3.zero);

            StringAssert.Contains("(from 0, 0, -5 looking at Box, settings of Eye but every layer (its culling mask is Nothing))",
                Ask("/shot", "from", "0,0,-5", "look", "Box", "width", "128", "height", "96"));
            Texture2D image = Load("view.png");
            Assert.Less(image.GetPixel(64, 48).r, 0.9f, "the box is drawn");
            UnityEngine.Object.DestroyImmediate(image);
            StringAssert.Contains("settings of Eye but every layer (its culling mask is Nothing)", Ask("/shot", "view", "Box", "angles", "true", "width", "64", "height", "64"));

            Assert.AreEqual(0, eye.cullingMask, "the source camera keeps its mask");
            Assert.IsFalse(LeftOver());
        }

        [Test]
        public void Look_alone_keeps_the_source_cameras_place_and_a_scene_without_cameras_uses_unitys_defaults()
        {
            if (!HasGraphics)
            {
                Assert.Ignore("no graphics device (-nographics): the PNG is tested where one exists");
            }

            Cube("Box", new Vector3(3, 0, 0));
            StringAssert.Contains("looking at Box, Unity's default camera settings)", Ask("/shot", "look", "Box", "width", "64", "height", "64"));

            RedEye();
            StringAssert.Contains("(from 0, 0, -10 looking at point 3, 0, 0, settings of Eye)", Ask("/shot", "look", "3,0,0", "width", "64", "height", "64"));
            StringAssert.Contains("(from 0, 0, -10 looking at Box, settings of Eye)", Ask("/shot", "view", "Eye", "look", "Box", "fov", "20", "width", "64", "height", "64"));
        }

        [Test]
        public void Angles_put_six_views_of_the_target_in_one_png()
        {
            if (!HasGraphics)
            {
                Assert.Ignore("no graphics device (-nographics): the PNG is tested where one exists");
            }

            RedEye();
            Cube("Box", new Vector3(5, 0, 0));

            string body = Ask("/shot", "view", "Box", "angles", "true", "width", "80", "height", "64");

            StringAssert.StartsWith("{\"lines\":[\"Library/ungrep/shots/angles-Box.png  244x130  ", body);
            StringAssert.Contains("(Box, 6 views of 80x64, distance ", body);
            StringAssert.Contains(", settings of Eye)\",\"  rows: front, back, left | right, top, three-quarter\"]}", body);
            Texture2D sheet = Load("angles-Box.png");
            Assert.AreEqual(244, sheet.width);
            Assert.AreEqual(130, sheet.height);
            for (int tile = 0; tile < 6; tile++)
            {
                int x = tile % 3 * 82 + 40;
                int y = (1 - tile / 3) * 66 + 32;
                Assert.Less(sheet.GetPixel(x, y).r, 0.9f, "tile " + tile + " has the box in its middle");
                Assert.Greater(sheet.GetPixel(x - 38, y + 30).r, 0.9f, "tile " + tile + " has the background in its corner");
            }

            Assert.AreEqual(32, Mathf.RoundToInt(sheet.GetPixel(80, 10).r * 255), "a dark gap between tiles");
            UnityEngine.Object.DestroyImmediate(sheet);
            Assert.IsFalse(LeftOver());
        }

        [Test]
        public void Orbit_lays_azimuths_in_columns_and_elevations_in_rows()
        {
            if (!HasGraphics)
            {
                Assert.Ignore("no graphics device (-nographics): the PNG is tested where one exists");
            }

            RedEye();
            Cube("Box", Vector3.zero);

            string body = Ask("/shot", "view", "Box", "orbit", "4", "elevations", "0,45", "width", "80", "height", "64");

            StringAssert.StartsWith("{\"lines\":[\"Library/ungrep/shots/orbit-Box.png  326x130  ", body);
            StringAssert.Contains("(Box, 4 azimuths x 2 elevations of 80x64, distance ", body);
            StringAssert.Contains("\"  columns: azimuth 0, 90, 180, 270 (0 = front, 90 = right); rows: elevation 0, 45\"", body);
            Texture2D sheet = Load("orbit-Box.png");
            Assert.AreEqual(326, sheet.width);
            Assert.Less(sheet.GetPixel(3 * 82 + 40, 32).r, 0.9f, "the last tile has the box too");
            UnityEngine.Object.DestroyImmediate(sheet);

            StringAssert.Contains("(point 0, 1, 0, 8 azimuths x 3 elevations of 240x180, distance 10, settings of Eye)", Ask("/shot", "view", "0,1,0", "orbit", ""));
            Assert.IsTrue(File.Exists(ShotFile("orbit-0-1-0.png")));
            StringAssert.Contains("distance 2.5", Ask("/shot", "view", "Box", "angles", "true", "distance", "2.5", "width", "64", "height", "64"));
        }

        [Test]
        public void A_shot_camera_leaves_no_trace_in_the_scene_or_the_undo_history()
        {
            if (!HasGraphics)
            {
                Assert.Ignore("no graphics device (-nographics): the PNG is tested where one exists");
            }

            RedEye().orthographic = true;
            GameObject box = Cube("Box", Vector3.zero);
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
            int objects = SceneManager.GetActiveScene().GetRootGameObjects().Length;

            StringAssert.Contains("angles-Box.png", Ask("/shot", "view", "Box", "angles", "true", "width", "64", "height", "64"));
            StringAssert.Contains("view.png", Ask("/shot", "from", "4,4,4", "look", "Box", "width", "64", "height", "64"));

            Assert.IsFalse(SceneManager.GetActiveScene().isDirty, "a shot leaves the scene unmodified");
            Assert.AreEqual(objects, SceneManager.GetActiveScene().GetRootGameObjects().Length);
            Assert.IsFalse(LeftOver());

            Undo.RecordObject(box, "Rename Box");
            box.name = "Crate";
            Undo.IncrementCurrentGroup();
            StringAssert.Contains("looking at Crate", Ask("/shot", "from", "4,4,4", "look", "Crate", "width", "64", "height", "64"));
            Undo.PerformUndo();
            Assert.AreEqual("Box", box.name, "the shot put nothing on the undo stack: one undo reverts the rename");
        }

        [Test]
        public void A_framed_scene_view_is_put_back_unless_kept()
        {
            if (!HasGraphics)
            {
                Assert.Ignore("no graphics device (-nographics): the PNG is tested where one exists");
            }

            Cube("Box", new Vector3(20, 0, 0));
            SceneView view = SceneView.lastActiveSceneView;
            if (view == null || view.camera == null)
            {
                // A batch-mode editor has no windows: the frame is checked live (SPEC-editor-ops, Measured).
                Assert.AreEqual(Err("no Scene view is open in the editor: open one (Window > General > Scene) or use ug shot game"), Ask("/shot", "view", "scene", "frame", "Box"));
                return;
            }

            Vector3 pivot = view.pivot;
            StringAssert.Contains("(Scene view framed on Box)", Ask("/shot", "view", "scene", "frame", "Box", "width", "64", "height", "64"));
            Assert.AreEqual(pivot, view.pivot);
            StringAssert.Contains("(Scene view framed on Box, left framed)", Ask("/shot", "view", "scene", "frame", "Box", "keep", "true", "width", "64", "height", "64"));
            Assert.Less(Vector3.Distance(view.pivot, new Vector3(20, 0, 0)), 0.01f);
            view.LookAtDirect(pivot, view.rotation, view.size);
        }

        [Test]
        public void Sheet_file_names_are_safe()
        {
            Assert.AreEqual("angles-Level-Player", ShotOps.Name("angles-", "Level/Player"));
            Assert.AreEqual("orbit-0-1-0", ShotOps.Name("orbit-", "0,1,0"));
        }

        // A camera named Eye at 0,0,-10 that clears to red: what is not red in a shot is the scene.
        private static Camera RedEye()
        {
            Camera camera = new GameObject("Eye").AddComponent<Camera>();
            camera.transform.position = new Vector3(0, 0, -10);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(1, 0, 0, 1);
            return camera;
        }

        // An unlit blue cube, so the picture does not depend on the scene's light.
        private static GameObject Cube(string name, Vector3 position)
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.position = position;
            cube.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Unlit/Color")) { color = Color.blue };
            return cube;
        }

        private static Texture2D Load(string name)
        {
            var image = new Texture2D(2, 2);
            Assert.IsTrue(image.LoadImage(File.ReadAllBytes(ShotFile(name))));
            return image;
        }

        private static bool LeftOver() => Resources.FindObjectsOfTypeAll<GameObject>().Any(o => o.name == ShotCamera.ObjectName);
    }
}
