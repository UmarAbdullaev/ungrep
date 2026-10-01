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
public sealed partial class EditorEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-e2e-" + Guid.NewGuid().ToString("N")[..8]);
    private Process? _unity;

    public void Dispose()
    {
        EditorCommands.DocsFetchOverride = null;
        EditorCommands.DocsCacheOverride = null;
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
        bool input = PrepareInput(manifest);
        string log = Path.Combine(_root, "editor.log");

        var clock = Stopwatch.StartNew();
        _unity = Process.Start(new ProcessStartInfo(unity)
        {
            ArgumentList = { "-batchmode", "-nographics", "-ugBridge", "-projectPath", _root, "-logFile", log },
            UseShellExecute = false,
        })!;
        WaitForIdle(TimeSpan.FromMinutes(5));
        Report("editor open to bridge idle", clock.Elapsed);

        Assert.StartsWith("Unity 6000.3.14f1  idle  bridge 0.4.0\n", Ug(log, "status"), StringComparison.Ordinal);
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

        LoopConsole(log);

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

        // Prefabs (O6): save an object as a prefab, override an instance, apply, revert, unpack.
        clock.Restart();
        string madePrefab = Ug(log, "prefab", "create", "Crate", "Assets/Prefabs/Crate.prefab");
        Report("ug prefab create", clock.Elapsed);
        Assert.Equal("created Assets/Prefabs/Crate.prefab from Crate  (written Assets/Prefabs/Crate.prefab; Main.unity modified, not saved)\n", madePrefab);
        Assert.True(File.Exists(Path.Combine(_root, "Assets", "Prefabs", "Crate.prefab")));
        Assert.Equal("added Turret to Crate  (Main.unity modified, not saved)\n", Ug(log, "component", "add", "Crate", "Turret"));
        Assert.Equal("Crate  Turret.speed  5 -> 9  (Main.unity modified, not saved)\n", Ug(log, "set", "Crate", "speed", "9", "--component", "Turret"));

        clock.Restart();
        string applied = Ug(log, "prefab", "apply", "Crate");
        Report("ug prefab apply", clock.Elapsed);
        Assert.StartsWith("applied 1 override from Crate to Assets/Prefabs/Crate.prefab  (written Assets/Prefabs/Crate.prefab; Main.unity modified, not saved)\n", applied, StringComparison.Ordinal);
        Assert.Contains("\n  Crate  added Turret\n", applied, StringComparison.Ordinal);
        Assert.Equal("no overrides on Crate to apply\n", Ug(log, "prefab", "apply", "Crate"));

        // The written asset, read back through the graph.
        string crate = Ug(log, "explain", "Assets/Prefabs/Crate.prefab");
        Assert.Contains("objects  1 game object", crate, StringComparison.Ordinal);
        Assert.Contains("scripts  BridgeHost.Turret", crate, StringComparison.Ordinal);
        Assert.Contains("speed: 9", File.ReadAllText(Path.Combine(_root, "Assets", "Prefabs", "Crate.prefab")), StringComparison.Ordinal);

        Assert.Equal("Crate  Turret.speed  9 -> 4  (Main.unity modified, not saved)\n", Ug(log, "set", "Crate", "speed", "4", "--component", "Turret"));
        Assert.Equal(
            "reverted 1 override on Crate  (Main.unity modified, not saved)\n  Crate  Turret.speed\n",
            Ug(log, "prefab", "revert", "Crate"));
        Assert.Contains("9", Ug(log, "inspect", "Crate", "--component", "Turret"), StringComparison.Ordinal);
        Assert.Equal("no overrides on Crate to revert\n", Ug(log, "prefab", "revert", "Crate"));

        Assert.Equal("unpacked Crate from Assets/Prefabs/Crate.prefab  (Main.unity modified, not saved)\n", Ug(log, "prefab", "unpack", "Crate"));
        Assert.Equal("Crate is not a prefab instance: ug prefab create Crate <path> makes a prefab from it\n", Ug(log, "prefab", "apply", "Crate"));
        Assert.Equal("Assets/Prefabs/Crate.prefab already exists: ug does not overwrite it\n", Ug(log, "prefab", "create", "Crate", "Assets/Prefabs/Crate.prefab"));
        Assert.Equal("Assets/Plugins/Crate.prefab is in the vendored folder Assets/Plugins/, which ug does not edit\n", Ug(log, "prefab", "create", "Crate", "Assets/Plugins/Crate.prefab"));

        // Duplicate, undo, redo and selection (O2-O4 endpoints without an end to end until now).
        Assert.Equal("duplicated Level/Spawn -> Level/Spawn2  (Main.unity modified, not saved)\n", Ug(log, "object", "duplicate", "Level/Spawn", "--name", "Spawn2"));
        Assert.Contains("  Spawn2", Ug(log, "tree", "Level"), StringComparison.Ordinal);
        Assert.Equal("undid ug object duplicate\n", Ug(log, "undo"));
        Assert.DoesNotContain("Spawn2", Ug(log, "tree", "Level"), StringComparison.Ordinal);
        Assert.Equal("redid ug object duplicate\n", Ug(log, "redo"));
        Assert.Contains("  Spawn2", Ug(log, "tree", "Level"), StringComparison.Ordinal);

        Assert.Equal("selected Level/Spawn\n", Ug(log, "select", "Level/Spawn"));
        Assert.Equal("Level/Spawn\n", Ug(log, "select"));
        Assert.Equal("nothing selected\n", Ug(log, "select", "--clear"));
        Assert.Equal("nothing selected\n", Ug(log, "select"));

        // Scenes: new (added), active, close.
        Assert.StartsWith("saved Assets/Scenes/Main.unity\n", Ug(log, "save"), StringComparison.Ordinal);
        Assert.Equal(
            "created Assets/Scenes/Extra.unity\nAssets/Scenes/Main.unity  3 roots\nAssets/Scenes/Extra.unity  active  2 roots\nedit mode\n",
            Ug(log, "scene", "new", "Assets/Scenes/Extra.unity", "--additive"));
        Assert.True(File.Exists(Path.Combine(_root, "Assets", "Scenes", "Extra.unity")));
        Assert.Equal(
            "Assets/Scenes/Main.unity  active  3 roots\nAssets/Scenes/Extra.unity  2 roots\nedit mode\n",
            Ug(log, "scene", "active", "Assets/Scenes/Main.unity"));
        Assert.Equal(
            "closed Assets/Scenes/Extra.unity\nAssets/Scenes/Main.unity  active  3 roots\nedit mode\n",
            Ug(log, "scene", "close", "Assets/Scenes/Extra.unity"));
        Assert.Contains("Extra.unity", Ug(log, "scene", "close", "Assets/Scenes/Extra.unity"), StringComparison.Ordinal);
        Assert.Equal("no modified scenes; assets saved\n", Ug(log, "save"));

        // Assets (O7): folders, a ScriptableObject from a script written into the project, a material,
        // an importer setting, then move, rename, duplicate, trash and reimport; GUIDs read from the .meta files.
        Assert.Equal("created folder Assets/Art/Icons  (written Assets/Art/Icons; not undoable)\n", Ug(log, "asset", "folder", "Assets/Art/Icons"));
        Assert.True(File.Exists(Path.Combine(_root, "Assets", "Art", "Icons.meta")));
        Assert.Equal("Assets/Art/Icons already exists: ug does not overwrite it\n", Ug(log, "asset", "folder", "Assets/Art/Icons"));

        File.WriteAllText(
            Path.Combine(_root, "Assets", "Scripts", "Alarm.cs"),
            "namespace BridgeHost { public class Alarm : UnityEngine.MonoBehaviour { public UnityEngine.Events.UnityEvent raised; public int rings; public void Ring() { rings++; } } }\n");
        File.WriteAllText(Path.Combine(_root, "Assets", "Scripts", "Wave.cs"), "namespace BridgeHost { public class Wave : UnityEngine.ScriptableObject { public int count = 3; } }\n");
        Assert.Matches(@"^compiled in \d+\.\d s: 0 errors\n$", Ug(log, "compile"));
        Assert.Equal("created folder Assets/Data  (written Assets/Data; not undoable)\n", Ug(log, "asset", "folder", "Assets/Data"));
        clock.Restart();
        string wave = Ug(log, "asset", "create", "Assets/Data/Wave1.asset", "Wave");
        Report("ug asset create (ScriptableObject)", clock.Elapsed);
        Assert.Equal("created Assets/Data/Wave1.asset (BridgeHost.Wave)  (written Assets/Data/Wave1.asset; not undoable)\n", wave);
        Assert.Equal("Assets/Data/Wave1.asset  count  3 -> 7  (Assets/Data/Wave1.asset modified, not saved)\n", Ug(log, "set", "Assets/Data/Wave1.asset", "count", "7"));
        Assert.Equal("no modified scenes; assets saved\n", Ug(log, "save"));
        Assert.Contains("count: 7", File.ReadAllText(Path.Combine(_root, "Assets", "Data", "Wave1.asset")), StringComparison.Ordinal);
        WriteProject("Assembly-CSharp", "Assets\\Scripts\\Turret.cs", "Assets\\Scripts\\Wave.cs");
        Assert.Contains("BridgeHost.Wave", Ug(log, "explain", "Assets/Data/Wave1.asset"), StringComparison.Ordinal);
        Assert.Equal("no Material, Folder or ScriptableObject type Wav (near: Wave)\n", Ug(log, "asset", "create", "Assets/Data/W.asset", "Wav"));

        Assert.Equal(
            "created Assets/Art/Ink.mat (Material, shader Unlit/Color)  (written Assets/Art/Ink.mat; not undoable)\n",
            Ug(log, "asset", "create", "Assets/Art/Ink.mat", "Material", "--shader", "Unlit/Color"));
        Assert.Equal(
            "Assets/Art/Ink.mat  _Color  1, 1, 1, 1 -> 1, 0, 0, 1  (Assets/Art/Ink.mat modified, not saved)\n",
            Ug(log, "set", "Assets/Art/Ink.mat", "_Color", "#ff0000"));
        Assert.Equal("no modified scenes; assets saved\n", Ug(log, "save"));
        Assert.Contains("_Color: {r: 1, g: 0, b: 0, a: 1}", File.ReadAllText(Path.Combine(_root, "Assets", "Art", "Ink.mat")), StringComparison.Ordinal);
        Assert.Contains("Unlit/Color", Ug(log, "inspect", "Assets/Art/Ink.mat"), StringComparison.Ordinal);

        // A 1x1 PNG dropped into the project: reimport brings it in, then an importer setting.
        string dot = Path.Combine(_root, "Assets", "Art", "Icons", "Dot.png");
        File.WriteAllBytes(dot, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        Assert.StartsWith("reimported Assets/Art/Icons/Dot.png  (", Ug(log, "asset", "reimport", "Assets/Art/Icons/Dot.png"), StringComparison.Ordinal);
        Assert.Equal(
            "Assets/Art/Icons/Dot.png  TextureImporter.isReadable  0 -> 1  (reimported; .meta written)\n",
            Ug(log, "set", "Assets/Art/Icons/Dot.png", "isReadable", "true", "--importer"));
        Assert.Contains("isReadable: 1", File.ReadAllText(dot + ".meta"), StringComparison.Ordinal);
        Assert.Contains("isReadable", Ug(log, "inspect", "Assets/Art/Icons/Dot.png", "--importer"), StringComparison.Ordinal);
        Assert.Equal("no property _MainTex on Assets/Art/Ink.mat (shader Unlit/Color: _Color)\n", Ug(log, "set", "Assets/Art/Ink.mat", "_MainTex", "Assets/Art/Icons/Dot.png"));
        Ug(log, "save");

        // UnityEvent calls and component references (UI tools): wire an event, list it, remove it, and
        // point a field at a component by address; the scene is saved clean again after.
        Assert.StartsWith("created Bell", Ug(log, "object", "create", "Bell"), StringComparison.Ordinal);
        Ug(log, "component", "add", "Bell", "Alarm");
        Assert.Equal("Bell  Alarm.raised  + Bell:Alarm.Ring()  (Main.unity modified, not saved)\n", Ug(log, "event", "add", "Bell", "raised", "Bell:Alarm", "Ring"));
        Assert.Equal("Bell  Alarm.raised\n  0  Bell:Alarm.Ring()\n", Ug(log, "event", "list", "Bell"));
        Assert.Equal("Bell  Alarm.raised  - Bell:Alarm.Ring()  (Main.unity modified, not saved)\n", Ug(log, "event", "remove", "Bell", "raised"));
        Assert.Equal("Level/Spawn  Turret.target  null -> Bell  (Main.unity modified, not saved)\n", Ug(log, "set", "Level/Spawn", "target", "Bell:Transform", "--component", "Turret"));
        Ug(log, "object", "delete", "Bell");
        Ug(log, "save");

        string inkGuid = MetaGuid("Assets/Art/Ink.mat");
        clock.Restart();
        string moved = Ug(log, "asset", "move", "Assets/Art/Ink.mat", "Assets/Art/Icons/Ink.mat");
        Report("ug asset move", clock.Elapsed);
        Assert.Equal("moved Assets/Art/Ink.mat -> Assets/Art/Icons/Ink.mat  (written Assets/Art/Icons/Ink.mat; guid " + inkGuid + " kept; not undoable)\n", moved);
        Assert.False(File.Exists(Path.Combine(_root, "Assets", "Art", "Ink.mat")) || File.Exists(Path.Combine(_root, "Assets", "Art", "Ink.mat.meta")));
        Assert.Equal(inkGuid, MetaGuid("Assets/Art/Icons/Ink.mat"));
        Assert.StartsWith("renamed Assets/Art/Icons/Ink.mat -> Assets/Art/Icons/Dye.mat  (written Assets/Art/Icons/Dye.mat; guid " + inkGuid + " kept", Ug(log, "asset", "rename", "Assets/Art/Icons/Ink.mat", "Dye"), StringComparison.Ordinal);
        Assert.Equal(inkGuid, MetaGuid("Assets/Art/Icons/Dye.mat"));
        string copy = Ug(log, "asset", "duplicate", "Assets/Art/Icons/Dye.mat", "Assets/Art/Icons/Dye Copy.mat");
        string copyGuid = MetaGuid("Assets/Art/Icons/Dye Copy.mat");
        Assert.NotEqual(inkGuid, copyGuid);
        Assert.Equal("duplicated Assets/Art/Icons/Dye.mat -> Assets/Art/Icons/Dye Copy.mat  (written Assets/Art/Icons/Dye Copy.mat; new guid " + copyGuid + "; not undoable)\n", copy);
        Assert.Contains("Assets/Art/Icons/Dye.mat", Ug(log, "explain", "Assets/Art/Icons/Dye.mat"), StringComparison.Ordinal);

        clock.Restart();
        string trashed = Ug(log, "asset", "delete", "Assets/Art/Icons/Dye Copy.mat");
        Report("ug asset delete (to the trash)", clock.Elapsed);
        Assert.Equal("deleted Assets/Art/Icons/Dye Copy.mat  (moved to the OS trash with its .meta; not undoable)\n", trashed);
        Assert.False(File.Exists(Path.Combine(_root, "Assets", "Art", "Icons", "Dye Copy.mat")) || File.Exists(Path.Combine(_root, "Assets", "Art", "Icons", "Dye Copy.mat.meta")));

        Assert.Equal("Assets/ itself cannot be deleted\n", Ug(log, "asset", "delete", "Assets"));
        Assert.Equal("Assets/Scenes/Main.unity is an open scene: ug scene close Assets/Scenes/Main.unity first\n", Ug(log, "asset", "delete", "Assets/Scenes/Main.unity"));
        Assert.Equal("Assets/Plugins/Dye.mat is in the vendored folder Assets/Plugins/, which ug does not edit\n", Ug(log, "asset", "move", "Assets/Art/Icons/Dye.mat", "Assets/Plugins/Dye.mat"));
        Assert.Equal("Assets/Art/Icons/Dye.mat already exists: ug does not overwrite it\n", Ug(log, "asset", "duplicate", "Assets/Art/Icons/Dye.mat", "Assets/Art/Icons/Dye.mat"));

        // Project data, menu items and packages (O8). The package is a local one made in the copy, so nothing is downloaded.
        clock.Restart();
        string project = Ug(log, "project");
        Report("ug project", clock.Elapsed);
        Assert.StartsWith("Unity 6000.3.14f1  render pipeline: Built-in\ntags  Untagged, Respawn, Finish, EditorOnly, MainCamera, Player, GameController\nlayers  0 Default, ", project, StringComparison.Ordinal);
        Assert.Contains("\nsorting layers  Default\nbuild scenes  ", project, StringComparison.Ordinal);

        Assert.Equal("did not run File/Quit: refused, it would close the editor (ug does not quit Unity)\n", Ug(log, "run", "File/Quit"));
        Assert.Equal("did not run File/Build And Run: refused, it builds a player (ug does not build; use Unity's Build Profiles window)\n", Ug(log, "run", "File/Build And Run"));
        Assert.StartsWith("Unity 6000.3.14f1  idle", Ug(log, "status"), StringComparison.Ordinal);
        Assert.Equal("did not run Tools/BridgeHost/Nope: no menu item Tools/BridgeHost/Nope\n", Ug(log, "run", "Tools/BridgeHost/Nope"));
        Assert.Matches(@"^ran Tools/BridgeHost/Log Hello in \d+ ms\n$", Ug(log, "run", "Tools/BridgeHost/Log Hello"));
        Assert.Contains("hello from the menu", EditorLogText(log), StringComparison.Ordinal);

        string packageFolder = Path.Combine(_root, "LocalPackages", "com.ug.e2e");
        Directory.CreateDirectory(packageFolder);
        File.WriteAllText(Path.Combine(packageFolder, "package.json"), "{\"name\":\"com.ug.e2e\",\"version\":\"1.2.3\",\"displayName\":\"ug e2e\",\"unity\":\"2022.3\"}\n");

        clock.Restart();
        string listed = Ug(log, "package", "list");
        Report("ug package list", clock.Elapsed);
        Assert.Contains("com.unity.test-framework  ", listed, StringComparison.Ordinal);
        Assert.Contains("com.unitygraph.bridge  0.4.0  local", listed, StringComparison.Ordinal);
        Assert.DoesNotContain("com.ug.e2e", listed, StringComparison.Ordinal);

        Assert.Equal(
            "removing com.unitygraph.bridge would end this connection: remove it in Package Manager (Window > Package Manager)\n",
            Ug(log, "package", "remove", "com.unitygraph.bridge"));
        Assert.Equal("no package id given: a name, name@version, a git URL or file:path\n", Ug(log, "package", "add", " "));

        clock.Restart();
        string added = Ug(log, "package", "add", "file:" + packageFolder.Replace('\\', '/'));
        Report("ug package add (local package)", clock.Elapsed);
        Assert.StartsWith("added com.ug.e2e  1.2.3  local  ", added, StringComparison.Ordinal);
        Assert.EndsWith("  (Unity updated Packages/manifest.json; scripts may recompile, ug compile waits; not undoable)\n", added, StringComparison.Ordinal);
        Assert.Contains("\"com.ug.e2e\"", File.ReadAllText(manifest), StringComparison.Ordinal);
        WaitForIdle(TimeSpan.FromMinutes(2));
        Assert.Contains("com.ug.e2e  1.2.3  local  ", Ug(log, "package", "list"), StringComparison.Ordinal);

        clock.Restart();
        string removed = Ug(log, "package", "remove", "com.ug.e2e");
        Report("ug package remove", clock.Elapsed);
        Assert.Equal("removed com.ug.e2e  (Unity updated Packages/manifest.json; scripts may recompile, ug compile waits; not undoable)\n", removed);
        Assert.DoesNotContain("com.ug.e2e", File.ReadAllText(manifest), StringComparison.Ordinal);
        WaitForIdle(TimeSpan.FromMinutes(2));
        Assert.DoesNotContain("com.ug.e2e", Ug(log, "package", "list"), StringComparison.Ordinal);
        Assert.StartsWith("could not remove com.ug.e2e: ", Ug(log, "package", "remove", "com.ug.e2e"), StringComparison.Ordinal);

        // Screenshots, API lookup, docs pages, perf and batch (O9). -nographics has no graphics device, so
        // the picture is refused; the docs fallback is a fake here, nothing in the run touches the network.
        var docFetches = new List<string>();
        EditorCommands.DocsFetchOverride = url =>
        {
            docFetches.Add(url);
            return new DocsFetchResult(null, "offline in tests");
        };
        EditorCommands.DocsCacheOverride = new DocsCache(Path.Combine(_root, "docs-cache"));
        Assert.Equal(
            "cannot take a screenshot: this Unity runs without a graphics device (started with -batchmode -nographics); open the editor normally\n",
            Ug(log, "shot"));
        Assert.StartsWith("width must be a whole number from 64 to 2048, not 10\n", Ug(log, "shot", "--width", "10"), StringComparison.Ordinal);

        clock.Restart();
        string api = Ug(log, "api", "Transform");
        Report("ug api Transform", clock.Elapsed);
        Assert.StartsWith("class UnityEngine.Transform : Component  (UnityEngine.CoreModule)\n", api, StringComparison.Ordinal);
        Assert.True(api.Length / 4 <= 300, "api Transform is " + (api.Length / 4) + " tokens");
        Assert.Contains("Raycast", Ug(log, "api", "Physics.Raycast"), StringComparison.Ordinal);
        Assert.Contains("void Rotate(", Ug(log, "api", "Transform", "Rotate"), StringComparison.Ordinal);
        Assert.Contains("BridgeHost.FixtureMethods", Ug(log, "api", "BridgeHost.FixtureMethods"), StringComparison.Ordinal);

        string unknown = Ug(log, "api", "NoSuchThingAtAll");
        Assert.StartsWith("no type NoSuchThingAtAll in the loaded assemblies\nnot fetched, read it at:\n  https://docs.unity3d.com/6000.3/Documentation/ScriptReference/NoSuchThingAtAll.html (offline in tests)\n", unknown, StringComparison.Ordinal);
        Assert.Equal(["https://docs.unity3d.com/6000.3/Documentation/ScriptReference/NoSuchThingAtAll.html"], docFetches);
        Assert.StartsWith("edit mode\nrender  only counted in play mode (ug play, then ug perf)\nmemory  ", Ug(log, "perf"), StringComparison.Ordinal);

        string batch = Ug(log, "batch", "object create BatchOne", "object create BatchTwo --parent BatchOne", "object rename BatchOne/BatchTwo \"Batch Two\"");
        Assert.StartsWith("1/3 object create BatchOne\ncreated BatchOne", batch, StringComparison.Ordinal);
        Assert.EndsWith("3 steps done. Each step is its own undo group (ug undo reverts the last).\n", batch, StringComparison.Ordinal);
        Assert.Contains("Batch Two", Ug(log, "tree", "BatchOne"), StringComparison.Ordinal);
        Assert.StartsWith("undid ug object rename\n", Ug(log, "undo"), StringComparison.Ordinal);
        Assert.DoesNotContain("Batch Two", Ug(log, "tree", "BatchOne"), StringComparison.Ordinal);
        string stopped = Ug(log, "batch", "object create BatchThree", "component add Ghost Rigidbody", "save");
        Assert.Contains("2/3 component add Ghost Rigidbody\nnot found: Ghost", stopped, StringComparison.Ordinal);
        Assert.EndsWith("stopped at step 2: 1 step not run. Earlier steps stay done; ug undo reverts them one by one.\n", stopped, StringComparison.Ordinal);
        Assert.Contains("BatchThree", Ug(log, "tree"), StringComparison.Ordinal);
        Ug(log, "undo");
        Ug(log, "undo");
        Ug(log, "undo");
        Assert.DoesNotContain("BatchOne", Ug(log, "tree"), StringComparison.Ordinal);

        clock.Restart();
        string play = Ug(log, "play");
        Report("ug play until playing", clock.Elapsed);
        Assert.Matches(@"^playing \(entered in \d+\.\d s\)\n$", play);
        Assert.EndsWith("\nplaying\n", Ug(log, "scene"), StringComparison.Ordinal);
        Assert.Contains("Spawn", Ug(log, "find", "--live", "--component", "Turret"), StringComparison.Ordinal);
        string perf = Ug(log, "perf");
        Assert.StartsWith("playing\nrender  ", perf, StringComparison.Ordinal);
        Assert.Contains(" draw calls  ", perf, StringComparison.Ordinal);

        clock.Restart();
        string stop = Ug(log, "stop");
        Report("ug stop until edit mode", clock.Elapsed);
        Assert.Matches(@"^stopped after \d+\.\d s: 0 errors, 0 warnings\n$", stop);
        Assert.EndsWith("\nedit mode\n", Ug(log, "scene"), StringComparison.Ordinal);

        LoopCommands(log);
        if (input)
        {
            PlayInputCommands(log, repo);
        }

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

    // The editor's log so far; Unity keeps the file open while it runs.
    private static string EditorLogText(string log)
    {
        using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // The guid Unity wrote into an asset's .meta file.
    private string MetaGuid(string asset)
    {
        string meta = File.ReadAllText(Path.Combine(_root, asset.Replace('/', Path.DirectorySeparatorChar) + ".meta"));
        return System.Text.RegularExpressions.Regex.Match(meta, @"^guid: ([0-9a-f]{32})", System.Text.RegularExpressions.RegexOptions.Multiline).Groups[1].Value;
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

    // unity-loop (SPEC-unity-loop, Testing): `ug console` shows a logged fixture error.
    private void LoopConsole(string log)
    {
        var clock = Stopwatch.StartNew();
        Ug(log, "run", "BridgeHost.FixtureMethods.LogError");
        string console = Ug(log, "console");
        Report("ug console (1 error)", clock.Elapsed);
        Tokens("ug console answer", console);
        Assert.Matches(@"^console  \d+ errors?(, \d+ warnings?)?  \(last #\d+\)\n  error  e2e fixture error", console);
    }

    // unity-loop: the compile gate check, `ug test`, `ug play --for` and `ug log`.
    private void LoopCommands(string log)
    {
        var clock = Stopwatch.StartNew();

        string broken = Path.Combine(_root, "Assets", "Editor", "Broken.cs");
        File.WriteAllText(broken, "namespace BridgeHost { static class Broken { static int Speed() { return spd; } } }\n");
        clock.Restart();
        GateAnswer gate = CompileGate.Check(_root, TimeSpan.FromSeconds(60));
        Report("compile gate with an error", clock.Elapsed);
        Assert.Equal(GateResult.Errors, gate.Result);
        Assert.Contains("Assets/Editor/Broken.cs:1:", gate.Text, StringComparison.Ordinal);
        File.WriteAllText(broken, "namespace BridgeHost { static class Broken { static int Speed() { return 1; } } }\n");
        clock.Restart();
        Assert.Equal(GateResult.Clean, CompileGate.Check(_root, TimeSpan.FromSeconds(60)).Result);
        Report("compile gate after the fix", clock.Elapsed);

        string tests = Path.Combine(_root, "Assets", "E2ETests");
        Directory.CreateDirectory(tests);
        File.WriteAllText(
            Path.Combine(tests, "E2E.Tests.asmdef"),
            "{\"name\":\"E2E.Tests\",\"references\":[\"UnityEngine.TestRunner\",\"UnityEditor.TestRunner\"],\"includePlatforms\":[\"Editor\"],"
            + "\"overrideReferences\":true,\"precompiledReferences\":[\"nunit.framework.dll\"],\"defineConstraints\":[\"UNITY_INCLUDE_TESTS\"]}");
        File.WriteAllText(
            Path.Combine(tests, "RedTests.cs"),
            "using NUnit.Framework;\nnamespace E2E\n{\n    public class RedTests\n    {\n        [Test] public void Passes() { }\n\n"
            + "        [Test] public void Fails() { Assert.AreEqual(3, 2); }\n    }\n}\n");
        Assert.Matches(@"^compiled in \d+\.\d s: 0 errors\n$", Ug(log, "compile"));

        clock.Restart();
        string red = Ug(log, "test", "--filter", "E2E");
        Report("ug test (1 red, 1 green)", clock.Elapsed);
        Tokens("ug test red answer", red);
        Assert.Matches(@"^EditMode: 1 failed, 1 passed in \d+\.\d s\n  Assets/E2ETests/RedTests\.cs:8  E2E\.RedTests\.Fails  ", red);

        clock.Restart();
        string green = Ug(log, "test", "--filter", "PlayLogTests");
        Report("ug test (green)", clock.Elapsed);
        Tokens("ug test green answer", green);
        Assert.Matches(@"^EditMode: \d+ passed in \d+\.\d s\n$", green);

        clock.Restart();
        string played = Ug(log, "play", "--for", "2");
        Report("ug play --for 2", clock.Elapsed);
        Assert.Matches(@"^play \d+\.\d s, stopped \d\d:\d\d:\d\d\n  ", played);
        Assert.Equal(played, Ug(log, "log"));
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

    private static void Tokens(string what, string answer) =>
        TestContext.Current.TestOutputHelper?.WriteLine(what + ": " + (answer.Length / 4).ToString(CultureInfo.InvariantCulture) + " tokens (" + answer.Length.ToString(CultureInfo.InvariantCulture) + " bytes)");

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
