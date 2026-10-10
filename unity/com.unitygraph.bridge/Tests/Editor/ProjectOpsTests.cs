using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityGraph.Bridge.Tests
{
    /// <summary>`/project`: read-only project data, on the OpsFixture scenes.</summary>
    public class ProjectOpsTests : OpsFixture
    {
        [Test]
        public void Project_lists_version_pipeline_tags_and_layers()
        {
            string body = Ask("/project");

            StringAssert.StartsWith("{\"lines\":[\"Unity " + Application.unityVersion + "  render pipeline: ", body);
            StringAssert.Contains("\"tags  Untagged, Respawn, Finish, EditorOnly, MainCamera, Player, GameController", body);
            StringAssert.Contains("\"layers  0 Default, 1 TransparentFX, 2 Ignore Raycast, ", body);
            StringAssert.Contains(", 5 UI", body);
            StringAssert.Contains("\"sorting layers  Default", body);
            StringAssert.Contains("\"build scenes  ", body);
        }

        [Test]
        public void Project_lists_build_scenes_in_order_with_their_build_index()
        {
            EditorBuildSettingsScene[] before = EditorBuildSettings.scenes;
            try
            {
                EditorBuildSettings.scenes = new[]
                {
                    new EditorBuildSettingsScene(OtherPath, false),
                    new EditorBuildSettingsScene(MainPath, true),
                };

                StringAssert.Contains(
                    "\"build scenes  1 of 2 enabled\",\"  -  " + OtherPath + "  (disabled)\",\"  0  " + MainPath + "\"]",
                    Ask("/project"));

                EditorBuildSettings.scenes = new EditorBuildSettingsScene[0];
                StringAssert.Contains("\"build scenes  none\"]", Ask("/project"));
            }
            finally
            {
                EditorBuildSettings.scenes = before;
            }
        }

        [Test]
        public void Project_changes_nothing()
        {
            string tags = string.Join("|", UnityEditorInternal.InternalEditorUtility.tags);
            int scenes = EditorBuildSettings.scenes.Length;

            Ask("/project");

            Assert.AreEqual(tags, string.Join("|", UnityEditorInternal.InternalEditorUtility.tags));
            Assert.AreEqual(scenes, EditorBuildSettings.scenes.Length);
        }
    }

    /// <summary>Menu items through `/run`: File/Quit, File/Exit and Build items are refused, others run.</summary>
    public class MenuRefusalTests
    {
        // The answer's lines, or its error.
        private static string Run(string target)
        {
            var request = new Request { Path = "/run" };
            request.Query["target"] = target;
            Reply reply = MethodRunner.Run(request, null)();
            return reply.Lines != null ? string.Join("\n", reply.Lines) : JsonUtility.FromJson<ErrorBody>(reply.Body).error;
        }

        [Serializable]
        private class ErrorBody
        {
            public string error;
        }

        [TestCase("File/Quit", "refused, it would close the editor (ug does not quit Unity)")]
        [TestCase("File/Exit", "refused, it would close the editor (ug does not quit Unity)")]
        [TestCase("file/quit", "refused, it would close the editor (ug does not quit Unity)")]
        [TestCase(" File / Quit ", "refused, it would close the editor (ug does not quit Unity)")]
        [TestCase("File/Build Profiles", "refused, it builds a player (ug does not build; use Unity's Build Profiles window)")]
        [TestCase("File/Build And Run", "refused, it builds a player (ug does not build; use Unity's Build Profiles window)")]
        [TestCase("File/Build Settings...", "refused, it builds a player (ug does not build; use Unity's Build Profiles window)")]
        [TestCase("Build/Anything", "refused, it builds a player (ug does not build; use Unity's Build Profiles window)")]
        public void Quit_and_build_items_are_refused_and_never_executed(string target, string reason) =>
            Assert.AreEqual("did not run " + target.Trim() + ": " + reason, Run(target));

        [TestCase("File/Save Project")]
        [TestCase("File/Open Scene")]
        [TestCase("Tools/Build Lab")]
        [TestCase("Assets/Refresh")]
        public void Other_items_are_not_refused(string target) => Assert.IsNull(MethodRunner.RefusedMenu(target));

        [Test]
        public void A_harmless_menu_item_runs_and_a_missing_one_says_so()
        {
            StringAssert.StartsWith("ran Tools/BridgeHost/Log Hello in ", Run("Tools/BridgeHost/Log Hello"));

            LogAssert.Expect(LogType.Error, "ExecuteMenuItem failed because there is no menu named 'Tools/BridgeHost/Nope'");
            Assert.AreEqual("did not run Tools/BridgeHost/Nope: no menu item Tools/BridgeHost/Nope (ug run --list --menu lists them)", Run("Tools/BridgeHost/Nope"));
        }
    }
}
