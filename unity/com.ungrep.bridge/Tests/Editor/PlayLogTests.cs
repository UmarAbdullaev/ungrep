using System;
using NUnit.Framework;
using UnityEditor;

namespace Ungrep.Bridge.Tests
{
    /// <summary>`/playlog`: the session is recorded from simulated play mode changes, without entering play mode.</summary>
    public class PlayLogTests
    {
        private static readonly DateTime Start = new DateTime(2026, 10, 1, 14, 2, 11, DateTimeKind.Utc);

        [Test]
        public void No_session_answers_null()
        {
            var log = new PlayLog();

            Assert.IsFalse(log.HasSession);
            Assert.AreEqual("{\"session\":null}", log.Answer());
        }

        [Test]
        public void A_running_session_has_no_end_and_the_console_id_it_started_at()
        {
            var log = new PlayLog();

            log.OnPlayModeChanged(PlayModeStateChange.ExitingEditMode, Start, 4001);

            Assert.IsTrue(log.Playing);
            Assert.AreEqual(
                "{\"session\":{\"start\":\"2026-10-01T14:02:11.000Z\",\"end\":null,\"after\":4001,\"until\":null,\"events\":[]}}",
                log.Answer());
        }

        [Test]
        public void A_finished_session_has_end_until_and_the_pauses()
        {
            var log = new PlayLog();

            log.OnPlayModeChanged(PlayModeStateChange.ExitingEditMode, Start, 4001);
            log.OnPause(true, Start.AddSeconds(4.1));
            log.OnPause(false, Start.AddSeconds(6));
            log.OnPlayModeChanged(PlayModeStateChange.ExitingPlayMode, Start.AddSeconds(12.4), 4120);

            Assert.IsFalse(log.Playing);
            Assert.AreEqual(
                "{\"session\":{\"start\":\"2026-10-01T14:02:11.000Z\",\"end\":\"2026-10-01T14:02:23.400Z\",\"after\":4001,\"until\":4120,"
                + "\"events\":[{\"at\":4.1,\"what\":\"pause\"},{\"at\":6,\"what\":\"resume\"}]}}",
                log.Answer());
        }

        [Test]
        public void A_new_session_replaces_the_old_one()
        {
            var log = new PlayLog();
            log.OnPlayModeChanged(PlayModeStateChange.ExitingEditMode, Start, 1);
            log.OnPause(true, Start.AddSeconds(1));
            log.OnPlayModeChanged(PlayModeStateChange.ExitingPlayMode, Start.AddSeconds(2), 5);

            log.OnPlayModeChanged(PlayModeStateChange.ExitingEditMode, Start.AddMinutes(1), 9);

            StringAssert.Contains("\"after\":9,\"until\":null,\"events\":[]", log.Answer());
        }

        [Test]
        public void Pauses_outside_a_session_and_an_exit_without_a_start_are_ignored()
        {
            var log = new PlayLog();
            log.OnPause(true, Start);
            log.OnPlayModeChanged(PlayModeStateChange.ExitingPlayMode, Start, 3);
            Assert.AreEqual("{\"session\":null}", log.Answer());

            log.OnPlayModeChanged(PlayModeStateChange.ExitingEditMode, Start, 3);
            log.OnPlayModeChanged(PlayModeStateChange.ExitingPlayMode, Start.AddSeconds(1), 4);
            log.OnPause(true, Start.AddSeconds(2));

            StringAssert.Contains("\"events\":[]", log.Answer());
        }

        [Test]
        public void The_session_survives_save_and_restore()
        {
            var log = new PlayLog();
            log.OnPlayModeChanged(PlayModeStateChange.ExitingEditMode, Start, 4001);
            log.OnPause(true, Start.AddSeconds(4.1));
            string running = log.Answer();

            var restored = new PlayLog();
            restored.Restore(log.Save());

            Assert.AreEqual(running, restored.Answer());
            restored.OnPlayModeChanged(PlayModeStateChange.ExitingPlayMode, Start.AddSeconds(9), 4050);
            StringAssert.Contains("\"until\":4050", restored.Answer());
        }

        [TestCase("")]
        [TestCase("garbage")]
        [TestCase("1;2;3")]
        public void A_bad_saved_state_is_no_session(string saved)
        {
            var log = new PlayLog();

            log.Restore(saved);

            Assert.AreEqual("{\"session\":null}", log.Answer());
        }
    }
}
