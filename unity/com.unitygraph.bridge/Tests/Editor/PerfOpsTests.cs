using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine.TestTools;

namespace UnityGraph.Bridge.Tests
{
    /// <summary>
    /// `/perf/*` (SPEC-editor-ops, Profiler) in a batch-mode editor: recorded frames, the profiler
    /// session and its file, counters, frame timing, object memory, snapshots, and the Frame
    /// Debugger, which batch mode cannot run, so only its refusals and Unity's member names.
    /// </summary>
    public class PerfOpsTests : OpsFixture
    {
        private const string RawPath = "Library/unitygraph/perf-test.raw";
        private const string SnapPath = "Library/unitygraph/perf-test.snap";

        [SetUp]
        public void ProfilerOff()
        {
            if (ProfilerDriver.enabled)
            {
                Start("/perf/profiler", "do", "stop");
            }

            Delete();
        }

        [TearDown]
        public void StopProfiler()
        {
            if (ProfilerDriver.enabled || UnityEngine.Profiling.Profiler.enableBinaryLog)
            {
                Start("/perf/profiler", "do", "stop");
            }

            Delete();
        }

        [UnityTest]
        public IEnumerator Record_profiles_the_editors_frames_and_turns_the_profiler_back_off()
        {
            var answer = new Reply[1];
            yield return Await(Start("/perf/record", "seconds", "0.3"), answer, 15);

            string body = answer[0].Body;
            StringAssert.StartsWith("{\"lines\":[\"edit mode (the editor's frames), ", body);
            StringAssert.Contains(", main thread: median ", body);
            StringAssert.Contains(" fps), p99 ", body);
            StringAssert.Contains("\"self ms a frame, share, calls a frame:\"", body);
            StringAssert.Contains("\"spikes over ", body);
            Assert.IsFalse(ProfilerDriver.enabled, "--record turns off the profiler it turned on");
        }

        [UnityTest]
        public IEnumerator Record_stops_at_the_frame_cap_before_the_time_asked()
        {
            var answer = new Reply[1];
            var clock = Stopwatch.StartNew();
            yield return Await(Start("/perf/record", "seconds", "30", "max-frames", "20"), answer, 15);

            Assert.Less(clock.Elapsed.TotalSeconds, 15, "the cap ends the recording, not the 30 s");
            StringAssert.Contains(" (the 20-frame cap: --max-frames, up to 2,000), main thread: median ", answer[0].Body);
            Assert.IsFalse(ProfilerDriver.enabled);
        }

        [UnityTest]
        public IEnumerator Frames_reads_the_held_frames_again_and_refuses_ones_it_does_not_hold()
        {
            var answer = new Reply[1];
            yield return Await(Start("/perf/record", "seconds", "0.3"), answer, 15);
            int first = ProfilerDriver.firstFrameIndex;
            int last = ProfilerDriver.lastFrameIndex;

            yield return Await(Start("/perf/record", "frames", first + "-" + last, "top", "2"), answer, 15);

            StringAssert.StartsWith("{\"lines\":[\"frames " + first + "-" + last + ", main thread: median ", answer[0].Body);
            Assert.AreEqual(Err("frames " + first + "-" + (last + 5) + " are not all held: the profiler holds frames " + first + "-" + last),
                Ask("/perf/record", "frames", first + "-" + (last + 5)));
            Assert.AreEqual(Err("--frames is a-b (first and last frame) or all"), Ask("/perf/record", "frames", "last"));
        }

        [Test]
        public void Record_refuses_bad_numbers_and_both_modes()
        {
            Assert.AreEqual(Err("--record is 0.1 to 60 seconds"), Ask("/perf/record", "seconds", "61"));
            Assert.AreEqual(Err("--record is 0.1 to 60 seconds"), Ask("/perf/record", "seconds", "soon"));
            Assert.AreEqual(Err("--top is 1 to 200"), Ask("/perf/record", "seconds", "1", "top", "0"));
            Assert.AreEqual(Err("give --record or --frames, not both"), Ask("/perf/record", "seconds", "1", "frames", "all"));
            Assert.AreEqual(Err("give --record <seconds> or --frames <a-b|all>"), Ask("/perf/record"));
            Assert.AreEqual(Err("--max-frames is 10 to 2000"), Ask("/perf/record", "seconds", "1", "max-frames", "5"));
            Assert.AreEqual(Err("--max-frames goes with --record"), Ask("/perf/record", "frames", "all", "max-frames", "100"));
            Assert.IsFalse(ProfilerDriver.enabled);
        }

