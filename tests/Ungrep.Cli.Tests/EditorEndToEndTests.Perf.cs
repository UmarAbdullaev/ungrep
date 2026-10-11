using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Ungrep.Cli.Tests;

/// <summary>
/// The profiler part of the end-to-end run (SPEC-editor-ops, Profiler): recorded frames in edit and
/// play mode, a profiler session to a .raw file read back with --frames, counters, frame timing,
/// object memory, two snapshots compared here, and the Frame Debugger's refusal in a batch editor.
/// </summary>
public sealed partial class EditorEndToEndTests
{
    private void ProfilerCommands(string log)
    {
        var clock = Stopwatch.StartNew();
        string record = Ug(log, "perf", "--record", "1");
        Report("ug perf --record 1 (edit mode)", clock.Elapsed);
        Tokens("ug perf --record answer", record);
        // A batch-mode editor updates thousands of times a second: the 300-frame cap ends the recording first.
        Assert.Matches(@"^edit mode \(the editor's frames\), [\d,]+ frames in \d\.\d s( \(the 300-frame cap: --max-frames, up to 2,000\))?, main thread: median [\d.]+ ms \(\d+ fps\), p99 ", record);
        Assert.Contains("\nself ms a frame, share, calls a frame:\n  ", record, StringComparison.Ordinal);
        Assert.Contains("\nspikes over ", record, StringComparison.Ordinal);
        Assert.StartsWith("profiler  off (holds frames ", Ug(log, "perf", "profiler"), StringComparison.Ordinal);

        string started = Ug(log, "perf", "profiler", "start", "--file");
        Assert.Matches(@"^profiler recording the editor's frames from frame \d+, to Library/ungrep/profiler/[\d-]+\.raw; stop: ug perf profiler stop\n$", started);
        string raw = Regex.Match(started, @"Library/ungrep/profiler/[\d-]+\.raw").Value;
        Assert.Contains("\nfile  " + raw + "  ", Ug(log, "perf", "profiler", "status"), StringComparison.Ordinal);
        Assert.Equal("the profiler is already recording to " + raw + " (ug perf profiler stop)\n", Ug(log, "perf", "profiler", "start"));
        Thread.Sleep(300);
        string stopped = Ug(log, "perf", "profiler", "stop");
        Match frames = Regex.Match(stopped, @"read them: ug perf --frames (\d+-\d+)\n");
        Assert.True(frames.Success, stopped);
        Assert.Contains("\nfile  " + raw + "  ", stopped, StringComparison.Ordinal);
        Assert.True(new FileInfo(Path.Combine(_root, raw)).Length > 0, raw + " is empty");
        clock.Restart();
        string again = Ug(log, "perf", "--frames", frames.Groups[1].Value, "--top", "3");
        Report("ug perf --frames (the stopped session)", clock.Elapsed);
        Assert.StartsWith("frames " + frames.Groups[1].Value + ", main thread: median ", again, StringComparison.Ordinal);
        Assert.Equal("the profiler was not recording\n", Ug(log, "perf", "profiler", "stop"));

        clock.Restart();
        string counters = Ug(log, "perf", "counters", "Total Used Memory", "Draw Calls Count");
        Report("ug perf counters (two names)", clock.Elapsed);
        Assert.Matches(@"^counters after \d+ frames \(edit mode\):\n  Memory  Total Used Memory  [\d.]+ [KMG]?B\n  Render  Draw Calls Count  \d+\n$", counters);
        Assert.StartsWith("categories (ug perf counters --category <c> [--markers]; or counter names):\n", Ug(log, "perf", "counters"), StringComparison.Ordinal);
        Assert.Matches(@"^(frame timing \(FrameTimingManager|frame timing is off|Unity gave no frame timing)", Ug(log, "perf", "timing"));

        clock.Restart();
        string memory = Ug(log, "perf", "memory", "--top", "5");
        Report("ug perf memory --top 5", clock.Elapsed);
        Tokens("ug perf memory --top 5 answer", memory);
        Assert.Matches(@"^[\d,]+ objects, [\d.]+ [KMG]?B \(native sizes, Profiler\.GetRuntimeMemorySizeLong; edit mode\)\nby type  ", memory);
        Assert.Contains("\n  ... ", memory, StringComparison.Ordinal);
        Assert.StartsWith("Level/Spawn  GameObject  ", Ug(log, "perf", "memory", "--object", "Level/Spawn"), StringComparison.Ordinal);

        // Two snapshots around a new object, then list and compare here.
        clock.Restart();
        string first = Ug(log, "perf", "snapshot", "take");
        Report("ug perf snapshot take", clock.Elapsed);
        Assert.Matches(@"^Library/ungrep/snapshots/[\d-]+\.snap  [\d.]+ [KMG]?B  in [\d.]+ s \(edit mode\); ug perf snapshot list, ug perf snapshot compare <a> <b>\n$", first);
        Assert.StartsWith("created SnapProbe", Ug(log, "object", "create", "SnapProbe"), StringComparison.Ordinal);
        string second = Ug(log, "perf", "snapshot", "take", "Library/ungrep/after");
        Assert.StartsWith("Library/ungrep/after.snap  ", second, StringComparison.Ordinal);
        string a = first[..first.IndexOf("  ", StringComparison.Ordinal)];
        Assert.Contains(a + "  ", Ug(log, "perf", "snapshot", "list"), StringComparison.Ordinal);
        clock.Restart();
        string compared = Ug(log, "perf", "snapshot", "compare", a, "Library/ungrep/after.snap");
        Report("ug perf snapshot compare (two editor snapshots)", clock.Elapsed);
        Tokens("ug perf snapshot compare answer", compared);
        Assert.StartsWith("a  " + a + "  ", compared, StringComparison.Ordinal);
        Assert.Contains("  same session (objects matched by instance ID)\nmemory  total used ", compared, StringComparison.Ordinal);
        Assert.Contains("\nnative objects  ", compared, StringComparison.Ordinal);
        Assert.Contains("\nnew in b: ", compared, StringComparison.Ordinal);
        Assert.EndsWith("managed objects are not compared: crawling the managed heap is the Memory Profiler package's work\n", compared, StringComparison.Ordinal);
        Ug(log, "undo");

        // Play mode: the game's frames; the Frame Debugger needs a multithreaded renderer, which batch mode lacks.
        Ug(log, "play");
        clock.Restart();
        string playing = Ug(log, "perf", "--record", "1");
        Report("ug perf --record 1 (play mode)", clock.Elapsed);
        Assert.Matches(@"^play mode, [\d,]+ frames in \d\.\d s( \(the 300-frame cap: --max-frames, up to 2,000\))?, main thread: median ", playing);
        Assert.StartsWith(
            "Unity's Frame Debugger is not available in this editor: it renders ",
            Ug(log, "perf", "frame-debugger", "enable"),
            StringComparison.Ordinal);
        Assert.Equal("the frame debugger is off: ug perf frame-debugger enable\n", Ug(log, "perf", "frame-debugger", "events"));
        Assert.Matches(@"^stopped after \d+\.\d s: \d+ errors?, 0 warnings", Ug(log, "stop"));

        // The batch editor's only play-mode errors are its missing graphics device's, none from the profiler.
        Assert.DoesNotMatch(@"(?m)^\s+error(?: x\d+)?  (?!No graphic device)", Ug(log, "log"));
    }
}
