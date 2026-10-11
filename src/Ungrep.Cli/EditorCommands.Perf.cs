using System.CommandLine;
using System.Globalization;
using Ungrep.EditorLink;

namespace Ungrep.Cli;

/// <summary>
/// `ug perf` (SPEC-editor-ops, Endpoints and Profiler): the one-screen summary, and with bridge 0.8.0
/// recorded frames, the profiler session, counters, frame timing, object memory, memory snapshots
/// and the Frame Debugger. `snapshot list` and `compare` read the files here, with Unity closed.
/// </summary>
internal static partial class EditorCommands
{
    // A snapshot can take a while in a big project; the bridge waits 60 s for Unity's callback.
    private static readonly TimeSpan _snapshotTimeout = TimeSpan.FromSeconds(90);

    private static Command PerfCommand()
    {
        var record = new Option<double?>("--record") { Description = "Record this many seconds (0.1 to 60) of frames and print the top markers by self time, GC allocations and spikes." };
        var maxFrames = new Option<int?>("--max-frames") { Description = "With --record: stop at this many frames, 10 to 2000. Default: 300 (the profiler's memory grows with the frames it holds)." };
        Option<string?> frames = Text("--frames", "Analyse frames the profiler holds instead of recording: a-b or all (the more line of --record).");
        var top = new Option<int?>("--top") { Description = "With --record or --frames: how many markers and callers, 1 to 200. Default: 10 markers, 5 callers." };
        Command perf = Ops("perf", "perf", "Play state, frame render stats (play mode), memory, object and asset counts; --record <s> profiles frames.", [record, maxFrames, frames, top], r =>
        {
            if (r.GetValue(record) == null && r.GetValue(frames) == null && r.GetValue(top) == null && r.GetValue(maxFrames) == null)
            {
                return new OpRequest("/stats");
            }

            double seconds = r.GetValue(record) ?? 0;
            return PerfRequest("/perf/record", r.GetValue(record) != null ? "--record" : "--frames", TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 60) + 60),
                ("seconds", PerfNumber(r.GetValue(record))), ("max-frames", PerfNumber(r.GetValue(maxFrames))), ("frames", r.GetValue(frames)), ("top", PerfNumber(r.GetValue(top))));
        });

        perf.Subcommands.Add(ProfilerCommand());

        var names = new Argument<string[]>("names")
        {
            Description = "Counter names in any category: \"Draw Calls Count\", \"Total Used Memory\". Default: the categories, or --category's counters.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        Option<string?> category = Text("--category", "Every counter of one category: Render, Memory, Physics, Scripts, ...");
        Option<bool> markers = Flag("--markers", "With --category: its markers (time in the last frame), slowest first, instead of its counters.");
        Option<bool> all = Flag("--all", "Every line, not the first 40.");
        perf.Subcommands.Add(Ops("counters", "perf counters", "Profiler counters and markers read over 3 frames through ProfilerRecorder.", [names, category, markers, all], r =>
            PerfRequest("/perf/counters", null, OpTimeout,
                ("names", (r.GetValue(names) ?? []) is { Length: > 0 } given ? string.Join('|', given) : null),
                ("category", r.GetValue(category)),
                ("markers", True(r.GetValue(markers))),
                ("all", True(r.GetValue(all))))));

        perf.Subcommands.Add(Ops("timing", "perf timing", "The last frame's FrameTimingManager timings: CPU, main and render thread, GPU, present and sync.", [], _ =>
            PerfRequest("/perf/timing", null, OpTimeout)));

        var memoryTop = new Option<int?>("--top") { Description = "How many objects and types, 1 to 500 (default 10 objects, 8 types); with --object how many sub-assets and used assets (default 20 and 5, bridge 0.9.0)." };
        Option<string?> type = Text("--type", "Only this type and its subclasses: Texture, Mesh, AudioClip, ...");
        Option<string?> memoryObject = Text("--object", "An object address (Player/Gun, Main:Enemy#2, Assets/X.prefab:Child) or an asset path: its size with components, children, sub-assets and the assets it uses.");
        perf.Subcommands.Add(Ops("memory", "perf memory", "Loaded objects by native size: the total, the largest types and the top objects; or one object.", [memoryTop, type, memoryObject], r =>
        {
            bool objectTop = r.GetValue(memoryObject) != null && r.GetValue(memoryTop) != null;
            return PerfRequest("/perf/memory", objectTop ? "--object --top" : null, OpTimeout, ("top", PerfNumber(r.GetValue(memoryTop))), ("type", r.GetValue(type)), ("object", r.GetValue(memoryObject))) with { Full = objectTop };
        }));

        perf.Subcommands.Add(SnapshotCommand());
        perf.Subcommands.Add(FrameDebuggerCommand());
        return perf;

        static Option<string?> Text(string name, string description) => new(name) { Description = description };
        static Option<bool> Flag(string name, string description) => new(name) { Description = description };
        static string? True(bool flag) => flag ? "true" : null;
    }

    // `ug perf profiler`: the session, its .raw file, callstacks and areas.
    private static Command ProfilerCommand()
    {
        Command profiler = Ops("profiler", "perf profiler", "The profiler's state: recording, its file, callstacks and areas (same as status).", [], _ => Profile("status"));
        profiler.Subcommands.Add(Ops("status", "perf profiler status", "The profiler's state: recording, the frames it holds, its file, callstacks and areas.", [], _ => Profile("status")));

        var file = new Option<string?>("--file")
        {
            Description = "Also write Unity's binary log, which the Profiler window loads: a .raw path outside Assets/ and Packages/. Default: Library/ungrep/profiler/<time>.raw.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var callstacks = new Option<bool>("--callstacks") { Description = "Record a call stack with each GC allocation (slower)." };
        var editor = new Option<bool>("--editor") { Description = "In play mode, profile the editor too (the Profiler window's Edit Mode target)." };
        profiler.Subcommands.Add(Ops("start", "perf profiler start", "Start recording frames, optionally to a .raw file.", [file, callstacks, editor], r =>
            Profile("start",
                ("file", r.GetResult(file) == null ? null : r.GetValue(file) ?? ""),
                ("callstacks", r.GetValue(callstacks) ? "true" : null),
                ("editor", r.GetValue(editor) ? "true" : null))));

        profiler.Subcommands.Add(Ops("stop", "perf profiler stop", "Stop recording and close the .raw file; prints the frames and the command that reads them.", [], _ => Profile("stop")));

        var pairs = new Argument<string[]>("areas")
        {
            Description = "Area=on|off pairs: CPU=on GPU=off. Areas: CPU, GPU, Rendering, Memory, Audio, Video, Physics, Physics2D, NetworkMessages, NetworkOperations, UI, UIDetails, GlobalIllumination, VirtualTexturing. Default: list them.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        profiler.Subcommands.Add(Ops("areas", "perf profiler areas", "List the profiler areas, or turn them on and off.", [pairs], r =>
            Profile("areas", ("areas", (r.GetValue(pairs) ?? []) is { Length: > 0 } given ? string.Join(',', given) : null))));
        return profiler;

        static OpRequest Profile(string action, params (string Name, string? Value)[] parameters) =>
            PerfRequest("/perf/profiler", null, OpTimeout, [("do", action), .. parameters]);
    }

    // `ug perf snapshot`: take in the editor; list and compare here.
    private static Command SnapshotCommand()
    {
        var snapshot = new Command("snapshot", "Memory snapshots: take one in the editor, list them, compare two (no package, Unity may be closed).");

        var path = new Argument<string?>("path") { Description = "Where to write the .snap, outside Assets/ and Packages/. Default: Library/ungrep/snapshots/<time>.snap.", Arity = ArgumentArity.ZeroOrOne };
        snapshot.Subcommands.Add(Ops("take", "perf snapshot take", "Take a memory snapshot (managed objects, native objects and allocations) and print its path and size.", [path], r =>
            PerfRequest("/perf/snapshot", null, _snapshotTimeout, ("path", r.GetValue(path)))));

        var folder = new Option<string?>("--folder") { Description = "Look here instead of " + SnapshotAnswers.Folder + "/ and " + SnapshotAnswers.PackageFolder + "/." };
        var all = new Option<bool>("--all") { Description = "Every snapshot, not the newest 20." };
        snapshot.Subcommands.Add(Command("list", "Snapshots newest first: size, time, product, Unity version, platform, memory used, native objects.", [folder, all], (root, r) =>
        {
            _opFailed = false;
            return (0, SnapshotAnswers.List(root, r.GetValue(folder), r.GetValue(all)));
        }));

        var a = new Argument<string>("a") { Description = "The earlier snapshot: a path, or a file name in " + SnapshotAnswers.Folder + "/." };
        var b = new Argument<string>("b") { Description = "The later snapshot." };
        var top = new Option<int>("--top") { Description = "How many types and objects per list, 1 to 200. Default: 10.", DefaultValueFactory = _ => 10 };
        snapshot.Subcommands.Add(Command("compare", "What changed between two snapshots: memory totals, native objects by type, objects new, gone and grown.", [a, b, top], (root, r) =>
        {
            _opFailed = false;
            int count = r.GetValue(top);
            if (count < 1 || count > 200)
            {
                return Failed("--top is 1 to 200\n");
            }

            string? first = SnapshotAnswers.Resolve(root, r.GetValue(a)!);
            string? second = SnapshotAnswers.Resolve(root, r.GetValue(b)!);
            if (first == null || second == null)
            {
                return Failed("no snapshot " + (first == null ? r.GetValue(a) : r.GetValue(b)) + " (ug perf snapshot list)\n");
            }

            try
            {
                return (0, SnapshotAnswers.Compare(root, first, second, count));
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return Failed("cannot read the snapshots: " + e.Message + "\n");
            }
        }));
        return snapshot;
    }

    // `ug perf frame-debugger`: Unity's Frame Debugger through its internal API.
    private static Command FrameDebuggerCommand()
    {
        var debugger = new Command("frame-debugger", "Unity's Frame Debugger: enable it, list the frame's events or one event's detail, disable it.");
        debugger.Subcommands.Add(Ops("enable", "perf frame-debugger enable", "Turn the Frame Debugger on (pauses play mode) and print the frame's event count.", [], _ => Debug("enable")));
        debugger.Subcommands.Add(Ops("disable", "perf frame-debugger disable", "Turn the Frame Debugger off; play mode stays paused.", [], _ => Debug("disable")));

        var index = new Argument<int?>("event") { Description = "One event's detail: shader, pass, keywords, mesh, counts, render target, batch break cause.", Arity = ArgumentArity.ZeroOrOne };
        var from = new Option<int?>("--from") { Description = "The first event listed. Default: 0." };
        var top = new Option<int?>("--top") { Description = "How many events, 1 to 500. Default: 50." };
        debugger.Subcommands.Add(Ops("events", "perf frame-debugger events", "The frame's events: index, type, name and object; or one event's detail.", [index, from, top], r =>
            Debug("events", ("event", PerfNumber(r.GetValue(index))), ("from", PerfNumber(r.GetValue(from))), ("top", PerfNumber(r.GetValue(top))))));
        return debugger;

        static OpRequest Debug(string action, params (string Name, string? Value)[] parameters) =>
            PerfRequest("/perf/frame-debugger", null, OpTimeout, [("do", action), .. parameters]);
    }

    // A profiler request: needs bridge 0.8.0, named `ug perf <option>` in the refusal.
    private static OpRequest PerfRequest(string path, string? option, TimeSpan timeout, params (string Name, string? Value)[] parameters) =>
        new(Query(path, parameters)) { Profiler = true, Option = option, Timeout = timeout };

    private static string? PerfNumber(IFormattable? number) => number?.ToString(null, CultureInfo.InvariantCulture);
}
