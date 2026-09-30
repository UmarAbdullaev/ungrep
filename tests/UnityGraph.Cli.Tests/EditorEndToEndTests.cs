using System.Diagnostics;
using System.Globalization;
using UnityGraph.EditorLink;

namespace UnityGraph.Cli.Tests;

/// <summary>
/// `ug status/compile/run` and the editor-ops commands against a real editor: a copy of
/// fixtures/BridgeHost opened in batch mode with `-ugBridge`, then killed to check the fallbacks.
/// Runs only when UG_UNITY names a Unity 6000.3 executable, and prints the times SPEC-editor-link
/// and SPEC-editor-ops record.
/// </summary>
[Collection(nameof(EditorCommandTests))]
public sealed class EditorEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-e2e-" + Guid.NewGuid().ToString("N")[..8]);
    private Process? _unity;

    public void Dispose()
    {
        if (_unity is { HasExited: false })
        {
            _unity.Kill(entireProcessTree: true);
            _unity.WaitForExit(30_000);
        }

        _unity?.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void The_commands_drive_a_real_editor_and_fall_back_when_it_dies()
    {
        string? unity = Environment.GetEnvironmentVariable("UG_UNITY");
        if (string.IsNullOrEmpty(unity))
        {
            Assert.Skip("UG_UNITY not set");
        }

        string repo = RepoRoot();
        Copy(Path.Combine(repo, "fixtures", "BridgeHost"), _root);
        string manifest = Path.Combine(_root, "Packages", "manifest.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace(
            "file:../../../unity/com.unitygraph.bridge",
            "file:" + Path.Combine(repo, "unity", "com.unitygraph.bridge").Replace('\\', '/'),
            StringComparison.Ordinal));
        string log = Path.Combine(_root, "editor.log");

        var clock = Stopwatch.StartNew();
        _unity = Process.Start(new ProcessStartInfo(unity)
        {
            ArgumentList = { "-batchmode", "-nographics", "-ugBridge", "-projectPath", _root, "-logFile", log },
            UseShellExecute = false,
        })!;
        WaitForIdle(TimeSpan.FromMinutes(5));
        Report("editor open to bridge idle", clock.Elapsed);

        Assert.StartsWith("Unity 6000.3.14f1  idle  bridge 0.2.0\n", Ug(log, "status"), StringComparison.Ordinal);
        clock.Restart();
        string unchanged = Ug(log, "compile");
        Report("compile with no script changes", clock.Elapsed);
        Assert.StartsWith("no script changes: 0 errors", unchanged, StringComparison.Ordinal);

        string broken = Path.Combine(_root, "Assets", "Editor", "Broken.cs");
        File.WriteAllText(broken, "namespace BridgeHost { static class Broken { static int Speed() { return spd; } } }\n");
        clock.Restart();
        string failed = Ug(log, "compile");
        Report("compile with an error", clock.Elapsed);
        Assert.StartsWith("compiled in ", failed, StringComparison.Ordinal);
        Assert.Contains(": 1 error\n  Assets/Editor/Broken.cs:1:", failed, StringComparison.Ordinal);
        Assert.Contains("CS0103  The name 'spd' does not exist in the current context", failed, StringComparison.Ordinal);
        Assert.Contains("  last compile  1 error  ", Ug(log, "status"), StringComparison.Ordinal);

        File.WriteAllText(broken, "namespace BridgeHost { static class Broken { static int Speed() { return 1; } } }\n");
        clock.Restart();
        string fixedAnswer = Ug(log, "compile");
        Report("compile after the fix (with domain reload)", clock.Elapsed);
        Assert.Matches(@"^compiled in \d+\.\d s: 0 errors\n$", fixedAnswer);

        Assert.Matches(@"^ran BridgeHost\.FixtureMethods\.Hello in \d+ ms\n  result: hello from BridgeHost\n$", Ug(log, "run", "BridgeHost.FixtureMethods.Hello"));
        Assert.Contains("threw after", Ug(log, "run", "BridgeHost.FixtureMethods.Throws"), StringComparison.Ordinal);
        Assert.StartsWith("did not run UnityEditor.EditorApplication.Exit", Ug(log, "run", "UnityEditor.EditorApplication.Exit"), StringComparison.Ordinal);

        // editor-ops (SPEC-editor-ops, Testing): open, read, edit, save and read back, play and stop.
        clock.Restart();
        string opened = Ug(log, "scene", "open", "Assets/Scenes/Main.unity");
        Report("ug scene open", clock.Elapsed);
        Assert.StartsWith("opened Assets/Scenes/Main.unity\nAssets/Scenes/Main.unity  active  2 roots\nedit mode\n", opened, StringComparison.Ordinal);

        clock.Restart();
        string tree = Ug(log, "tree");
        Report("ug tree", clock.Elapsed);
        Assert.Contains("Level", tree, StringComparison.Ordinal);
        Assert.Contains("  Enemy  Rigidbody", tree, StringComparison.Ordinal);

        clock.Restart();
        string inspected = Ug(log, "inspect", "Level/Enemy");
        Report("ug inspect", clock.Elapsed);
        Assert.StartsWith("Level/Enemy", inspected, StringComparison.Ordinal);
        Assert.Contains("Rigidbody", inspected, StringComparison.Ordinal);

        clock.Restart();
        string created = Ug(log, "object", "create", "Spawn", "--parent", "Level", "--prefab", "Assets/Prefabs/Turret.prefab");
        Report("ug object create (prefab instance)", clock.Elapsed);
        Assert.Equal("created Level/Spawn (Turret prefab instance)  (Main.unity modified, not saved)\n", created);

        clock.Restart();
        string set = Ug(log, "set", "Level/Spawn", "speed", "7.5", "--component", "Turret");
        Report("ug set", clock.Elapsed);
        Assert.Equal("Level/Spawn  Turret.speed  5 -> 7.5  (Main.unity modified, not saved)\n", set);

        Assert.Equal("moved Level/Crate -> Crate  (Main.unity modified, not saved)\n", Ug(log, "object", "move", "Level/Crate", "--root"));
        Assert.Equal("deleted Level/Enemy  (Main.unity modified, not saved)\n", Ug(log, "object", "delete", "Level/Enemy"));
        Assert.Contains("Assets/Scenes/Main.unity  active  modified  3 roots\n", Ug(log, "scene"), StringComparison.Ordinal);

        clock.Restart();
        string saved = Ug(log, "save");
        Report("ug save", clock.Elapsed);
        Assert.StartsWith("saved Assets/Scenes/Main.unity\n", saved, StringComparison.Ordinal);

        // The saved YAML, read back through the graph (Unity makes no .csproj files in batch mode).
        WriteProject("Assembly-CSharp", "Assets\\Scripts\\Turret.cs");
        string scene = File.ReadAllText(Path.Combine(_root, "Assets", "Scenes", "Main.unity"));
        string spawn = Ug(log, "explain", "Level/Spawn");
        Assert.Contains("\nSpawn  prefab_instance  Assets/Scenes/Main.unity:", spawn, StringComparison.Ordinal);
        Assert.Contains("instance of  Assets/Prefabs/Turret.prefab", spawn, StringComparison.Ordinal);
        Assert.Contains("speed = 7.5", spawn, StringComparison.Ordinal);
        Assert.DoesNotContain("m_Name: Enemy", scene, StringComparison.Ordinal);

        clock.Restart();
        string play = Ug(log, "play");
        Report("ug play until playing", clock.Elapsed);
        Assert.Matches(@"^playing \(entered in \d+\.\d s\)\n$", play);
        Assert.EndsWith("\nplaying\n", Ug(log, "scene"), StringComparison.Ordinal);
        Assert.Contains("Spawn", Ug(log, "find", "--live", "--component", "Turret"), StringComparison.Ordinal);

        clock.Restart();
        string stop = Ug(log, "stop");
        Report("ug stop until edit mode", clock.Elapsed);
        Assert.Matches(@"^stopped after \d+\.\d s: 0 errors, 0 warnings\n$", stop);
        Assert.EndsWith("\nedit mode\n", Ug(log, "scene"), StringComparison.Ordinal);

        string ug = Path.Combine(repo, "artifacts", "bin", "UnityGraph.Cli", "release", OperatingSystem.IsWindows() ? "ug.exe" : "ug");
        if (File.Exists(ug))
        {
            Report("fresh-process ug status, median of 5", Median(ug, 5, _ => ["status"]));
            Report("fresh-process ug tree, median of 5", Median(ug, 5, _ => ["tree"]));
            Report("fresh-process ug inspect, median of 5", Median(ug, 5, _ => ["inspect", "Level/Spawn", "--component", "Turret"]));
            Report("fresh-process ug object active (an edit), median of 5", Median(ug, 5, i => ["object", "active", "Crate", i % 2 == 0 ? "false" : "true"]));
        }

        int pid = _unity.Id;
        _unity.Kill(entireProcessTree: true);
        _unity.WaitForExit(30_000);

        Assert.Equal("Unity not reachable (stale bridge.json: Unity (pid " + pid.ToString(CultureInfo.InvariantCulture) + ") is not running)\n", Ug(log, "status"));
        string fallback = Ug(log, "compile");
        Assert.StartsWith("Unity not reachable (stale bridge.json: Unity (pid ", fallback, StringComparison.Ordinal);
        Assert.Matches(@"; from (compile\.json|Editor\.log) of [-\d: ]+: 0 errors\n$", fallback);
    }

    // The median time of `count` runs of the release ug in its own process; every run must answer.
    private TimeSpan Median(string ug, int count, Func<int, string[]> args)
    {
        var times = new List<double>();
        for (int i = 0; i < count; i++)
        {
            var info = new ProcessStartInfo(ug) { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (string arg in args(i).Concat(["--project", _root]))
            {
                info.ArgumentList.Add(arg);
            }

            var clock = Stopwatch.StartNew();
            using Process process = Process.Start(info)!;
            string text = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            times.Add(clock.Elapsed.TotalMilliseconds);
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("not reachable", text, StringComparison.Ordinal);
            Assert.DoesNotContain("no answer", text, StringComparison.Ordinal);
        }

        times.Sort();
        return TimeSpan.FromMilliseconds(times[count / 2]);
    }

    // A minimal Unity-style project file, so the graph indexes the copy.
    private void WriteProject(string assembly, params string[] files) =>
        File.WriteAllText(Path.Combine(_root, assembly + ".csproj"),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Project ToolsVersion=\"4.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n"
            + "  <PropertyGroup>\n    <LangVersion>9.0</LangVersion>\n    <AssemblyName>" + assembly + "</AssemblyName>\n  </PropertyGroup>\n  <ItemGroup>\n"
            + string.Concat(files.Select(f => "    <Compile Include=\"" + f + "\" />\n"))
            + "  </ItemGroup>\n</Project>\n");

    private void WaitForIdle(TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < limit)
        {
            Assert.False(_unity!.HasExited, "Unity exited while opening the project; see its editor.log");
            BridgeConnection connection = BridgeClient.Connect(_root);
            using (connection.Client)
            {
                if (connection.Status?.State == "idle")
                {
                    return;
                }
            }

            Thread.Sleep(500);
        }

        Assert.Fail("the bridge was not idle within " + limit.TotalMinutes.ToString(CultureInfo.InvariantCulture) + " min");
    }

    private string Ug(string editorLog, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exitCode = CliApp.Run([.. args, "--project", _root], output, error, editorLog: editorLog);
        TestContext.Current.TestOutputHelper?.WriteLine("$ ug " + string.Join(' ', args) + "\n" + output.ToString().TrimEnd());
        Assert.Equal("", error.ToString());
        Assert.Equal(0, exitCode);
        return output.ToString().ReplaceLineEndings("\n");
    }

    private static void Report(string what, TimeSpan elapsed) =>
        TestContext.Current.TestOutputHelper?.WriteLine(what + ": " + elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + " s");

    // The project without Unity's per-session folders; Library comes along so the editor opens offline and fast.
    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }

        foreach (string dir in Directory.EnumerateDirectories(from))
        {
            if (Path.GetFileName(dir) is not ("Temp" or "Logs" or "UserSettings"))
            {
                Copy(dir, Path.Combine(to, Path.GetFileName(dir)));
            }
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "unitygraph.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("unitygraph.sln not found above " + AppContext.BaseDirectory);
    }
}