        [UnityTest]
        public IEnumerator Profiler_starts_to_a_raw_file_reports_it_and_stops()
        {
            string started = Ask("/perf/profiler", "do", "start", "file", RawPath);

            StringAssert.StartsWith("{\"lines\":[\"profiler recording the editor's frames from frame ", started);
            StringAssert.EndsWith(", to " + RawPath + "; stop: ug perf profiler stop\"]}", started);
            Assert.IsTrue(ProfilerDriver.enabled);
            string status = Ask("/perf/profiler");
            StringAssert.StartsWith("{\"lines\":[\"profiler  recording the editor's frames since frame ", status);
            StringAssert.Contains("\"file  " + RawPath + "  ", status);
            StringAssert.Contains("\"allocation callstacks  off\",\"areas on  ", status);
            Assert.AreEqual(Err("the profiler is already recording to " + RawPath + " (ug perf profiler stop)"), Ask("/perf/profiler", "do", "start"));

            for (int i = 0; i < 10; i++)
            {
                yield return null;
            }

            var answer = new Reply[1];
            yield return Await(Start("/perf/profiler", "do", "stop"), answer, 5);

            // The range it names is one the profiler still holds, though frames arrive after the stop.
            string stopped = answer[0].Body;
            StringAssert.StartsWith("{\"lines\":[\"stopped after ", stopped);
            Match range = Regex.Match(stopped, "; read them: ug perf --frames (\\d+)-(\\d+)\"");
            Assert.IsTrue(range.Success, stopped);
            Assert.GreaterOrEqual(int.Parse(range.Groups[1].Value), ProfilerDriver.firstFrameIndex);
            Assert.AreEqual(ProfilerDriver.lastFrameIndex, int.Parse(range.Groups[2].Value));
            StringAssert.Contains("\"file  " + RawPath + "  ", stopped);
            StringAssert.EndsWith("  (the Profiler window loads it)\"]}", stopped);
            Assert.IsFalse(ProfilerDriver.enabled);
            Assert.IsFalse(UnityEngine.Profiling.Profiler.enableBinaryLog);
            Assert.AreEqual(Err("the profiler was not recording"), Ask("/perf/profiler", "do", "stop"));
        }

        [Test]
        public void Profiler_restores_callstacks_on_stop()
        {
            Ask("/perf/profiler", "do", "start", "callstacks", "true");

            StringAssert.Contains("\"allocation callstacks  on\"", Ask("/perf/profiler", "do", "status"));
            Start("/perf/profiler", "do", "stop");

            Assert.IsFalse(UnityEngine.Profiling.Profiler.enableAllocationCallstacks);
        }

        [Test]
        public void Profiler_files_stay_out_of_Assets_and_never_overwrite()
        {
            Assert.AreEqual(Err("Assets/x.raw is under Assets/, where Unity would import it: use Library/ or a folder outside the project"),
                Ask("/perf/profiler", "do", "start", "file", "Assets/x.raw"));
            Assert.AreEqual(Err("Packages/x.raw is under Packages/, where Unity would import it: use Library/ or a folder outside the project"),
                Ask("/perf/profiler", "do", "start", "file", "Packages/x"));
            Assert.AreEqual(Err("give a .raw path: Library/x.txt"), Ask("/perf/profiler", "do", "start", "file", "Library/x.txt"));
            Assert.AreEqual(Err("no folder NoSuchFolder"), Ask("/perf/profiler", "do", "start", "file", "NoSuchFolder/x.raw"));
            Directory.CreateDirectory(Path.GetDirectoryName(Full(RawPath)));
            File.WriteAllText(Full(RawPath), "kept");
            Assert.AreEqual(Err(RawPath + " exists; ug does not overwrite it"), Ask("/perf/profiler", "do", "start", "file", RawPath));
            Assert.AreEqual("kept", File.ReadAllText(Full(RawPath)));
            Assert.IsFalse(ProfilerDriver.enabled);
        }

