using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;

namespace Ungrep.Bridge
{
    /// <summary>`ug perf counters` and `ug perf timing` (SPEC-editor-ops, Profiler).</summary>
    internal static partial class PerfOps
    {
        private const int CounterCap = 40;

        /// <summary>`/perf/counters?names=a|b&amp;category=&amp;markers=&amp;all=`: recorders read over 3 frames.</summary>
        public static Func<Reply> Counters(Request request)
        {
            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            List<ProfilerRecorderDescription> all = handles.Select(ProfilerRecorderHandle.GetDescription).ToList();
            string names = request.Get("names");
            string category = request.Get("category");
            bool markers = request.Get("markers") == "true";
            bool everything = request.Get("all") == "true";
            if (names == null && category == null)
            {
                if (markers)
                {
                    return Now(Answer.Error("--markers needs --category: ug perf counters --category Scripts --markers"));
                }

                var lines = new List<string> { "categories (ug perf counters --category <c> [--markers]; or counter names):" };
                lines.AddRange(all.GroupBy(d => d.Category.Name)
                    .Select(g => new { Name = g.Key, Counters = g.Count(IsCounter), Markers = g.Count(d => !IsCounter(d)) })
                    .OrderByDescending(g => g.Counters).ThenByDescending(g => g.Markers)
                    .Select(g => "  " + g.Name + "  " + Answer.Count(g.Counters, "counter", "counters") + ", " + Answer.Count(g.Markers, "marker", "markers")));
                return Now(Answer.Lines(lines));
            }

            List<ProfilerRecorderDescription> chosen;
            if (names != null)
            {
                if (markers)
                {
                    return Now(Answer.Error("--markers lists a category's markers: give --category, not names"));
                }

                chosen = new List<ProfilerRecorderDescription>();
                foreach (string name in names.Split('|').Select(n => n.Trim()).Where(n => n.Length > 0))
                {
                    List<ProfilerRecorderDescription> found = all.Where(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)
                        && (category == null || string.Equals(d.Category.Name, category, StringComparison.OrdinalIgnoreCase))).ToList();
                    if (found.Count == 0)
                    {
                        IEnumerable<string> counterNames = all.Where(IsCounter).Select(d => d.Name).Distinct();
                        List<string> containing = counterNames.Where(n => n.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                        return Now(Answer.Error("no counter named " + name + (containing.Count > 0
                            ? " (containing it: " + string.Join(", ", containing.Take(10))
                                + (containing.Count > 10 ? ", ... " + (containing.Count - 10) + " more: ug perf counters --category <c> lists a category's" : "") + ")"
                            : Addresses.Near(name, counterNames))));
                    }

                    chosen.AddRange(found.Where(IsCounter).DefaultIfEmpty(found[0]));
                }
            }
            else
            {
                string known = all.Select(d => d.Category.Name).Distinct().FirstOrDefault(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase));
                if (known == null)
                {
                    return Now(Answer.Error("no profiler category " + category + ": " + string.Join(", ", all.Select(d => d.Category.Name).Distinct().OrderBy(c => c))));
                }

                category = known;
                chosen = all.Where(d => d.Category.Name == known && IsCounter(d) != markers).ToList();
                if (chosen.Count == 0)
                {
                    return Now(Answer.Error(known + " has no " + (markers ? "markers" : "counters") + (markers ? "" : ": ug perf counters --category " + known + " --markers")));
                }
            }

            var recorders = chosen.Select(d => new ProfilerRecorder(d.Category, d.Name, 4)).ToList();
            foreach (ProfilerRecorder recorder in recorders)
            {
                recorder.Start();
            }

            double start = EditorApplication.timeSinceStartup;
            int updates = 0;
            return () =>
            {
                updates++;
                bool enough = updates > 3 && recorders.Any(r => r.Count >= 3);
                if (!enough && EditorApplication.timeSinceStartup - start < 2)
                {
                    if (!EditorApplication.isPlaying)
                    {
                        EditorApplication.QueuePlayerLoopUpdate();
                    }

                    return null;
                }

                try
                {
                    return Read(chosen, recorders, names == null ? category : null, markers, everything);
                }
                finally
                {
                    foreach (ProfilerRecorder recorder in recorders)
                    {
                        recorder.Dispose();
                    }
                }
            };
        }

