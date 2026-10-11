using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine.Profiling;

namespace Ungrep.Bridge
{
    /// <summary>
    /// `ug perf --record`, `--frames` and `ug perf profiler` (SPEC-editor-ops, Profiler): frames the
    /// profiler records, read back through <see cref="HierarchyFrameDataView"/>, and the session that
    /// records them (its `.raw` file, allocation callstacks, areas). Profiler state is editor state:
    /// nothing goes on the Undo stack and nothing is saved.
    /// </summary>
    internal static partial class PerfOps
    {
        // Set while `--record` has the profiler on that it turned on, so a domain reload in the
        // middle (a compile, entering play mode) turns it back off on the next bridge start.
        private const string RecordKey = "ungrep.perf.record";

        // `ug perf profiler start`: start frame, start time, profileEditor and callstacks before.
        private const string SessionKey = "ungrep.perf.session";

        private const string ProfilerFolder = "Library/ungrep/profiler";

        // Analysis slice per editor update, so the editor stays usable while it reads 2000 frames.
        private const double SliceMs = 50;

        // Frames a `--record` takes at most unless --max-frames says otherwise: the Profiler window's
        // default history. The profiler's memory grows with the frames it holds and its pool never
        // shrinks, so an editor that updates thousands of times a second (batch mode) must not record
        // for the whole time asked.
        private const int DefaultMaxFrames = 300;
        private const int MaxFrames = 2000;

        /// <summary>On bridge start: ends a `--record` a domain reload cut off.</summary>
        public static void Restore()
        {
            string before = SessionState.GetString(RecordKey, "");
            if (before.Length == 0)
            {
                return;
            }

            SessionState.EraseString(RecordKey);
            ProfilerDriver.enabled = false;
            ProfilerDriver.profileEditor = before == "editor";
        }