        [Test]
        public void Areas_turn_on_and_off_and_refuse_unknown_names()
        {
            bool audio = UnityEngine.Profiling.Profiler.GetAreaEnabled(UnityEngine.Profiling.ProfilerArea.Audio);
            try
            {
                string body = Ask("/perf/profiler", "do", "areas", "areas", "audio=off");

                StringAssert.Contains("\"areas off  ", body);
                Assert.IsFalse(UnityEngine.Profiling.Profiler.GetAreaEnabled(UnityEngine.Profiling.ProfilerArea.Audio));
                Ask("/perf/profiler", "do", "areas", "areas", "Audio=on");
                Assert.IsTrue(UnityEngine.Profiling.Profiler.GetAreaEnabled(UnityEngine.Profiling.ProfilerArea.Audio));
                StringAssert.StartsWith("{\"error\":\"no profiler area Sound: CPU, GPU, ", Ask("/perf/profiler", "do", "areas", "areas", "Sound=on"));
                Assert.AreEqual(Err("give Audio=on or Audio=off"), Ask("/perf/profiler", "do", "areas", "areas", "Audio=maybe"));
            }
            finally
            {
                UnityEngine.Profiling.Profiler.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.Audio, audio);
            }
        }

        [Test]
        public void Counters_without_names_list_the_categories()
        {
            string body = Ask("/perf/counters");

            StringAssert.StartsWith("{\"lines\":[\"categories (ug perf counters --category <c> [--markers]; or counter names):\",\"  ", body);
            StringAssert.Contains("\"  Render  ", body);
            StringAssert.Contains("\"  Memory  ", body);
        }

        [UnityTest]
        public IEnumerator Counters_read_named_counters_over_frames()
        {
            var answer = new Reply[1];
            yield return Await(Start("/perf/counters", "names", "Draw Calls Count|total used memory"), answer, 10);

            string body = answer[0].Body;
            StringAssert.StartsWith("{\"lines\":[\"counters after ", body);
            StringAssert.Contains(" (edit mode):\",\"  Render  Draw Calls Count  ", body);
            StringAssert.Contains("\"  Memory  Total Used Memory  ", body);
            StringAssert.Contains(" MB\"", body);
        }

        [UnityTest]
        public IEnumerator Counters_list_a_category()
        {
            var answer = new Reply[1];
            yield return Await(Start("/perf/counters", "category", "memory"), answer, 10);

            StringAssert.StartsWith("{\"lines\":[\"Memory counters after ", answer[0].Body);
            StringAssert.Contains("\"  Total Used Memory  ", answer[0].Body);
        }

        [Test]
        public void Counters_refuse_unknown_names_and_categories()
        {
            StringAssert.StartsWith("{\"error\":\"no counter named Draw Calls (containing it: ", Ask("/perf/counters", "names", "Draw Calls"));
            StringAssert.StartsWith("{\"error\":\"no profiler category Nope: ", Ask("/perf/counters", "category", "Nope"));
            Assert.AreEqual(Err("--markers needs --category: ug perf counters --category Scripts --markers"), Ask("/perf/counters", "markers", "true"));
            Assert.AreEqual(Err("--markers lists a category's markers: give --category, not names"), Ask("/perf/counters", "names", "Draw Calls Count", "markers", "true"));
        }

        [UnityTest]
        public IEnumerator Timing_answers_the_last_frame_or_says_why_not()
        {
            var answer = new Reply[1];
            yield return Await(Start("/perf/timing"), answer, 10);

            string body = answer[0].Body;
            if (body.StartsWith("{\"lines\"", StringComparison.Ordinal))
            {
                StringAssert.StartsWith("{\"lines\":[\"frame timing (FrameTimingManager, last frame, edit mode):\",\"  cpu frame ", body);
            }
            else
            {
                StringAssert.IsMatch("^\\{\"error\":\"(frame timing is off|Unity gave no frame timing)", body);
            }
        }

        [Test]
        public void Memory_lists_types_and_the_largest_objects()
        {
            string body = Ask("/perf/memory", "top", "3");

            StringAssert.Contains(" objects, ", body);
            StringAssert.Contains(" (native sizes, Profiler.GetRuntimeMemorySizeLong; edit mode)\",\"by type  ", body);
            StringAssert.Contains("\"top objects:\",\"  ", body);
            StringAssert.Contains(" more: ug perf memory --top 9\"]}", body);
            Assert.AreEqual(Err("--top is 1 to 500"), Ask("/perf/memory", "top", "501"));
        }

        [Test]
        public void Memory_type_keeps_one_type_and_its_subclasses()
        {
            string body = Ask("/perf/memory", "type", "Material", "top", "500");

            StringAssert.StartsWith("{\"lines\":[\"Material and subclasses: ", body);
            StringAssert.Contains("  Material  Paint  " + MaterialPath + "\"", body);
            Assert.AreEqual(Err("no UnityEngine.Object type named Nope"), Ask("/perf/memory", "type", "Nope"));
        }

