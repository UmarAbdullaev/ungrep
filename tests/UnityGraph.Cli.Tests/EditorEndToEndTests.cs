using System.Diagnostics;
using System.Globalization;
using UnityGraph.EditorLink;

namespace UnityGraph.Cli.Tests;

/// <summary>
/// `ug status/compile/run` against a real editor: a copy of fixtures/BridgeHost opened in batch mode
/// with `-ugBridge`, then killed to check the fallbacks. Runs only when UG_UNITY names a Unity 6000.3
/// executable, and prints the times SPEC-editor-link records.
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

        string ug = Path.Combine(repo, "artifacts", "bin", "UnityGraph.Cli", "release", OperatingSystem.IsWindows() ? "ug.exe" : "ug");
        if (File.Exists(ug))
        {
            var times = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                clock.Restart();
                using Process status = Process.Start(new ProcessStartInfo(ug) { ArgumentList = { "status", "--project", _root }, RedirectStandardOutput = true, UseShellExecute = false })!;
                string text = status.StandardOutput.ReadToEnd();
                status.WaitForExit();
                times.Add(clock.Elapsed.TotalMilliseconds);
                Assert.StartsWith("Unity 6000.3.14f1  idle", text, StringComparison.Ordinal);
            }

            times.Sort();
            Report("fresh-process ug status, median of 5", TimeSpan.FromMilliseconds(times[2]));
        }

        int pid = _unity.Id;
        _unity.Kill(entireProcessTree: true);
        _unity.WaitForExit(30_000);

        Assert.Equal("Unity not reachable (stale bridge.json: Unity (pid " + pid.ToString(CultureInfo.InvariantCulture) + ") is not running)\n", Ug(log, "status"));
        string fallback = Ug(log, "compile");
        Assert.StartsWith("Unity not reachable (stale bridge.json: Unity (pid ", fallback, StringComparison.Ordinal);
        Assert.Matches(@"; from (compile\.json|Editor\.log) of [-\d: ]+: 0 errors\n$", fallback);
    }

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