        /// <summary>`/perf/record?seconds=&amp;max-frames=&amp;frames=&amp;top=`: records for a while, or reads held frames; then the digest.</summary>
        public static Func<Reply> Record(Request request)
        {
            string secondsText = request.Get("seconds");
            string framesText = request.Get("frames");
            if (!Whole(request, "top", 1, 200, out int? top, out Reply bad) || !Whole(request, "max-frames", 10, MaxFrames, out int? maxFrames, out bad))
            {
                return Now(bad);
            }

            if (secondsText != null && framesText != null)
            {
                return Now(Answer.Error("give --record or --frames, not both"));
            }

            if (maxFrames != null && secondsText == null)
            {
                return Now(Answer.Error("--max-frames goes with --record"));
            }

            if (framesText != null)
            {
                Reply refused = HeldRange(framesText, out int first, out int last);
                if (refused != null)
                {
                    return Now(refused);
                }

                var frames = new FrameDigest(first, last, first);
                return () => frames.Step() ? frames.Answer("frames " + first + "-" + last, top) : null;
            }

            if (secondsText == null)
            {
                return Now(Answer.Error("give --record <seconds> or --frames <a-b|all>"));
            }

            if (!double.TryParse(secondsText, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds < 0.1 || seconds > 60)
            {
                return Now(Answer.Error("--record is 0.1 to 60 seconds"));
            }

            bool playing = EditorApplication.isPlaying;
            bool turnedOn = !ProfilerDriver.enabled;
            bool editorBefore = ProfilerDriver.profileEditor;
            int after = ProfilerDriver.lastFrameIndex;
            if (turnedOn)
            {
                SessionState.SetString(RecordKey, editorBefore ? "editor" : "player");
                ProfilerDriver.profileEditor = !playing;
                ProfilerDriver.enabled = true;
            }

            int cap = maxFrames ?? DefaultMaxFrames;
            double start = EditorApplication.timeSinceStartup;
            double stopped = 0;
            double settled = 0;
            int lastSeen = -1;
            int updates = 0;
            bool capped = false;
            FrameDigest digest = null;
            string mode = playing ? "play mode" : "edit mode (the editor's frames)";
            return () =>
            {
                double now = EditorApplication.timeSinceStartup;
                if (stopped == 0)
                {
                    // Polled once an editor update: counting updates stops in time where the
                    // profiler hands its frames over late.
                    capped = ++updates >= cap || ProfilerDriver.lastFrameIndex - after >= cap;
                    if (now - start < seconds && !capped)
                    {
                        return null;
                    }

                    stopped = now;
                    if (turnedOn)
                    {
                        ProfilerDriver.enabled = false;
                        ProfilerDriver.profileEditor = editorBefore;
                        SessionState.EraseString(RecordKey);
                    }
                }

                if (digest == null)
                {
                    // A batch-mode editor hands frames over late: wait until the last index holds still.
                    int last = ProfilerDriver.lastFrameIndex;
                    if (turnedOn && last != lastSeen)
                    {
                        lastSeen = last;
                        settled = now;
                    }

                    if (turnedOn && now - settled < 0.1 && now - stopped < 2)
                    {
                        return null;
                    }

                    if (last <= after)
                    {
                        return Answer.Error("the profiler recorded no frames in " + Seconds(stopped - start) + " s"
                            + (playing ? "" : " (in edit mode the editor records a frame when it updates)"));
                    }

                    int first = Math.Max(after + 1, ProfilerDriver.firstFrameIndex);
                    digest = new FrameDigest(first, last, after + 1);
                }

                return digest.Step()
                    ? digest.Answer(mode + ", " + Number(digest.Recorded) + " frames in " + Seconds(stopped - start) + " s"
                        + (capped ? " (the " + Number(cap) + "-frame cap: --max-frames, up to " + Number(MaxFrames) + ")" : ""), top)
                    : null;
            };
        }

        /// <summary>`/perf/profiler?do=status|start|stop|areas`.</summary>
        public static Func<Reply> Profiler(Request request)
        {
            switch (request.Get("do") ?? "status")
            {
                case "status":
                    return Now(Answer.Lines(Status()));
                case "start":
                    return Now(Start(request.Get("file"), request.Get("callstacks") == "true", request.Get("editor") == "true"));
                case "stop":
                    return Stop();
                case "areas":
                    return Now(Areas(request.Get("areas")));
                default:
                    return Now(Answer.Error("do is status, start, stop or areas"));
            }
        }

        private static List<string> Status()
        {
            var lines = new List<string>();
            string held = Held();
            if (ProfilerDriver.enabled)
            {
                string since = Session(out int startFrame, out _, out _, out _) ? " since frame " + startFrame : " (started outside ug)";
                lines.Add("profiler  recording " + Target() + since + (held == null ? "" : " (holds " + held + ")"));
            }
            else
            {
                lines.Add("profiler  off" + (held == null ? ", holds no frames" : " (holds frames " + held + ": ug perf --frames all)"));
            }

            if (UnityEngine.Profiling.Profiler.enableBinaryLog && !string.IsNullOrEmpty(UnityEngine.Profiling.Profiler.logFile))
            {
                lines.Add("file  " + Shown(UnityEngine.Profiling.Profiler.logFile) + "  " + FileSize(UnityEngine.Profiling.Profiler.logFile));
            }

            lines.Add("allocation callstacks  " + (UnityEngine.Profiling.Profiler.enableAllocationCallstacks ? "on" : "off"));
            lines.AddRange(AreaLines());
            return lines;
        }

        private static Reply Start(string file, bool callstacks, bool editor)
        {
            bool recording = ProfilerDriver.enabled;
            bool logging = UnityEngine.Profiling.Profiler.enableBinaryLog && !string.IsNullOrEmpty(UnityEngine.Profiling.Profiler.logFile);
            if (recording && (file == null || logging))
            {
                return Answer.Error(logging
                    ? "the profiler is already recording to " + Shown(UnityEngine.Profiling.Profiler.logFile) + " (ug perf profiler stop)"
                    : "the profiler is already recording (ug perf profiler stop; ug perf profiler start --file adds a .raw file)");
            }

            string path = null;
            if (file != null)
            {
                path = OutputPath(file, ProfilerFolder, ".raw", out string error);
                if (path == null)
                {
                    return Answer.Error(error);
                }
            }

            if (!recording)
            {
                SessionState.SetString(SessionKey, string.Join("|",
                    (ProfilerDriver.lastFrameIndex + 1).ToString(CultureInfo.InvariantCulture),
                    EditorApplication.timeSinceStartup.ToString("R", CultureInfo.InvariantCulture),
                    ProfilerDriver.profileEditor ? "editor" : "player",
                    UnityEngine.Profiling.Profiler.enableAllocationCallstacks ? "on" : "off"));
                ProfilerDriver.profileEditor = editor || !EditorApplication.isPlaying;
            }

            if (path != null)
            {
                UnityEngine.Profiling.Profiler.logFile = path;
                UnityEngine.Profiling.Profiler.enableBinaryLog = true;
            }

            if (callstacks)
            {
                UnityEngine.Profiling.Profiler.enableAllocationCallstacks = true;
            }

            ProfilerDriver.enabled = true;
            return Answer.Line("profiler recording " + Target() + " from frame " + (ProfilerDriver.lastFrameIndex + 1)
                + (path == null ? "" : ", to " + Shown(path)) + (callstacks ? ", allocation callstacks on" : "")
                + "; stop: ug perf profiler stop");
        }

        // Stops at once; answers once the frame range holds still, since a batch-mode editor hands
        // over thousands of frames after the profiler is off and they push older ones out.
        private static Func<Reply> Stop()
        {
            string file = UnityEngine.Profiling.Profiler.enableBinaryLog ? UnityEngine.Profiling.Profiler.logFile : null;
            if (!ProfilerDriver.enabled && string.IsNullOrEmpty(file))
            {
                return Now(Answer.Error("the profiler was not recording"));
            }

            bool ours = Session(out int startFrame, out double startTime, out bool editorBefore, out bool callstacksBefore);
            UnityEngine.Profiling.Profiler.enableBinaryLog = false;
            UnityEngine.Profiling.Profiler.logFile = "";
            UnityEngine.Profiling.Profiler.enableAllocationCallstacks = ours && callstacksBefore;
            ProfilerDriver.enabled = false;
            if (ours)
            {
                ProfilerDriver.profileEditor = editorBefore;
                SessionState.EraseString(SessionKey);
            }

            double stopped = EditorApplication.timeSinceStartup;
            double settled = 0;
            int lastSeen = -1;
            return () =>
            {
                double now = EditorApplication.timeSinceStartup;
                int last = ProfilerDriver.lastFrameIndex;
                if (last != lastSeen)
                {
                    lastSeen = last;
                    settled = now;
                }

                if (now - settled < 0.1 && now - stopped < 2)
                {
                    return null;
                }

                int first = ProfilerDriver.firstFrameIndex;
                var lines = new List<string>();
                if (ours && last >= startFrame)
                {
                    int from = Math.Max(startFrame, first);
                    lines.Add("stopped after " + Seconds(stopped - startTime) + " s: frames " + startFrame + "-" + last
                        + (from > startFrame ? " (the profiler holds " + from + "-" + last + ")" : "") + "; read them: ug perf --frames " + from + "-" + last);
                }
                else
                {
                    string held = Held();
                    lines.Add("stopped" + (ours ? " after " + Seconds(stopped - startTime) + " s, no frames recorded" : "")
                        + (held == null ? "" : "; the profiler holds frames " + held + ": ug perf --frames all"));
                }

                if (!string.IsNullOrEmpty(file))
                {
                    lines.Add("file  " + Shown(file) + "  " + FileSize(file) + "  (the Profiler window loads it)");
                }

                return Answer.Lines(lines);
            };
        }

        private static Reply Areas(string given)
        {
            if (!string.IsNullOrEmpty(given))
            {
                string[] names = Enum.GetNames(typeof(ProfilerArea));
                var changes = new List<KeyValuePair<ProfilerArea, bool>>();
                foreach (string pair in given.Split(','))
                {
                    int equals = pair.IndexOf('=');
                    string name = equals < 0 ? pair.Trim() : pair.Substring(0, equals).Trim();
                    string value = equals < 0 ? "" : pair.Substring(equals + 1).Trim().ToLowerInvariant();
                    string area = names.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                    if (area == null)
                    {
                        return Answer.Error("no profiler area " + name + ": " + string.Join(", ", names));
                    }

                    if (value != "on" && value != "off" && value != "true" && value != "false")
                    {
                        return Answer.Error("give " + area + "=on or " + area + "=off");
                    }

                    changes.Add(new KeyValuePair<ProfilerArea, bool>((ProfilerArea)Enum.Parse(typeof(ProfilerArea), area), value == "on" || value == "true"));
                }

                foreach (KeyValuePair<ProfilerArea, bool> change in changes)
                {
                    UnityEngine.Profiling.Profiler.SetAreaEnabled(change.Key, change.Value);
                }
            }

            return Answer.Lines(AreaLines());
        }

        private static IEnumerable<string> AreaLines()
        {
            ProfilerArea[] areas = (ProfilerArea[])Enum.GetValues(typeof(ProfilerArea));
            string on = string.Join(", ", areas.Where(a => UnityEngine.Profiling.Profiler.GetAreaEnabled(a)));
            string off = string.Join(", ", areas.Where(a => !UnityEngine.Profiling.Profiler.GetAreaEnabled(a)));
            yield return "areas on  " + (on.Length == 0 ? "none" : on);
            yield return "areas off  " + (off.Length == 0 ? "none" : off);
        }

        // What the profiler records: the game in play mode, else the editor.
        private static string Target() =>
            ProfilerDriver.profileEditor ? "the editor's frames" : EditorApplication.isPlaying ? "play mode frames" : "play mode frames (none until play mode)";

        // `first-last` of the frames the profiler holds, or null.
        private static string Held()
        {
            int first = ProfilerDriver.firstFrameIndex;
            int last = ProfilerDriver.lastFrameIndex;
            return first < 0 || last < first ? null : first + "-" + last;
        }

        private static Reply HeldRange(string text, out int first, out int last)
        {
            first = ProfilerDriver.firstFrameIndex;
            last = ProfilerDriver.lastFrameIndex;
            if (first < 0 || last < first)
            {
                return Answer.Error("the profiler holds no frames: ug perf --record <seconds>");
            }

            if (text == "all")
            {
                return null;
            }

            string[] parts = text.Split('-');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int from)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int to) || to < from)
            {
                return Answer.Error("--frames is a-b (first and last frame) or all");
            }