        [Test]
        public void Memory_object_sizes_a_game_object_a_component_and_an_asset()
        {
            string player = Ask("/perf/memory", "object", "Level/Player");
            StringAssert.StartsWith("{\"lines\":[\"Level/Player  GameObject  ", player);
            StringAssert.Contains("; components ", player);
            StringAssert.Contains("Transform ", player);
            StringAssert.Contains("\"children  1 object, ", player);

            StringAssert.StartsWith("{\"lines\":[\"Level/Player:Transform  Transform  ", Ask("/perf/memory", "object", "Level/Player:Transform"));

            string material = Ask("/perf/memory", "object", MaterialPath);
            StringAssert.StartsWith("{\"lines\":[\"" + MaterialPath + "  ", material);
            StringAssert.Contains(" in 1 object (loaded to measure)\",\"  ", material);
            StringAssert.Contains("  Material  Paint  (main)\"", material);

            Assert.AreEqual(Err(Error("Level/Nobody")), Ask("/perf/memory", "object", "Level/Nobody"));
            Assert.AreEqual(Err("not found: Assets/_UgOpsTests/None.mat"), Ask("/perf/memory", "object", "Assets/_UgOpsTests/None.mat"));
        }

        [UnityTest]
        public IEnumerator Snapshot_writes_a_snap_file_without_the_package()
        {
            var answer = new Reply[1];
            yield return Await(Start("/perf/snapshot", "path", SnapPath), answer, 60);

            StringAssert.StartsWith("{\"lines\":[\"" + SnapPath + "  ", answer[0].Body);
            StringAssert.EndsWith(" s (edit mode); ug perf snapshot list, ug perf snapshot compare <a> <b>\"]}", answer[0].Body);
            Assert.Greater(new FileInfo(Full(SnapPath)).Length, 1024);
            Assert.AreEqual(Err(SnapPath + " exists; ug does not overwrite it"), Ask("/perf/snapshot", "path", SnapPath));
            Assert.AreEqual(Err("Assets/x.snap is under Assets/, where Unity would import it: use Library/ or a folder outside the project"),
                Ask("/perf/snapshot", "path", "Assets/x"));
        }

        [Test]
        public void Frame_debugger_members_are_the_ones_ug_knows()
        {
            Assert.IsNull(FrameDebuggerOps.Missing());
        }

        [Test]
        public void Frame_debugger_refuses_where_unity_cannot_debug_frames_and_while_off()
        {
            Assert.AreEqual(Err("do is enable, events or disable"), Ask("/perf/frame-debugger", "do", "open"));
            Assert.AreEqual(Err("the frame debugger is off: ug perf frame-debugger enable"), Ask("/perf/frame-debugger", "do", "events"));
            Assert.AreEqual(Err("the frame debugger is off"), Ask("/perf/frame-debugger", "do", "disable"));

            string refused = Start("/perf/frame-debugger", "do", "enable")()?.Body;
            if (refused == null)
            {
                Ask("/perf/frame-debugger", "do", "disable");
                Assert.Ignore("this editor can debug frames: the end-to-end test covers enable");
            }

            Assert.AreEqual(Err("Unity's Frame Debugger is not available in this editor: it renders " + UnityEngine.SystemInfo.renderingThreadingMode
                + " (as -batchmode and -automated start it); start Unity with -force-gfx-mt"), refused);
        }

        private static Func<Reply> Start(string path, params string[] query) => Routes.Handle(Request(path, query), new ConsoleBuffer());

        private static string Full(string path) => Path.Combine(Bridge.ProjectRoot, path);

        private static void Delete()
        {
            foreach (string path in new[] { RawPath, SnapPath })
            {
                if (File.Exists(Full(path)))
                {
                    File.Delete(Full(path));
                }
            }
        }

        // Polls on editor updates until the answer comes, for at most `seconds`.
        private static IEnumerator Await(Func<Reply> poll, Reply[] answer, double seconds)
        {
            var clock = Stopwatch.StartNew();
            while ((answer[0] = poll()) == null && clock.Elapsed.TotalSeconds < seconds)
            {
                yield return null;
            }

            Assert.IsNotNull(answer[0], "no answer within " + seconds + " s");
        }
    }
}