        private static Reply Read(List<ProfilerRecorderDescription> chosen, List<ProfilerRecorder> recorders, string category, bool markers, bool everything)
        {
            int frames = recorders.Max(r => r.Count);
            var lines = new List<string>
            {
                (markers ? category + " markers, time in the last frame" : category == null ? "counters" : category + " counters")
                    + " after " + Answer.Count(frames, "frame", "frames") + " (" + SceneOps.PlayState() + "):",
            };
            var values = new List<KeyValuePair<string, double>>();
            for (int i = 0; i < chosen.Count; i++)
            {
                ProfilerRecorderDescription d = chosen[i];
                ProfilerRecorder r = recorders[i];
                bool real = d.DataType == ProfilerMarkerDataType.Float || d.DataType == ProfilerMarkerDataType.Double;
                double value = r.Count > 0 ? (real ? r.LastValueAsDouble : r.LastValue) : (real ? r.CurrentValueAsDouble : r.CurrentValue);
                string label = category == null ? d.Category.Name + "  " + d.Name : d.Name;
                values.Add(new KeyValuePair<string, double>(label + "  " + Unit(value, d.UnitType), value));
            }

            int idle = 0;
            if (markers)
            {
                idle = values.Count(v => v.Value == 0);
                values = values.Where(v => v.Value != 0).OrderByDescending(v => v.Value).ToList();
            }

            int shown = everything ? values.Count : Math.Min(values.Count, CounterCap);
            lines.AddRange(values.Take(shown).Select(v => "  " + v.Key));
            if (shown < values.Count)
            {
                lines.Add("  ... " + Number(values.Count - shown) + " more: ug perf counters --category " + Quote(category) + (markers ? " --markers" : "") + " --all");
            }

            if (idle > 0)
            {
                lines.Add("  " + Answer.Count(idle, "marker", "markers") + " did not run");
            }

            return Answer.Lines(lines);
        }

        private static bool IsCounter(ProfilerRecorderDescription description) => (description.Flags & MarkerFlags.Counter) != 0;

        private static string Unit(double value, ProfilerMarkerDataUnit unit)
        {
            switch (unit)
            {
                case ProfilerMarkerDataUnit.TimeNanoseconds:
                    return Ms(value / 1e6) + " ms";
                case ProfilerMarkerDataUnit.Bytes:
                    return Bytes(value);
                case ProfilerMarkerDataUnit.Percent:
                    return value.ToString("0.#", CultureInfo.InvariantCulture) + "%";
                case ProfilerMarkerDataUnit.FrequencyHz:
                    return value.ToString("0.#", CultureInfo.InvariantCulture) + " Hz";
                default:
                    return value == Math.Floor(value) && Math.Abs(value) < 1e15 ? Number((long)value) : value.ToString("0.###", CultureInfo.InvariantCulture);
            }
        }

        private static string Quote(string text) => text != null && text.IndexOf(' ') >= 0 ? "\"" + text + "\"" : text;

        /// <summary>`/perf/timing`: FrameTimingManager over 5 frames, the last frame's 12 fields.</summary>
        public static Func<Reply> Timing(Request request)
        {
            if (!FrameTimingManager.IsFeatureEnabled())
            {
                return Now(Answer.Error("frame timing is off: turn on Frame Timing Stats in Player Settings (ug set ProjectSettings/ProjectSettings.asset enableFrameTimingStats true), then enter play mode again"));
            }

            double start = EditorApplication.timeSinceStartup;
            int captures = 0;
            return () =>
            {
                FrameTimingManager.CaptureFrameTimings();
                captures++;
                if (captures < 5 && EditorApplication.timeSinceStartup - start < 2)
                {
                    if (!EditorApplication.isPlaying)
                    {
                        EditorApplication.QueuePlayerLoopUpdate();
                    }

                    return null;
                }

                var timings = new FrameTiming[1];
                if (FrameTimingManager.GetLatestTimings(1, timings) == 0)
                {
                    return Answer.Error("Unity gave no frame timing (no frame finished in " + Seconds(EditorApplication.timeSinceStartup - start) + " s)");
                }

                FrameTiming t = timings[0];
                ulong frequency = FrameTimingManager.GetCpuTimerFrequency();
                return Answer.Lines(new[]
                {
                    "frame timing (FrameTimingManager, last frame, " + SceneOps.PlayState() + "):",
                    "  cpu frame " + Ms(t.cpuFrameTime) + " ms, main thread " + Ms(t.cpuMainThreadFrameTime) + " ms, present wait " + Ms(t.cpuMainThreadPresentWaitTime)
                        + " ms, render thread " + Ms(t.cpuRenderThreadFrameTime) + " ms, gpu " + (t.gpuFrameTime > 0 ? Ms(t.gpuFrameTime) + " ms" : "not measured"),
                    "  after the frame start: first submit " + After(t.firstSubmitTimestamp, t.frameStartTimestamp, frequency) + ", present called "
                        + After(t.cpuTimePresentCalled, t.frameStartTimestamp, frequency) + ", frame complete " + After(t.cpuTimeFrameComplete, t.frameStartTimestamp, frequency),
                    "  frame start timestamp " + t.frameStartTimestamp.ToString(CultureInfo.InvariantCulture) + " (" + frequency.ToString("N0", CultureInfo.InvariantCulture)
                        + " a second), width scale " + t.widthScale.ToString("0.##", CultureInfo.InvariantCulture) + ", height scale "
                        + t.heightScale.ToString("0.##", CultureInfo.InvariantCulture) + ", sync interval " + t.syncInterval,
                });
            };
        }

        private static string After(ulong timestamp, ulong frameStart, ulong frequency) =>
            timestamp == 0 || timestamp < frameStart || frequency == 0 ? "not measured" : Ms((timestamp - frameStart) * 1000.0 / frequency) + " ms";
    }
}
