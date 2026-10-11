using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ungrep.Bridge.Tests
{
    /// <summary>
    /// `ug play --pause-on-error` and `/play/state` (SPEC-editor-ops, Play-mode debugging) without entering
    /// play mode: the error pause gets a stand-in play state and pause, the state request its refusal.
    /// </summary>
    public class ErrorPauseTests
    {
        private ConsoleBuffer _console;
        private bool _playing;
        private int _pauses;
        private ErrorPause _errors;

        [SetUp]
        public void SetUp()
        {
            _console = new ConsoleBuffer();
            _playing = true;
            _pauses = 0;
            _errors = new ErrorPause(_console, () => _playing, () => _pauses++);
        }

        [TearDown]
        public void TearDown() => ErrorPause.Current = null;

        [Test]
        public void The_console_answers_the_id_of_a_new_entry_and_of_a_folded_repeat()
        {
            long first = _console.Add("error", "boom", "Assets/A.cs:1");
            long other = _console.Add("log", "tick", null);
            long again = _console.Add("log", "tick", null);

            Assert.AreEqual(1, first);
            Assert.AreEqual(2, other);
            Assert.AreEqual(3, again, "a repeat folds into the newest entry and takes a new id");
            Assert.AreEqual(2, _console.Find(3).Count);
        }

        [Test]
        public void Unarmed_an_error_changes_nothing()
        {
            _errors.OnLog(_console.Add("error", "boom", null), true);

            Assert.AreEqual(0, _pauses);
            Assert.IsFalse(_errors.Fired);
            Assert.AreEqual(0, _errors.Fires);
        }

        [Test]
        public void Armed_the_first_error_pauses_once_and_is_kept()
        {
            _errors.Arm();

            _errors.OnLog(_console.Add("error", "boom", "Assets/A.cs:1"), true);
            _errors.OnLog(_console.Add("error", "second", null), true);

            Assert.AreEqual(1, _pauses, "once: the arm ends when it fires");
            Assert.IsFalse(_errors.Armed);
            Assert.IsTrue(_errors.Fired);
            Assert.AreEqual(1, _errors.Fires);
            List<string> lines = _errors.Lines();
            StringAssert.StartsWith("paused at the first error, frame ", lines[0]);
            StringAssert.EndsWith(" s in", lines[0]);
            Assert.AreEqual("  #1 error  boom  Assets/A.cs:1", lines[1]);
            Assert.AreEqual("  more  whole: ug console --id 1", lines[2]);
            StringAssert.StartsWith("paused at the first error #1 (frame ", _errors.PlayPart());
        }

        [Test]
        public void In_edit_mode_an_error_leaves_the_arm_for_play_mode()
        {
            _playing = false;
            _errors.Arm();

            _errors.OnLog(_console.Add("error", "editor script", null), true);

            Assert.AreEqual(0, _pauses);
            Assert.IsTrue(_errors.Armed);
        }

        [Test]
        public void An_error_from_another_thread_pauses_on_the_next_update()
        {
            _errors.Arm();

            _errors.OnLog(_console.Add("error", "from a job", null), false);
            Assert.AreEqual(0, _pauses, "not off the main thread");

            _errors.Tick();
            Assert.AreEqual(1, _pauses);
            Assert.AreEqual("  #1 error  from a job", _errors.Lines()[1]);

            _errors.Tick();
            Assert.AreEqual(1, _pauses, "handled once");
        }

        [Test]
        public void A_repeat_folded_into_the_error_gives_its_new_id_and_count()
        {
            _console.Add("log", "before", null);
            _errors.Arm();
            _errors.OnLog(_console.Add("error", "NullReferenceException: Object reference not set\n  at Enemy.Update ()", "Assets/Enemy.cs:42"), true);

            _console.Add("error", "NullReferenceException: Object reference not set\n  at Enemy.Update ()", "Assets/Enemy.cs:42");
            long folded = _console.Add("error", "NullReferenceException: Object reference not set\n  at Enemy.Update ()", "Assets/Enemy.cs:42");

            List<string> lines = _errors.Lines();
            Assert.AreEqual("  #" + folded + " error x3  NullReferenceException: Object reference not set  Assets/Enemy.cs:42", lines[1]);
            Assert.AreEqual("  more  whole: ug console --id " + folded, lines[2]);
            StringAssert.StartsWith("paused at the first error #" + folded + " (frame ", _errors.PlayPart());
        }

        [Test]
        public void Resuming_forgets_the_error_and_leaving_play_mode_drops_the_arm()
        {
            _errors.Arm();
            _errors.OnLog(_console.Add("error", "boom", null), true);

            _errors.OnPause(true);
            Assert.IsTrue(_errors.Fired, "a pause keeps it");
            _errors.OnPause(false);
            Assert.IsFalse(_errors.Fired);

            _errors.Arm();
            _errors.OnLog(_console.Add("error", "from a job", null), false);
            _errors.OnPlayModeChanged(PlayModeStateChange.ExitingPlayMode);
            Assert.IsFalse(_errors.Armed);
            _errors.Tick();
            Assert.AreEqual(1, _pauses, "the deferred error went with play mode");

            _errors.Arm();
            _errors.OnPlayModeChanged(PlayModeStateChange.ExitingEditMode);
            Assert.IsTrue(_errors.Armed, "the arm crosses into play mode");
        }

        [Test]
        public void The_digest_names_the_error_that_paused_since_its_snapshot()
        {
            ErrorPause.Current = _errors;
            _errors.Arm();
            Digest before = Digest.Take(_console);

            _errors.OnLog(_console.Add("error", "boom", null), true);

            StringAssert.StartsWith("paused at the first error #1 (frame ", before.Paused());
            Assert.AreEqual("", Digest.Take(_console).Paused(), "a later snapshot has seen it");
        }

        [Test]
        public void The_console_part_counts_what_was_logged_after_an_id()
        {
            _console.Add("error", "before play", null);
            long after = _console.LastId;
            _console.Add("warning", "careful", null);
            _console.Add("error", "boom", "Assets/Boot.cs:12");
            var rows = new List<string>();

            Assert.AreEqual("1 error, 1 warning", Digest.ConsolePart(_console, after, null, rows));
            CollectionAssert.AreEqual(new[] { "  error  boom  Assets/Boot.cs:12" }, rows);
        }
    }

    /// <summary>`loaded:` in `/play/state`: the loaded scenes with their roots and objects.</summary>
    public class PlayStateSceneTests : OpsFixture
    {
        [Test]
        public void Loaded_counts_roots_and_objects_per_scene()
        {
            Assert.AreEqual("Main (active, 2 roots, 6 objects)", SceneOps.Loaded());

            var hidden = new GameObject("Hidden") { hideFlags = HideFlags.HideInHierarchy };
            try
            {
                Assert.AreEqual("Main (active, 2 roots, 6 objects)", SceneOps.Loaded(), "objects hidden in the hierarchy are left out");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(hidden);
            }
        }

        [Test]
        public void The_state_is_refused_in_edit_mode()
        {
            Assert.AreEqual(Err("not playing: ug play first"), Ask("/play/state"));
        }
    }
}