            if (from < first || to > last)
            {
                return Answer.Error("frames " + from + "-" + to + " are not all held: the profiler holds frames " + first + "-" + last);
            }

            first = from;
            last = to;
            return null;
        }

        private static bool Session(out int startFrame, out double startTime, out bool editorBefore, out bool callstacksBefore)
        {
            string[] parts = SessionState.GetString(SessionKey, "").Split('|');
            startFrame = 0;
            startTime = 0;
            editorBefore = parts.Length > 2 && parts[2] == "editor";
            callstacksBefore = parts.Length > 3 && parts[3] == "on";
            return parts.Length == 4
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out startFrame)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out startTime);
        }

        /// <summary>
        /// The digest of a frame range, built a slice at a time: per main-thread frame, the inverted
        /// hierarchy's top items are markers with their self time and calls, and the callers under
        /// its `GC.Alloc` item carry the bytes they allocated.
        /// </summary>
        private sealed class FrameDigest
        {
            private readonly int _first;
            private readonly int _last;
            private readonly int _wanted;
            private readonly Dictionary<string, double[]> _markers = new Dictionary<string, double[]>(StringComparer.Ordinal);
            private readonly Dictionary<string, double[]> _callers = new Dictionary<string, double[]>(StringComparer.Ordinal);
            private readonly List<KeyValuePair<int, float>> _times = new List<KeyValuePair<int, float>>();
            private readonly Dictionary<int, string> _tops = new Dictionary<int, string>();
            private readonly HashSet<int> _cutTops = new HashSet<int>();
            private readonly List<int> _items = new List<int>();
            private readonly List<int> _children = new List<int>();
            private int _next;
            private double _gc;

            // wanted: the first frame asked for; frames before _first had already been dropped.
            public FrameDigest(int first, int last, int wanted)
            {
                _first = first;
                _last = last;
                _wanted = wanted;
                _next = first;
            }

            public int Recorded => _last - _wanted + 1;

            public bool Step()
            {
                var clock = Stopwatch.StartNew();
                while (_next <= _last && clock.Elapsed.TotalMilliseconds < SliceMs)
                {
                    Read(_next++);
                }

                return _next > _last;
            }

            private void Read(int frame)
            {
                using (HierarchyFrameDataView view = ProfilerDriver.GetHierarchyFrameDataView(frame, 0,
                    HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName | HierarchyFrameDataView.ViewModes.InvertHierarchy,
                    HierarchyFrameDataView.columnSelfTime, false))
                {
                    if (view == null || !view.valid)
                    {
                        return;
                    }

                    _times.Add(new KeyValuePair<int, float>(frame, view.frameTimeMs));
                    view.GetItemChildren(view.GetRootItemID(), _items);
                    var top = new List<KeyValuePair<string, float>>();
                    foreach (int item in _items)
                    {
                        string name = view.GetItemName(item);
                        float self = view.GetItemColumnDataAsFloat(item, HierarchyFrameDataView.columnSelfTime);
                        float calls = view.GetItemColumnDataAsFloat(item, HierarchyFrameDataView.columnCalls);
                        Add(_markers, name, self, calls);
                        top.Add(new KeyValuePair<string, float>(name, self));
                        if (name == "GC.Alloc")
                        {
                            _gc += view.GetItemColumnDataAsFloat(item, HierarchyFrameDataView.columnGcMemory);
                            view.GetItemChildren(item, _children);
                            foreach (int caller in _children)
                            {
                                Add(_callers, view.GetItemName(caller), view.GetItemColumnDataAsFloat(caller, HierarchyFrameDataView.columnGcMemory), 1);
                            }
                        }
                    }

                    _tops[frame] = string.Join(", ", top.OrderByDescending(p => p.Value).Take(3).Select(p => p.Key + " " + Ms(p.Value)));
                    if (top.Count > 3)
                    {
                        _cutTops.Add(frame);
                    }
                }
            }

            // Totals per name: [sum, frames seen, second sum (calls)].
            private static void Add(Dictionary<string, double[]> totals, string name, double value, double second)
            {
                if (!totals.TryGetValue(name, out double[] sums))
                {
                    totals[name] = sums = new double[3];
                }

                sums[0] += value;
                sums[1]++;
                sums[2] += second;
            }

            public Reply Answer(string what, int? topGiven)
            {
                int n = _times.Count;
                if (n == 0)
                {
                    return Ungrep.Bridge.Answer.Error("the profiler has no main-thread data for frames " + _first + "-" + _last);
                }

                int markersTop = topGiven ?? 10;
                int callersTop = topGiven ?? 5;
                int spikesTop = topGiven ?? 3;
                string more = "ug perf --frames " + _first + "-" + _last + " --top " + Math.Min(200, markersTop * 3);
                float[] sorted = _times.Select(t => t.Value).OrderBy(t => t).ToArray();
                float median = n % 2 == 1 ? sorted[n / 2] : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2;
                float p99 = sorted[Math.Max(0, (int)Math.Ceiling(n * 0.99) - 1)];
                double mean = sorted.Average(t => (double)t);
                var lines = new List<string>
                {
                    what + ", main thread: median " + Ms(median) + " ms (" + (median > 0 ? Math.Round(1000 / median).ToString(CultureInfo.InvariantCulture) : "?")
                        + " fps), p99 " + Ms(p99) + " ms, max " + Ms(sorted[n - 1]) + " ms; GC " + Bytes(_gc / n) + " a frame",
                };
                if (_first > _wanted)
                {
                    lines.Add("analysed the last " + Number(_last - _first + 1) + " of " + Number(Recorded) + " frames (the profiler keeps "
                        + Number(_last - ProfilerDriver.firstFrameIndex + 1) + ": record shorter for all)");
                }

                lines.Add("self ms a frame, share, calls a frame:");
                bool playing = EditorApplication.isPlaying;
                var markers = _markers.OrderByDescending(m => m.Value[0]).ToList();
                foreach (KeyValuePair<string, double[]> m in markers.Take(markersTop))
                {
                    double average = m.Value[0] / n;
                    lines.Add("  " + m.Key + "  " + Ms(average) + "  " + (mean > 0 ? Math.Round(average / mean * 100).ToString(CultureInfo.InvariantCulture) : "0") + "%  "
                        + Calls(m.Value[2] / n) + (m.Key == "EditorLoop" && playing ? "  (editor, not in a build)" : ""));
                }

                More(lines, markers.Count, markersTop, more);
                var callers = _callers.OrderByDescending(c => c.Value[0]).ToList();
                lines.Add(callers.Count == 0 ? "GC allocated: none" : "GC allocated a frame, by caller:");
                foreach (KeyValuePair<string, double[]> c in callers.Take(callersTop))
                {
                    lines.Add("  " + c.Key + "  " + Bytes(c.Value[0] / n) + "  (in " + Number((int)c.Value[1]) + " frames)");
                }

                More(lines, callers.Count, callersTop, more);
                float limit = median * 2;
                var spikes = _times.Where(t => t.Value > limit).OrderByDescending(t => t.Value).ToList();
                lines.Add("spikes over " + Ms(limit) + " ms (2x median): " + (spikes.Count == 0 ? "none" : Number(spikes.Count) + " frames; slowest:"));
                foreach (KeyValuePair<int, float> s in spikes.Take(spikesTop))
                {
                    lines.Add("  frame " + s.Key + "  " + Ms(s.Value) + " ms  " + _tops[s.Key]);
                }

                More(lines, spikes.Count, spikesTop, more);
                if (spikes.Take(spikesTop).Any(s => _cutTops.Contains(s.Key)))
                {
                    int slowest = spikes[0].Key;
                    lines.Add("  a spike's other markers: ug perf --frames " + slowest + "-" + slowest);
                }
                return Ungrep.Bridge.Answer.Lines(lines);
            }

            private static void More(List<string> lines, int count, int shown, string more)
            {
                if (count > shown)
                {
                    lines.Add("  ... " + Number(count - shown) + " more: " + more);
                }
            }

            private static string Calls(double calls) => calls.ToString(calls >= 10 ? "0" : "0.#", CultureInfo.InvariantCulture);
        }

        // `name` as a whole number in [min, max], or null when absent.
        private static bool Whole(Request request, string name, int min, int max, out int? value, out Reply error)
        {
            value = null;
            error = null;
            string text = request.Get(name);
            if (text == null)
            {
                return true;
            }

            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) || number < min || number > max)
            {
                error = Answer.Error("--" + name + " is " + min + " to " + max);
                return false;
            }

            value = number;
            return true;
        }

        private static Func<Reply> Now(Reply reply) => () => reply;

        private static string Ms(double ms) => ms.ToString(ms >= 100 ? "0" : ms >= 10 ? "0.0" : "0.00", CultureInfo.InvariantCulture);

        private static string Seconds(double seconds) => seconds.ToString("0.0", CultureInfo.InvariantCulture);

        private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

        /// <summary>Bytes as B, KB, MB or GB with one decimal, as the client's snapshot answers.</summary>
        internal static string Bytes(double bytes) =>
            bytes < 1024 ? Math.Round(bytes).ToString(CultureInfo.InvariantCulture) + " B"
            : bytes < 1024 * 1024 ? (bytes / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " KB"
            : bytes < 1024.0 * 1024 * 1024 ? (bytes / (1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
            : (bytes / (1024.0 * 1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " GB";

        private static string FileSize(string path)
        {
            var info = new FileInfo(path);
            return info.Exists ? Bytes(info.Length) : "not written yet";
        }
    }
}
