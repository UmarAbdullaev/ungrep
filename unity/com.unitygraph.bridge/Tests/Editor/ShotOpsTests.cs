using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityGraph.Bridge.Tests
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
            Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "unitygraph", "shots", name);

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

            StringAssert.StartsWith("{\"lines\":[\"Library/unitygraph/shots/game.png  128x96  ", body);
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

            StringAssert.StartsWith("{\"error\":\"no enabled camera in the loaded scenes", Ask("/shot", "view", "game"));
        }
    }
}
