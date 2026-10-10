using System.Diagnostics;

namespace UnityGraph.Cli.Tests;

/// <summary>
/// The camera part of the end-to-end run (SPEC-camera, Testing): the BridgeHost copy gets Cinemachine 3, Splines,
/// Mathematics and Settings Manager as file: packages from the package cache UG_PACKAGE_CACHE names (a Unity
/// project's Library/PackageCache: no download), and the physics module. The bridge's Cinemachine EditMode tests run
/// in the open editor through ug test, then ug camera edits Main.unity and undoes it.
/// </summary>
public sealed partial class EditorEndToEndTests
{
    private const string MainModified = "  (Main.unity modified, not saved)";

    private static readonly string[] _cinemachinePackages = ["com.unity.cinemachine", "com.unity.splines", "com.unity.mathematics", "com.unity.settings-manager"];

    // In the copy only. False when UG_PACKAGE_CACHE names no cache with Cinemachine 3 and its dependencies: that part is then skipped.
    private static bool PrepareCinemachine(string manifest)
    {
        string? cache = Environment.GetEnvironmentVariable("UG_PACKAGE_CACHE");
        string?[] found = string.IsNullOrEmpty(cache) || !Directory.Exists(cache)
            ? [null]
            : [.. _cinemachinePackages.Select(name => Directory.EnumerateDirectories(cache, name + "@*").FirstOrDefault())];
        if (found.Any(dir => dir == null) || !File.ReadAllText(Path.Combine(found[0]!, "package.json")).Contains("\"version\": \"3.", StringComparison.Ordinal))
        {
            TestContext.Current.TestOutputHelper?.WriteLine("camera end to end skipped: UG_PACKAGE_CACHE names no package cache with Cinemachine 3, Splines, Mathematics and Settings Manager");
            return false;
        }

        string entries = string.Concat(_cinemachinePackages.Zip(found, (name, dir) => "\n    \"" + name + "\": \"file:" + dir!.Replace('\\', '/') + "\","));
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace(
            "\"dependencies\": {",
            "\"dependencies\": {" + entries + "\n    \"com.unity.modules.physics\": \"1.0.0\",",
            StringComparison.Ordinal));
        return true;
    }

    // ug camera: the Cinemachine tests, then a follow camera on Main.unity's only camera, tuned, forced, released and undone.
    private void CameraCommands(string log)
    {
        // The test runner asks to save modified scenes before a run, a dialog batch mode never answers.
        Ug(log, "save");
        var clock = Stopwatch.StartNew();
        string tests = Ug(log, "test", "--filter", "CinemachineOpsTests");
        Report("ug test CinemachineOpsTests", clock.Elapsed);
        Assert.Matches(@"^EditMode: \d+ passed in \d+\.\d s\n$", tests);

        if (!Ug(log, "scene").Contains("Assets/Scenes/Main.unity", StringComparison.Ordinal))
        {
            Ug(log, "scene", "open", "Assets/Scenes/Main.unity");
        }

        clock.Restart();
        string list = Ug(log, "camera");
        Report("ug camera (list)", clock.Elapsed);
        Tokens("ug camera answer", list);
        Assert.Equal("Cinemachine 3.1.7  0 Cinemachine cameras, 1 camera\nCamera  Camera  depth 0  fov 60\nlive: Camera (Camera, depth 0, draws last on display 1)\n", list);

        clock.Restart();
        string created = Ug(log, "camera", "create", "Cam", "--preset", "follow", "--follow", "Crate");
        Report("ug camera create --preset follow", clock.Elapsed);
        Tokens("ug camera create answer", created);
        Assert.Equal(
            "created Cam (follow)" + MainModified + "\nCam  priority 0  body Follow  aim RotationComposer  follow Crate\n"
            + "added CinemachineBrain to Camera" + MainModified + "\nlive: Cam (brain Camera)\n",
            created);

        clock.Restart();
        string priority = Ug(log, "camera", "priority", "Cam", "5");
        Report("ug camera priority", clock.Elapsed);
        Assert.Equal("Cam  priority 0 -> 5" + MainModified + "\nlive: Cam (brain Camera)\n", priority);
        Assert.Equal("Cam  noise none -> 6D Shake  amplitude 1  frequency 1" + MainModified + "\nlive: Cam (brain Camera)\n", Ug(log, "camera", "noise", "Cam", "6D Shake"));

        clock.Restart();
        string forced = Ug(log, "camera", "force", "Cam");
        Report("ug camera force", clock.Elapsed);
        Assert.Equal("forced Cam live on Camera (until ug camera release, a play mode change or a script reload)\nlive: Cam (brain Camera, forced)\n", forced);
        Assert.Equal("released Camera: the brain goes back to priorities\nlive: Cam (brain Camera)\n", Ug(log, "camera", "release"));

        Assert.Equal("undid ug camera noise\n", Ug(log, "undo"));
        Assert.Equal("undid ug camera priority\n", Ug(log, "undo"));
        Assert.Equal("undid ug camera create\n", Ug(log, "undo"));
        Assert.Equal(list, Ug(log, "camera"));
    }
}
