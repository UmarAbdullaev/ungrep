using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Ungrep.Bridge.Tests
{
    /// <summary>
    /// The play-input requests (SPEC-play-input) that need no play mode: the refusals and range checks,
    /// the action queue (its poll sees an editor that is not playing, which is the cut-short path),
    /// and the digest's console part.
    /// </summary>
    public class PlayInputTests
    {
        [TearDown]
        public void TearDown() => InputQueue.Abort("test over");

        private static Request Query(params string[] pairs)
        {
            var request = new Request { Path = "/input" };
            for (int i = 0; i < pairs.Length; i += 2)
            {
                request.Query[pairs[i]] = pairs[i + 1];
            }

            return request;
        }

        private static string Error(Func<Reply> poll)
        {
            Reply reply = poll();
            Assert.IsNotNull(reply, "the request answers at once");
            StringAssert.StartsWith("{\"error\":", reply.Body);
            return reply.Body.Substring("{\"error\":\"".Length).TrimEnd('}', '"');
        }

        [Test]
        public void Every_action_refuses_while_the_editor_is_not_playing()
        {
            Assert.AreEqual("not playing: ug play first", Error(PlayInput.Press(Query("key", "Space"))));
            Assert.AreEqual("not playing: ug play first", Error(PlayInput.Stick(Query("side", "left", "x", "1", "y", "0"))));
            Assert.AreEqual("not playing: ug play first", Error(PlayInput.Wait(Query("seconds", "1"))));
            Assert.AreEqual("not playing: ug play first", Error(PlayInput.Step(Query("frames", "2"))));
            Assert.AreEqual("not playing: ug play first", Error(PlayInput.UiClick(Query("element", "Play"))));
            Assert.AreEqual("not playing: ug play first", Error(PlayInput.UiNav(Query("direction", "down"))));
        }

        [Test]
        public void A_press_needs_a_key_and_a_hold_in_range()
        {
            StringAssert.StartsWith("ug press needs a key or button", Error(PlayInput.Press(Query())));
            StringAssert.StartsWith("--hold must be 0.05 to 60", Error(PlayInput.Press(Query("key", "Space", "hold", "0.01"))));
            StringAssert.StartsWith("--hold must be 0.05 to 60", Error(PlayInput.Press(Query("key", "Space", "hold", "61"))));
            StringAssert.StartsWith("--hold must be a number", Error(PlayInput.Press(Query("key", "Space", "hold", "long"))));
            StringAssert.StartsWith("--hold must be a number", Error(PlayInput.Press(Query("key", "Space", "hold", "NaN"))));
        }

        [Test]
        public void A_stick_needs_a_side_and_a_deflection_in_range()
        {
            StringAssert.StartsWith("ug stick needs left or right", Error(PlayInput.Stick(Query("side", "up", "x", "1", "y", "0"))));
            Assert.AreEqual("x,y must be -1 to 1, like 1,0", Error(PlayInput.Stick(Query("side", "left", "x", "1.5", "y", "0"))));
            Assert.AreEqual("x,y must be -1 to 1, like 1,0", Error(PlayInput.Stick(Query("side", "left", "x", "0", "y", "-2"))));
            StringAssert.StartsWith("--hold must be 0.05 to 60", Error(PlayInput.Stick(Query("side", "right", "x", "1", "y", "0", "hold", "0"))));
        }

        [Test]
        public void A_tap_and_a_swipe_need_points_and_a_swipe_a_length_of_time()
        {
            StringAssert.StartsWith("a point is x,y in Game view pixels", Error(PlayInput.Tap(Query("x", "left", "y", "1"))));
            StringAssert.StartsWith("a point is x,y in Game view pixels", Error(PlayInput.Tap(Query("x", "1"))));
            StringAssert.StartsWith("a point is x,y in Game view pixels", Error(PlayInput.Swipe(Query("x1", "0", "y1", "0", "x2", "1"))));
        }

        [Test]
        public void A_wait_is_005_to_60_seconds_and_a_step_a_whole_count_of_frames()
        {
            StringAssert.StartsWith("seconds must be 0.05 to 60", Error(PlayInput.Wait(Query("seconds", "0.01"))));
            StringAssert.StartsWith("seconds must be 0.05 to 60", Error(PlayInput.Wait(Query("seconds", "61"))));
            StringAssert.StartsWith("seconds must be a number", Error(PlayInput.Wait(Query())));
            StringAssert.StartsWith("frames must be 1 to 600", Error(PlayInput.Step(Query("frames", "0"))));
            StringAssert.StartsWith("frames must be 1 to 600", Error(PlayInput.Step(Query("frames", "601"))));
            StringAssert.StartsWith("frames is a whole number", Error(PlayInput.Step(Query("frames", "1.5"))));
        }

        [Test]
        public void A_click_needs_an_element()
        {
            // Not playing is checked first; the element only matters while playing.
            Assert.AreEqual("not playing: ug play first", Error(PlayInput.UiClick(Query())));
        }

        [Test]
        public void Lasting_holds_for_the_time_and_the_frames_and_moves_once_per_frame()
        {
            var moves = new List<float>();
            Func<double, int, bool> hold = PlayAction.Lasting(1, 2, moves.Add);

            Assert.IsFalse(hold(0.5, 5), "too early");
            Assert.IsFalse(hold(0.5, 6), "the same frame again moves nothing more");
            Assert.AreEqual(new[] { 0.5f }, moves.ToArray());
            Assert.IsFalse(hold(2, 1), "time is up but fewer than two frames have run");
            Assert.IsTrue(hold(1, 2));
            Assert.IsTrue(PlayAction.Lasting(0, 0)(0, 0));
        }

        [Test]
        public void The_queue_runs_one_action_and_refuses_a_second()
        {
            var released = new List<string>();
            PlayAction first = Action("press Space", released);

            Func<Reply> poll = InputQueue.Start(first);
            Func<Reply> second = InputQueue.Start(Action("press A", released));

            Assert.AreEqual("{\"error\":\"an input action is running (press Space): wait for it, or ug stop\"}", second().Body);
            Assert.IsEmpty(released, "a refused action holds nothing");
            Assert.IsNotNull(poll);
        }

        [Test]
        public void An_action_cut_short_by_the_end_of_play_is_released_and_says_so()
        {
            var released = new List<string>();
            Func<Reply> poll = InputQueue.Start(Action("press Space", released));

            // The editor is not playing: the poll is the play-mode-ended path.
            Reply reply = poll();

            Assert.AreEqual(new[] { "press Space" }, released.ToArray());
            StringAssert.StartsWith("{\"lines\":[\"press Space: play mode ended after ", reply.Body);
            StringAssert.EndsWith(" s, released\"]}", reply.Body);

            // The queue is free again: a new action runs, and the next one is refused naming it.
            InputQueue.Start(Action("press A", released));
            Assert.AreEqual("{\"error\":\"an input action is running (press A): wait for it, or ug stop\"}", InputQueue.Start(Action("press B", released))().Body);
        }

        [Test]
        public void An_abort_releases_once_and_answers_the_waiting_request_with_the_reason()
        {
            var released = new List<string>();
            Func<Reply> poll = InputQueue.Start(Action("stick left", released));

            InputQueue.Abort("assemblies reloaded");
            InputQueue.Abort("assemblies reloaded");

            Assert.AreEqual(new[] { "stick left" }, released.ToArray());
            StringAssert.StartsWith("{\"lines\":[\"stick left: assemblies reloaded after ", poll().Body);
        }

        [Test]
        public void A_refused_start_holds_nothing_and_frees_the_queue()
        {
            var released = new List<string>();
            var refused = new PlayAction("press Nope") { Start = () => Answer.Error("no key Nope"), Release = () => released.Add("press Nope") };

            Func<Reply> poll = InputQueue.Start(refused);

            Assert.AreEqual("{\"error\":\"no key Nope\"}", poll().Body);
            Assert.IsEmpty(released);
            Func<Reply> next = InputQueue.Start(Action("press A", released));
            StringAssert.StartsWith("{\"lines\":", next().Body);
        }

        [Test]
        public void A_start_that_throws_releases_and_frees_the_queue()
        {
            var released = new List<string>();
            var broken = new PlayAction("press X") { Start = () => throw new InvalidOperationException("device gone"), Release = () => released.Add("press X") };

            Assert.Throws<InvalidOperationException>(() => InputQueue.Start(broken));

            Assert.AreEqual(new[] { "press X" }, released.ToArray());
            StringAssert.StartsWith("{\"lines\":", InputQueue.Start(Action("press A", released))().Body);
        }

        [Test]
        public void Extend_puts_more_lines_after_the_answers_own_and_leaves_an_error_alone()
        {
            Reply lines = Answer.Lines(new[] { "pressed Space for 0.1 s" });

            Reply extended = Answer.Extend(lines, new[] { "  now: nothing changed" });

            Assert.AreEqual("{\"lines\":[\"pressed Space for 0.1 s\",\"  now: nothing changed\"]}", extended.Body);
            Assert.AreEqual(new[] { "pressed Space for 0.1 s", "  now: nothing changed" }, new List<string>(extended.Lines).ToArray());
            Reply error = Answer.Error("no");
            Assert.AreSame(error, Answer.Extend(error, new[] { "  now: x" }));
        }

        [Test]
        public void The_digest_counts_new_errors_and_warnings_and_shows_two_error_rows()
        {
            var console = new ConsoleBuffer();
            console.Add("error", "old problem", null);
            long id = console.LastId;
            Digest before = Digest.Take(console);

            console.Add("error", "NullReferenceException: Object reference not set\nat Enemy.Update", "Assets/Scripts/Enemy.cs:42");
            console.Add("log", "tick", null);
            console.Add("warning", "careful", null);
            console.Add("error", "second", null);
            console.Add("error", "third", null);
            console.Add("error", "second", null);
            console.Add("error", "second", null);
            List<string> lines = before.Compare(false);

            Assert.AreEqual("  now: 5 errors, 1 warning; play stopped", lines[0]);
            Assert.AreEqual(4, lines.Count, "the now line, two error rows and the rest named");
            Assert.AreEqual("  error x3  second", lines[1], "the newest rows first, repeats folded");
            Assert.AreEqual("  error  third", lines[2]);
            Assert.AreEqual("  +1 more errors: ug console --after " + id, lines[3]);
        }

        [Test]
        public void A_cut_error_row_names_console_whole()
        {
            var console = new ConsoleBuffer();
            long id = console.LastId;
            Digest before = Digest.Take(console);
            console.Add("error", "NullReferenceException: Object reference not set\nat Enemy.Update", "Assets/Scripts/Enemy.cs:42");
            console.Add("error", new string('x', 200), null);
            console.Add("error", "third", null);

            List<string> cut = before.Compare(true);
            Assert.AreEqual("  +1 more errors: ug console --after " + id + " --whole", cut[3]);

            Digest again = Digest.Take(console);
            console.Add("error", "first line\nsecond line", null);
            CollectionAssert.AreEqual(
                new[] { "  now: 1 error; play stopped", "  error  first line", "  (whole text: ug console --after " + (id + 3) + " --whole)" },
                again.Compare(true));
        }

        [Test]
        public void A_repeat_of_the_entry_last_at_the_snapshot_counts_only_its_new_repeats()
        {
            var console = new ConsoleBuffer();
            console.Add("error", "boom", "Assets/A.cs:1");
            console.Add("error", "boom", "Assets/A.cs:1");
            Digest before = Digest.Take(console);

            console.Add("error", "boom", "Assets/A.cs:1");
            List<string> lines = before.Compare(false);

            Assert.AreEqual("  now: 1 error; play stopped", lines[0]);
            Assert.AreEqual("  error  boom  Assets/A.cs:1", lines[1]);
        }

        [Test]
        public void With_nothing_logged_the_digest_says_only_that_play_stopped()
        {
            var console = new ConsoleBuffer();
            console.Add("log", "before", null);
            Digest before = Digest.Take(console);

            console.Add("log", "tick", null);

            CollectionAssert.AreEqual(new[] { "  now: play stopped" }, before.Compare(true));
            CollectionAssert.AreEqual(new[] { "  now: play stopped" }, Digest.Take(null).Compare(true));
        }

        // An action that starts with a line and records its release by name.
        private static PlayAction Action(string name, List<string> released) =>
            new PlayAction(name) { Start = () => Answer.Line(name + " done"), Release = () => released.Add(name), Hold = (elapsed, frames) => false };
    }
}
