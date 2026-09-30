using UnityGraph.EditorLink.Tests;

namespace UnityGraph.Cli.Tests;

/// <summary>
/// The editor-ops commands (`ug scene`, `ug play`, `ug tree`, `ug set`, ...) against a fake bridge:
/// the request each sends, the lines it prints, play-mode waits, and the answers when the bridge is
/// missing, too old, or asked to edit a vendored folder.
/// </summary>
[Collection(nameof(EditorCommandTests))]
public sealed class EditorOpsCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-ops-cli-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeBridge _bridge = new();

    public EditorOpsCommandTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Assets"));
        Directory.CreateDirectory(Path.Combine(_root, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_root, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.3.14f1\n");
        EditorCommands.ProcessCheck = _ => true;
    }

    public void Dispose()
    {
        EditorCommands.ProcessCheck = null;
        _bridge.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("/scene", "scene")]
    [InlineData("/scene/open?path=Assets%2FScenes%2FMain.unity", "scene", "open", "Assets/Scenes/Main.unity")]
    [InlineData("/scene/open?path=Assets%2FUI.unity&additive=true", "scene", "open", "Assets/UI.unity", "--additive")]
    [InlineData("/scene/new?path=Assets%2FLevel%202.unity", "scene", "new", "Assets/Level 2.unity")]
    [InlineData("/scene/close?path=Assets%2FUI.unity", "scene", "close", "Assets/UI.unity")]
    [InlineData("/scene/active?path=Assets%2FUI.unity", "scene", "active", "Assets/UI.unity")]
    [InlineData("/save", "save")]
    [InlineData("/save?path=Assets%2FMain.unity", "save", "Assets/Main.unity")]
    [InlineData("/pause", "pause")]
    [InlineData("/tree", "tree")]
    [InlineData("/tree?object=Player&depth=1", "tree", "Player", "--depth", "1")]
    [InlineData("/inspect?object=Main%3AEnemy%232&component=Rigidbody", "inspect", "Main:Enemy#2", "--component", "Rigidbody")]
    [InlineData("/inspect?object=Assets%2FArt%2FHero.png&importer=true", "inspect", "Assets/Art/Hero.png", "--importer")]
    [InlineData("/find?name=Enemy&tag=Respawn&layer=8&component=Rigidbody", "find", "Enemy", "--live", "--tag", "Respawn", "--layer", "8", "--component", "Rigidbody")]
    [InlineData("/find?tag=Player", "find", "--live", "--tag", "Player")]
    [InlineData("/object/create", "object", "create")]
    [InlineData("/object/create?name=Spawn&parent=Level&prefab=Assets%2FSpawner.prefab", "object", "create", "Spawn", "--parent", "Level", "--prefab", "Assets/Spawner.prefab")]
    [InlineData("/object/create?primitive=Cube&scene=UI", "object", "create", "--primitive", "Cube", "--scene", "UI")]
    [InlineData("/object/delete?object=Player%2FGun", "object", "delete", "Player/Gun")]
    [InlineData("/object/rename?object=Player&name=Hero%20One", "object", "rename", "Player", "Hero One")]
    [InlineData("/object/move?object=Gun&parent=Player&index=0", "object", "move", "Gun", "--parent", "Player", "--index", "0")]
    [InlineData("/object/move?object=Player%2FGun&parent=", "object", "move", "Player/Gun", "--root")]
    [InlineData("/object/move?object=Player&scene=UI", "object", "move", "Player", "--scene", "UI")]
    [InlineData("/object/active?object=Player&value=false", "object", "active", "Player", "false")]
    [InlineData("/object/duplicate?object=Enemy&name=Enemy2", "object", "duplicate", "Enemy", "--name", "Enemy2")]
    [InlineData("/component/add?object=Player&type=Rigidbody", "component", "add", "Player", "Rigidbody")]
    [InlineData("/component/remove?object=Player&component=BoxCollider%232", "component", "remove", "Player", "BoxCollider#2")]
    [InlineData("/set?object=Player&component=PlayerController&field=speed&value=7.5", "set", "Player", "speed", "7.5", "--component", "PlayerController")]
    [InlineData("/set?object=Player&field=m_LocalPosition&value=1%2C2%2C3", "set", "Player", "m_LocalPosition", "1,2,3")]
    [InlineData("/set?object=Assets%2FArt%2FHero.png&field=m_MaxTextureSize&value=512&importer=true", "set", "Assets/Art/Hero.png", "m_MaxTextureSize", "512", "--importer")]
    [InlineData("/undo", "undo")]
    [InlineData("/redo", "redo")]
    [InlineData("/select", "select")]
    [InlineData("/select?object=Player", "select", "Player")]
    [InlineData("/select?clear=true", "select", "--clear")]
    public void Each_command_sends_its_request_and_prints_the_lines(string request, params string[] args)
    {
        Answer(_ => (200, "{\"lines\":[\"first line\",\"  second  line\"]}"));

        var (exitCode, output) = Run(args);

        Assert.Equal(0, exitCode);
        Assert.Equal("first line\n  second  line\n", output);
        Assert.Equal(["/status", request], _bridge.Requests);
    }

    [Fact]
    public void A_refusal_prints_the_bridges_reason()
    {
        Answer(_ => (200, "{\"error\":\"ambiguous: Enemy (3 in Main): Main:Enemy#1, Main:Enemy#2, Main:Enemy#3\"}"));

        var (exitCode, output) = Run("inspect", "Enemy");

        Assert.Equal(0, exitCode);
        Assert.Equal("ambiguous: Enemy (3 in Main): Main:Enemy#1, Main:Enemy#2, Main:Enemy#3\n", output);
    }

    [Fact]
    public void Play_waits_for_play_mode_and_says_how_long_it_took()
    {
        int polls = 0;
        Serve(path => path switch
        {
            "/play" => (200, "{\"lines\":[\"playing\"],\"wait\":\"playing\"}"),
            _ => (200, FakeBridge.Status(Interlocked.Increment(ref polls) < 3 ? "idle" : "playing", 3)),
        });

        var (exitCode, output) = Run("play");

        Assert.Equal(0, exitCode);
        Assert.Matches(@"^playing \(entered in \d+\.\d s\)\n$", output);
        Assert.Equal(["/status", "/play", "/status", "/status"], _bridge.Requests.Take(4));
    }

    [Fact]
    public void Stop_waits_for_edit_mode_and_prints_the_bridges_digest()
    {
        int polls = 0;
        Serve(path => path switch
        {
            "/stop" => (200, "{\"lines\":[\"stopped after 12.4 s: 0 errors, 2 warnings (ug console)\"],\"wait\":\"idle\"}"),
            _ => (200, FakeBridge.Status(Interlocked.Increment(ref polls) < 3 ? "playing" : "idle", 3)),
        });

        Assert.Equal("stopped after 12.4 s: 0 errors, 2 warnings (ug console)\n", Run("stop").Output);
        Assert.Equal(3, polls);
    }

    [Fact]
    public void Play_that_never_starts_says_so_at_the_deadline()
    {
        Serve(path => path == "/play"
            ? (200, "{\"lines\":[\"playing\"],\"wait\":\"playing\"}")
            : (200, FakeBridge.Status("idle", 3)));

        Assert.Equal("Unity was still idle after 1 s (entering play mode)\n", Run("play", "--timeout", "1").Output);
    }

    [Fact]
    public void A_bridge_without_ops_is_too_old()
    {
        _bridge.Publish(_root, bridge: "0.1.0");
        _bridge.Handle = _ => (200, FakeBridge.Status("idle", 3));

        Assert.Equal("bridge 0.1.0 is too old for ug scene open: run ug bridge install again\n", Run("scene", "open", "Assets/Main.unity").Output);
        Assert.Equal("bridge 0.1.0 is too old for ug find --live: run ug bridge install again\n", Run("find", "--live", "--tag", "Player").Output);
        Assert.Equal(["/status", "/status"], _bridge.Requests);
    }

    [Fact]
    public void Without_the_bridge_the_commands_say_they_need_it()
    {
        Assert.Equal("""
            Unity not reachable (no bridge.json: Unity is closed or the bridge is not installed); ug object create needs the bridge
              the bridge is not in this project; `ug bridge install` shows how to add it

            """.ReplaceLineEndings("\n"), Run("object", "create", "Spawn").Output);
    }

    [Fact]
    public void A_lost_answer_says_Unity_did_not_answer()
    {
        Answer(path => path == "/tree" ? (500, "{\"error\":\"boom\"}") : (200, FakeBridge.Status("idle", 3)));

        Assert.Equal("no answer from Unity (bridge answered 500: boom)\n", Run("tree").Output);
    }

    [Theory]
    [InlineData("Assets/Plugins/Vendor.prefab:Barrel is in the vendored folder Assets/Plugins/, which ug does not edit\n", "object", "delete", "Assets/Plugins/Vendor.prefab:Barrel")]
    [InlineData("Assets/ThirdParty/Kit.prefab is in the vendored folder Assets/ThirdParty/, which ug does not edit\n", "set", "Assets/ThirdParty/Kit.prefab", "m_Name", "Kit2")]
    [InlineData("Assets/Plugins/Vendor.prefab:Root is in the vendored folder Assets/Plugins/, which ug does not edit\n", "object", "create", "Spawn", "--parent", "Assets/Plugins/Vendor.prefab:Root")]
    [InlineData("Assets/Plugins/Vendor.prefab:Root is in the vendored folder Assets/Plugins/, which ug does not edit\n", "object", "move", "Player", "--parent", "Assets/Plugins/Vendor.prefab:Root")]
    [InlineData("Assets/Plugins/Hero.png is in the vendored folder Assets/Plugins/, which ug does not edit\n", "set", "Assets/Plugins/Hero.png", "m_MaxTextureSize", "512", "--importer")]
    public void Edits_to_vendored_folders_are_refused_before_asking_Unity(string expected, params string[] args)
    {
        Answer(_ => (200, "{\"lines\":[\"done\"]}"));

        Assert.Equal(expected, Run(args).Output);
        Assert.Empty(_bridge.Requests);
    }

    [Fact]
    public void Reads_and_prefab_instances_from_vendored_folders_are_allowed()
    {
        Answer(_ => (200, "{\"lines\":[\"done\"]}"));

        Assert.Equal("done\n", Run("inspect", "Assets/Plugins/Vendor.prefab").Output);
        Assert.Equal("done\n", Run("object", "create", "--prefab", "Assets/Plugins/Vendor.prefab").Output);
    }

    [Fact]
    public void The_vendored_setting_decides_what_is_refused()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".claude"));
        File.WriteAllText(Path.Combine(_root, ".claude", "unitygraph.json"), "{\"vendored\":[\"Assets/Imported/\"]}");
        Answer(_ => (200, "{\"lines\":[\"done\"]}"));

        Assert.Equal("Assets/Imported/Tree.prefab is in the vendored folder Assets/Imported/, which ug does not edit\n", Run("object", "rename", "Assets/Imported/Tree.prefab", "Oak").Output);
        Assert.Equal("done\n", Run("object", "rename", "Assets/Plugins/Tree.prefab", "Oak").Output);
    }

    [Theory]
    [InlineData("--tag, --layer and --component need --live", "find", "Enemy", "--tag", "Respawn")]
    [InlineData("give a name to find", "find")]
    [InlineData("--kind is for the graph; --live finds game objects only", "find", "Enemy", "--live", "--kind", "type")]
    public void Find_checks_its_options(string error, params string[] args)
    {
        var output = new StringWriter();
        var errors = new StringWriter();

        int exitCode = CliApp.Run([.. args, "--project", _root], output, errors);

        Assert.Equal(1, exitCode);
        Assert.Contains(error, errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_commands_obey_the_module_switch()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".claude"));
        File.WriteAllText(Path.Combine(_root, ".claude", "unitygraph.json"), "{\"modules\":{\"editor-link\":false}}");

        Assert.Equal("editor-link is off in .claude/unitygraph.json\n", Run("tree").Output);
        Assert.Equal("editor-link is off in .claude/unitygraph.json\n", Run("find", "--live", "--tag", "Player").Output);
    }

    // An idle 0.2.0 bridge answering the ops requests with handle.
    private void Answer(Func<string, (int, string)> handle) => Serve(path => path == "/status" ? (200, FakeBridge.Status("idle", 3)) : handle(path));

    // A 0.2.0 bridge answering every request, /status too, with handle.
    private void Serve(Func<string, (int, string)> handle)
    {
        _bridge.Publish(_root, bridge: "0.2.0");
        _bridge.Handle = handle;
    }

    private (int ExitCode, string Output) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exitCode = CliApp.Run([.. args, "--project", _root], output, error);
        Assert.Equal("", error.ToString());
        return (exitCode, output.ToString().ReplaceLineEndings("\n"));
    }
}
