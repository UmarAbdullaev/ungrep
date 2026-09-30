using NUnit.Framework;

namespace UnityGraph.Bridge.Tests
{
    /// <summary>`/stats`: read-only counts on the OpsFixture scene (Camera, Level/Enemy x2, Level/Player/Model).</summary>
    public class StatsOpsTests : OpsFixture
    {
        [Test]
        public void Stats_answer_play_state_render_memory_objects_and_assets()
        {
            string body = Ask("/stats");

            StringAssert.StartsWith("{\"lines\":[\"edit mode\",\"render  only counted in play mode (ug play, then ug perf)\",\"memory  ", body);
            StringAssert.Contains(" MB allocated of ", body);
            StringAssert.Contains(" MB scripts heap  ", body);
            StringAssert.Contains(" MB graphics driver\",\"objects  6 game objects (6 active), 7 components in 1 loaded scene\",\"assets  ", body);
            StringAssert.Contains(" files in Assets/: ", body);
            StringAssert.Contains(" scenes", body);
            StringAssert.Contains(" prefabs", body);
            StringAssert.Contains(" materials", body);
        }

        [Test]
        public void Inactive_objects_are_counted_but_not_as_active()
        {
            Find("Level/Player").SetActive(false);

            StringAssert.Contains("\"objects  6 game objects (4 active), 7 components in 1 loaded scene\"", Ask("/stats"));
        }

        [Test]
        public void Stats_change_nothing()
        {
            string tags = string.Join("|", UnityEditorInternal.InternalEditorUtility.tags);

            Ask("/stats");

            Assert.AreEqual(tags, string.Join("|", UnityEditorInternal.InternalEditorUtility.tags));
            Assert.IsFalse(UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty);
        }
    }
}
