using Ungrep.EditorLink.Tests;

namespace Ungrep.Cli.Tests;

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

    // The default vendored folders, joined with | and escaped: what `ug prefab apply` sends so the bridge can refuse them.
    private const string DefaultVendored =
        "Assets%2FPlugins%2F%7CAssets%2F3rd%20Party%2F%7CAssets%2F3rdParty%2F%7CAssets%2FThird%20Party%2F%7CAssets%2FThirdParty%2F%7CAssets%2FVendor%2F%7CAssets%2FAssetStore%2F%7CPackages%2F";

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
    [InlineData("/event/list?object=Canvas%2FButton", "event", "list", "Canvas/Button")]
    [InlineData("/event/list?object=Canvas%2FButton&event=onClick&component=Button", "event", "list", "Canvas/Button", "onClick", "--component", "Button")]
    [InlineData("/event/add?object=Canvas%2FButton&event=onClick&target=Canvas%3AUiHandler&method=OnPlay", "event", "add", "Canvas/Button", "onClick", "Canvas:UiHandler", "OnPlay")]
    [InlineData("/event/add?object=Panel&event=onClick&target=Hud&method=SetActive&arg=false", "event", "add", "Panel", "onClick", "Hud", "SetActive", "--arg", "false")]
    [InlineData("/event/remove?object=Canvas%2FButton&event=onClick", "event", "remove", "Canvas/Button", "onClick")]
    [InlineData("/event/remove?object=Canvas%2FButton&event=onClick&index=1&component=Button", "event", "remove", "Canvas/Button", "onClick", "1", "--component", "Button")]
    [InlineData("/prefab/create?object=Level%2FCrate&path=Assets%2FPrefabs%2FCrate.prefab", "prefab", "create", "Level/Crate", "Assets/Prefabs/Crate.prefab")]
    [InlineData("/prefab/apply?object=Main%3ATurret&vendored=" + DefaultVendored, "prefab", "apply", "Main:Turret")]
    [InlineData("/prefab/revert?object=Turret", "prefab", "revert", "Turret")]
    [InlineData("/prefab/unpack?object=Turret", "prefab", "unpack", "Turret")]
    [InlineData("/prefab/unpack?object=Turret&completely=true", "prefab", "unpack", "Turret", "--completely")]
    [InlineData("/asset/folder?path=Assets%2FArt%2FIcons", "asset", "folder", "Assets/Art/Icons")]
    [InlineData("/asset/create?path=Assets%2FMats%2FInk.mat&type=Material&shader=Unlit%2FColor", "asset", "create", "Assets/Mats/Ink.mat", "Material", "--shader", "Unlit/Color")]
    [InlineData("/asset/create?path=Assets%2FData%2FWave1.asset&type=Wave", "asset", "create", "Assets/Data/Wave1.asset", "Wave")]
    [InlineData("/asset/move?from=Assets%2FA.png&to=Assets%2FArt%2FA.png&vendored=" + DefaultVendored, "asset", "move", "Assets/A.png", "Assets/Art/A.png")]
    [InlineData("/asset/rename?path=Assets%2FA.png&name=Hero&vendored=" + DefaultVendored, "asset", "rename", "Assets/A.png", "Hero")]
    [InlineData("/asset/duplicate?path=Assets%2FA.png&to=Assets%2FB.png", "asset", "duplicate", "Assets/A.png", "Assets/B.png")]
    [InlineData("/asset/duplicate?path=Packages%2Fcom.x%2FA.prefab", "asset", "duplicate", "Packages/com.x/A.prefab")]
    [InlineData("/asset/delete?path=Assets%2FOld&vendored=" + DefaultVendored, "asset", "delete", "Assets/Old")]
    [InlineData("/asset/reimport?path=Assets%2FA.png&vendored=" + DefaultVendored, "asset", "reimport", "Assets/A.png")]
    [InlineData("/project", "project")]
    [InlineData("/package/list", "package", "list")]
    [InlineData("/package/add?id=com.unity.cinemachine", "package", "add", "com.unity.cinemachine")]
    [InlineData("/package/add?id=com.unity.cinemachine%403.1.2", "package", "add", "com.unity.cinemachine@3.1.2")]
    [InlineData("/package/add?id=file%3A..%2Fmy-pkg", "package", "add", "file:../my-pkg")]
    [InlineData("/package/add?id=https%3A%2F%2Fgithub.com%2Fx%2Fy.git%23v1.0", "package", "add", "https://github.com/x/y.git#v1.0")]
    [InlineData("/package/remove?name=com.unity.cinemachine", "package", "remove", "com.unity.cinemachine")]
    [InlineData("/shot", "shot")]
    [InlineData("/shot?view=scene&width=640&height=360", "shot", "scene", "--width", "640", "--height", "360")]
    [InlineData("/shot?view=Main%3ACamera", "shot", "Main:Camera")]
    [InlineData("/api?type=Transform", "api", "Transform")]
    [InlineData("/api?type=Physics&member=Raycast", "api", "Physics", "Raycast")]
    [InlineData("/api?type=Physics.Raycast", "api", "Physics.Raycast")]
    [InlineData("/stats", "perf")]
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

    [Theory]
    [InlineData("/tree?ui=Menu", "tree", "--ui", "Menu")]
    [InlineData("/tree?depth=3&ui=Menu%2FPanel", "tree", "--ui", "Menu/Panel", "--depth", "3")]
    [InlineData("/inspect?object=Menu%2FPlay", "inspect", "Menu/Play")]
    [InlineData("/set?object=Menu%2FPlay&field=style.width&value=50%25", "set", "Menu/Play", "style.width", "50%")]
    [InlineData("/shot?view=Assets%2FUI%2FMenu.uxml", "shot", "Assets/UI/Menu.uxml")]
    [InlineData("/shot?view=Assets%2FUI%2FMenu.uxml&width=540&height=960&panel=Assets%2FUI%2FPanel.asset", "shot", "Assets/UI/Menu.uxml", "--panel", "Assets/UI/Panel.asset", "--width", "540", "--height", "960")]
    [InlineData("/asset/create?path=Assets%2FUI%2FMenu.uxml", "asset", "create", "Assets/UI/Menu.uxml")]
    [InlineData("/asset/create?path=Assets%2FUI%2FGame.tss&type=TSS", "asset", "create", "Assets/UI/Game.tss", "TSS")]
    public void Ui_toolkit_requests_carry_their_addresses_and_options(string request, params string[] args)
    {
        _bridge.Publish(_root, bridge: "0.7.0");
        _bridge.Handle = path => path == "/status" ? (200, FakeBridge.Status("idle", 3)) : (200, "{\"lines\":[\"Menu  UIDocument  Assets/UI/Menu.uxml\"]}");

        var (exitCode, output) = Run(args);

        Assert.Equal(0, exitCode);
        Assert.Equal("Menu  UIDocument  Assets/UI/Menu.uxml\n", output);
        Assert.Equal(["/status", request], _bridge.Requests);
    }

    [Fact]
    public void A_bridge_before_ui_toolkit_refuses_tree_ui_and_uxml_shots()
    {
        _bridge.Publish(_root, bridge: "0.6.0");
        _bridge.Handle = path => path == "/status" ? (200, FakeBridge.Status("idle", 3)) : (200, "{\"lines\":[\"ok\"]}");

        Assert.Equal("bridge 0.6.0 is too old for ug tree --ui: run ug bridge install again\n", Run("tree", "--ui", "Menu").Output);
        Assert.Equal("bridge 0.6.0 is too old for ug shot Assets/UI/Menu.uxml: run ug bridge install again\n", Run("shot", "Assets/UI/Menu.uxml").Output);
        Assert.Equal("bridge 0.6.0 is too old for ug shot --panel: run ug bridge install again\n", Run("shot", "game", "--panel", "Assets/UI/Panel.asset").Output);
        Assert.Equal("ok\n", Run("tree", "Menu").Output);
        Assert.Equal(["/status", "/status", "/status", "/status", "/tree?object=Menu"], _bridge.Requests);
    }

    [Theory]
    [InlineData("/inspect?object=Level%2FSpawner&component=WaveSpawner&field=waves", "inspect", "Level/Spawner", "--component", "WaveSpawner", "--field", "waves")]
    [InlineData("/inspect?object=Level%2FSpawner&field=waves%5B2%5D.count&from=200", "inspect", "Level/Spawner", "--field", "waves[2].count", "--from", "200")]
    [InlineData("/inspect?object=Assets%2FMats%2FInk.mat&from=150", "inspect", "Assets/Mats/Ink.mat", "--from", "150")]
    [InlineData("/inspect?object=Player&from=0", "inspect", "Player", "--from", "-3")]
    [InlineData("/ui?all=true&from=150", "ui", "--all", "--from", "150")]
    [InlineData("/prefab/overrides?object=Level%2FCrate", "prefab", "overrides", "Level/Crate")]
    [InlineData("/select?all=true", "select", "--all")]
    [InlineData("/find?name=Enemy&all=true", "find", "Enemy", "--live", "--all")]
    [InlineData("/api?type=Transform&all=true", "api", "Transform", "--all")]
    [InlineData("/api?type=Physics&member=Raycast&all=true", "api", "Physics", "Raycast", "--all")]
    [InlineData("/perf/memory?top=60&object=Assets%2FHero.fbx", "perf", "memory", "--object", "Assets/Hero.fbx", "--top", "60")]
    [InlineData("/camera/noise?camera=FollowCam&all=true", "camera", "noise", "FollowCam", "--all")]
    public void The_c1_escapes_carry_their_options(string request, params string[] args)
    {
        _bridge.Publish(_root, bridge: "0.9.0");
        _bridge.Handle = path => path == "/status" ? (200, FakeBridge.Status("idle", 3)) : (200, "{\"lines\":[\"whole\"]}");

        var (exitCode, output) = Run(args);

        Assert.Equal(0, exitCode);
        Assert.Equal("whole\n", output);
        Assert.Equal(["/status", request], _bridge.Requests);
    }

    [Fact]
    public void A_bridge_before_c1_refuses_the_escapes_by_name_and_keeps_the_defaults()
    {
        _bridge.Publish(_root, bridge: "0.8.0");
        _bridge.Handle = path => path == "/status" ? (200, FakeBridge.Status("idle", 3)) : (200, "{\"lines\":[\"ok\"]}");

        Assert.Equal("bridge 0.8.0 is too old for ug inspect --field: run ug bridge install again\n", Run("inspect", "Player", "--field", "speed").Output);
        Assert.Equal("bridge 0.8.0 is too old for ug inspect --from: run ug bridge install again\n", Run("inspect", "Player", "--from", "150").Output);
        Assert.Equal("bridge 0.8.0 is too old for ug ui --from: run ug bridge install again\n", Run("ui", "--all", "--from", "150").Output);
        Assert.Equal("--from pages ug ui --all\n", Run("ui", "--from", "150").Output);
        Assert.Equal("bridge 0.8.0 is too old for ug prefab overrides: run ug bridge install again\n", Run("prefab", "overrides", "Crate").Output);
        Assert.Equal("bridge 0.8.0 is too old for ug select --all: run ug bridge install again\n", Run("select", "--all").Output);
        Assert.Equal("bridge 0.8.0 is too old for ug run --all: run ug bridge install again\n", Run("run", "--list", "--all").Output);
        Assert.Equal("bridge 0.8.0 is too old for ug api --all: run ug bridge install again\n", Run("api", "Transform", "--all").Output);
        Assert.Equal("bridge 0.8.0 is too old for ug perf memory --object --top: run ug bridge install again\n", Run("perf", "memory", "--object", "Player", "--top", "30").Output);
        Assert.Equal("bridge 0.8.0 is too old for ug camera noise --all: run ug bridge install again\n", Run("camera", "noise", "FollowCam", "--all").Output);
        var refused = new StringWriter();
        Assert.Equal(1, CliApp.Run(["run", "Tools.Spawn", "--all", "--project", _root], new StringWriter(), refused));
        Assert.StartsWith("--all goes with --list", refused.ToString(), StringComparison.Ordinal);
        Assert.Equal("ok\n", Run("perf", "memory", "--top", "30").Output);
        Assert.Equal("ok\n", Run("inspect", "Player").Output);
        Assert.Equal("/inspect?object=Player", _bridge.Requests.Last());
    }

    [Theory]
    [InlineData("/shot?from=0%2C10%2C-10&look=Enemy", "shot", "--from", "0,10,-10", "--look", "Enemy")]
    [InlineData("/shot?view=Main%20Camera&rotation=30%2C45%2C0&fov=40.5", "shot", "Main Camera", "--rotation", "30,45,0", "--fov", "40.5")]
    [InlineData("/shot?look=1%2C0%2C2&camera=Rig%2FCam", "shot", "--look", "1,0,2", "--camera", "Rig/Cam")]
    [InlineData("/shot?view=scene&frame=Player&keep=true", "shot", "scene", "--frame", "Player", "--keep")]
    [InlineData("/shot?view=Player&width=200&height=150&angles=true", "shot", "Player", "--angles", "--width", "200", "--height", "150")]
    [InlineData("/shot?view=Player&angles=true&distance=4.5", "shot", "Player", "--angles", "--distance", "4.5")]
    [InlineData("/shot?view=Player&orbit=", "shot", "Player", "--orbit")]
    [InlineData("/shot?view=0%2C1%2C0&orbit=4&elevations=0%2C45", "shot", "0,1,0", "--orbit", "4", "--elevations", "0,45")]
    public void Shot_viewpoints_and_sheets_send_their_options(string request, params string[] args)
    {
        _bridge.Publish(_root, bridge: "0.6.0");
        _bridge.Handle = path => path == "/status" ? (200, FakeBridge.Status("idle", 3)) : (200, "{\"lines\":[\"Library/ungrep/shots/view.png  960x540  80 KB  (from 0, 10, -10)\"]}");

        var (exitCode, output) = Run(args);

        Assert.Equal(0, exitCode);
        Assert.Equal("Library/ungrep/shots/view.png  960x540  80 KB  (from 0, 10, -10)\n", output);
        Assert.Equal(["/status", request], _bridge.Requests);
    }

    [Fact]
    public void A_bridge_before_shot_cameras_refuses_the_new_options_and_takes_plain_shots()
    {
        _bridge.Publish(_root, bridge: "0.5.0");
        _bridge.Handle = path => path == "/status" ? (200, FakeBridge.Status("idle", 3)) : (200, "{\"lines\":[\"Library/ungrep/shots/game.png\"]}");

        Assert.Equal("bridge 0.5.0 is too old for ug shot --angles: run ug bridge install again\n", Run("shot", "Player", "--angles").Output);
        Assert.Equal("bridge 0.5.0 is too old for ug shot --orbit: run ug bridge install again\n", Run("shot", "Player", "--orbit", "--width", "100").Output);
        Assert.Equal("bridge 0.5.0 is too old for ug shot --from: run ug bridge install again\n", Run("shot", "--from", "1,2,3").Output);
        Assert.Equal("bridge 0.5.0 is too old for ug shot --frame: run ug bridge install again\n", Run("shot", "scene", "--frame", "Player", "--keep").Output);
        Assert.Equal("Library/ungrep/shots/game.png\n", Run("shot", "--width", "640").Output);
        Assert.Equal(["/status", "/status", "/status", "/status", "/status", "/shot?width=640"], _bridge.Requests);
    }

    [Theory]
    [InlineData("/camera", "camera")]
    [InlineData("/camera?all=true", "camera", "--all")]
    [InlineData("/camera?all=true", "camera", "list", "--all")]
    [InlineData("/camera/create", "camera", "create")]
    [InlineData("/camera/create?name=Close%20Up&preset=third-person&follow=Player&look-at=Enemy&priority=20&fov=40.5&parent=Rig&at=0%2C2%2C-4",
        "camera", "create", "Close Up", "--preset", "third-person", "--follow", "Player", "--look-at", "Enemy", "--priority", "20", "--fov", "40.5", "--parent", "Rig", "--at", "0,2,-4")]
    [InlineData("/camera/priority?camera=FollowCam&priority=30", "camera", "priority", "FollowCam", "30")]
    [InlineData("/camera/body?camera=FollowCam&n=0", "camera", "body", "FollowCam")]
    [InlineData("/camera/body?camera=Rig%2FCam&type=ThirdPersonFollow&n=2&a0=CameraDistance%3D5&a1=ShoulderOffset%3D0.8%2C-0.4%2C0&follow=Player",
        "camera", "body", "Rig/Cam", "ThirdPersonFollow", "CameraDistance=5", "ShoulderOffset=0.8,-0.4,0", "--follow", "Player")]
    [InlineData("/camera/aim?camera=FollowCam&n=1&a0=Damping%3D1%2C1&look-at=none", "camera", "aim", "FollowCam", "Damping=1,1", "--look-at", "none")]
    [InlineData("/camera/aim?camera=FollowCam&type=none&n=0", "camera", "aim", "FollowCam", "none")]
    [InlineData("/camera/noise?camera=FollowCam", "camera", "noise", "FollowCam")]
    [InlineData("/camera/noise?camera=FollowCam&profile=6D%20Shake&amplitude=2&frequency=0.5", "camera", "noise", "FollowCam", "6D Shake", "--amplitude", "2", "--frequency", "0.5")]
    [InlineData("/camera/blend", "camera", "blend")]
    [InlineData("/camera/blend?style=Cut&time=0.5&from=any&to=CloseUp&brain=Main%20Camera",
        "camera", "blend", "--style", "Cut", "--time", "0.5", "--from", "any", "--to", "CloseUp", "--brain", "Main Camera")]
    [InlineData("/camera/force?camera=CloseUp", "camera", "force", "CloseUp")]
    [InlineData("/camera/force?camera=CloseUp&brain=Main%20Camera", "camera", "force", "CloseUp", "--brain", "Main Camera")]
    [InlineData("/camera/release", "camera", "release")]
    [InlineData("/camera/brain", "camera", "brain")]
    [InlineData("/camera/brain?camera=Main%20Camera&style=EaseIn&time=1", "camera", "brain", "Main Camera", "--style", "EaseIn", "--time", "1")]
    public void Camera_commands_send_their_requests(string request, params string[] args)
    {
        _bridge.Publish(_root, bridge: "0.6.0");
        _bridge.Handle = path => path == "/status"
            ? (200, FakeBridge.Status("idle", 3))
            : (200, "{\"lines\":[\"CloseUp  priority 10 -> 30  (Main.unity modified, not saved)\",\"live: CloseUp (brain Main Camera)\"]}");

        var (exitCode, output) = Run(args);

        Assert.Equal(0, exitCode);
        Assert.Equal("CloseUp  priority 10 -> 30  (Main.unity modified, not saved)\nlive: CloseUp (brain Main Camera)\n", output);
        Assert.Equal(["/status", request], _bridge.Requests);
    }

    [Theory]
    [InlineData("camera", "camera")]
    [InlineData("camera list", "camera", "list")]
    [InlineData("camera create", "camera", "create", "--preset", "follow")]
    [InlineData("camera priority", "camera", "priority", "FollowCam", "3")]
    [InlineData("camera body", "camera", "body", "FollowCam", "Follow")]
    [InlineData("camera aim", "camera", "aim", "FollowCam")]
    [InlineData("camera noise", "camera", "noise", "FollowCam", "none")]
    [InlineData("camera blend", "camera", "blend", "--style", "Cut")]
    [InlineData("camera force", "camera", "force", "FollowCam")]
    [InlineData("camera release", "camera", "release")]
    [InlineData("camera brain", "camera", "brain")]
    public void A_bridge_before_cameras_refuses_each_camera_command(string command, params string[] args)
    {
        _bridge.Publish(_root, bridge: "0.5.0");
        _bridge.Handle = _ => (200, FakeBridge.Status("idle", 3));

        Assert.Equal("bridge 0.5.0 is too old for ug " + command + ": run ug bridge install again\n", Run(args).Output);
        Assert.Equal(["/status"], _bridge.Requests);
    }

    [Fact]
    public void A_camera_refusal_prints_the_bridges_reason()
    {
        _bridge.Publish(_root, bridge: "0.6.0");
        _bridge.Handle = path => path == "/status"
            ? (200, FakeBridge.Status("idle", 3))
            : (200, "{\"error\":\"Cinemachine 2.10.7 is installed; ug camera knows Cinemachine 3.x only: ug set and ug component edit 2.x cameras\"}");

        Assert.Equal(
            "Cinemachine 2.10.7 is installed; ug camera knows Cinemachine 3.x only: ug set and ug component edit 2.x cameras\n",
            Run("camera", "priority", "VCam", "5").Output);
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
    public void A_package_refusal_prints_the_bridges_reason()
    {
        Answer(_ => (200, "{\"error\":\"removing com.ungrep.bridge would end this connection: remove it in Package Manager (Window > Package Manager)\"}"));

        Assert.Equal(
            "removing com.ungrep.bridge would end this connection: remove it in Package Manager (Window > Package Manager)\n",
            Run("package", "remove", "com.ungrep.bridge").Output);
    }

    [Fact]
    public void Package_add_waits_as_long_as_asked_and_then_says_Unity_did_not_answer()
    {
        Serve(path => path == "/package/add?id=com.x"
            ? Slow(1500, (200, "{\"lines\":[\"added com.x  1.0.0  registry\"]}"))
            : (200, FakeBridge.Status("idle", 3)));

        Assert.Equal("no answer from Unity (no answer within 1 s: Unity is busy)\n", Run("package", "add", "com.x", "--timeout", "1").Output);
        Assert.Equal("added com.x  1.0.0  registry\n", Run("package", "add", "com.x", "--timeout", "30").Output);
    }

    [Fact]
    public void Package_remove_waits_like_add()
    {
        Serve(path => path.StartsWith("/package/remove", StringComparison.Ordinal)
            ? Slow(1500, (200, "{\"lines\":[\"removed com.x\"]}"))
            : (200, FakeBridge.Status("idle", 3)));

        Assert.Equal("no answer from Unity (no answer within 1 s: Unity is busy)\n", Run("package", "remove", "com.x", "--timeout", "1").Output);
        Assert.Equal("removed com.x\n", Run("package", "remove", "com.x").Output);
    }

    [Fact]
    public void A_bridge_that_reloads_during_a_package_change_is_reported_as_unavailable()
    {
        Answer(_ => (503, "{\"error\":\"the bridge is stopping (domain reload or quit)\"}"));

        Assert.Equal(
            "no answer from Unity (bridge answered 503: the bridge is stopping (domain reload or quit))\n",
            Run("package", "add", "com.x").Output);
    }

    [Theory]
    [InlineData("File/Quit", "did not run File/Quit: refused, it would close the editor (ug does not quit Unity)")]
    [InlineData("File/Build And Run", "did not run File/Build And Run: refused, it builds a player (ug does not build; use Unity's Build Profiles window)")]
    public void Menu_refusals_are_printed_with_the_menu_path(string target, string refusal)
    {
        Answer(path => path.StartsWith("/run", StringComparison.Ordinal)
            ? (200, "{\"error\":\"" + refusal + "\"}")
            : (200, FakeBridge.Status("idle", 3)));
        _bridge.Publish(_root, bridge: "0.5.0");

        Assert.Equal(refusal + "\n", Run("run", target).Output);
        Assert.Contains("/run?target=" + Uri.EscapeDataString(target) + "&n=0&timeout=60", _bridge.Requests);
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
    [InlineData("Assets/Plugins/Ink.mat is in the vendored folder Assets/Plugins/, which ug does not edit\n", "set", "Assets/Plugins/Ink.mat", "_Color", "#ff0000")]
    [InlineData("Assets/Plugins/Deep is in the vendored folder Assets/Plugins/, which ug does not edit\n", "asset", "folder", "Assets/Plugins/Deep")]
    [InlineData("Assets/Plugins/Ink.mat is in the vendored folder Assets/Plugins/, which ug does not edit\n", "asset", "create", "Assets/Plugins/Ink.mat", "Material")]
    [InlineData("Assets/Plugins/A.png is in the vendored folder Assets/Plugins/, which ug does not edit\n", "asset", "move", "Assets/Plugins/A.png", "Assets/A.png")]
    [InlineData("Assets/Plugins/A.png is in the vendored folder Assets/Plugins/, which ug does not edit\n", "asset", "move", "Assets/A.png", "Assets/Plugins/A.png")]
    [InlineData("Assets/Plugins/A.png is in the vendored folder Assets/Plugins/, which ug does not edit\n", "asset", "rename", "Assets/Plugins/A.png", "B")]
    [InlineData("Assets/ThirdParty/B.png is in the vendored folder Assets/ThirdParty/, which ug does not edit\n", "asset", "duplicate", "Assets/A.png", "Assets/ThirdParty/B.png")]
    [InlineData("Packages/com.x/B.png is in the vendored folder Packages/, which ug does not edit\n", "asset", "duplicate", "Assets/A.png", "Packages/com.x/B.png")]
    [InlineData("Assets/Plugins/A.png is in the vendored folder Assets/Plugins/, which ug does not edit\n", "asset", "delete", "Assets/Plugins/A.png")]
    [InlineData("Assets/Plugins/A.png is in the vendored folder Assets/Plugins/, which ug does not edit\n", "asset", "reimport", "Assets/Plugins/A.png")]
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
        Assert.Equal("done\n", Run("asset", "duplicate", "Assets/Plugins/Vendor.prefab", "Assets/Prefabs/Vendor.prefab").Output);
        Assert.Equal("done\n", Run("asset", "duplicate", "Packages/com.x/Vendor.prefab", "Assets/Prefabs/Vendor.prefab").Output);
    }

    [Fact]
    public void Prefab_apply_sends_the_projects_vendored_folders_for_the_bridge_to_check()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".claude"));
        File.WriteAllText(Path.Combine(_root, ".claude", "ungrep.json"), "{\"vendored\":[\"Assets/Imported/\",\"Assets/Kits/\"]}");
        Answer(_ => (200, "{\"lines\":[\"done\"]}"));

        Assert.Equal("done\n", Run("prefab", "apply", "Turret").Output);
        Assert.Equal(["/status", "/prefab/apply?object=Turret&vendored=Assets%2FImported%2F%7CAssets%2FKits%2F"], _bridge.Requests);
    }

    [Fact]
    public void Asset_move_and_delete_send_the_projects_vendored_folders_for_the_bridge_to_check()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".claude"));
        File.WriteAllText(Path.Combine(_root, ".claude", "ungrep.json"), "{\"vendored\":[\"Assets/Imported/\",\"Assets/Kits/\"]}");
        Answer(_ => (200, "{\"lines\":[\"done\"]}"));

        Assert.Equal("done\n", Run("asset", "move", "Assets/Art", "Assets/Pics").Output);
        Assert.Equal("done\n", Run("asset", "delete", "Assets/Art").Output);
        Assert.Equal(
            [
                "/status",
                "/asset/move?from=Assets%2FArt&to=Assets%2FPics&vendored=Assets%2FImported%2F%7CAssets%2FKits%2F",
                "/status",
                "/asset/delete?path=Assets%2FArt&vendored=Assets%2FImported%2F%7CAssets%2FKits%2F",
            ],
            _bridge.Requests);
        Assert.Equal("Assets/Kits/A.png is in the vendored folder Assets/Kits/, which ug does not edit\n", Run("asset", "move", "Assets/A.png", "Assets/Kits/A.png").Output);
    }

    [Fact]
    public void The_vendored_setting_decides_what_is_refused()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".claude"));
        File.WriteAllText(Path.Combine(_root, ".claude", "ungrep.json"), "{\"vendored\":[\"Assets/Imported/\"]}");
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

    [Theory]
    [InlineData("/perf/record?seconds=2", "perf", "--record", "2")]
    [InlineData("/perf/record?seconds=0.5&top=20", "perf", "--record", "0.5", "--top", "20")]
    [InlineData("/perf/record?seconds=30&max-frames=2000", "perf", "--record", "30", "--max-frames", "2000")]
    [InlineData("/perf/record?frames=100-200", "perf", "--frames", "100-200")]
    [InlineData("/perf/record?frames=all&top=3", "perf", "--frames", "all", "--top", "3")]
    [InlineData("/perf/profiler?do=status", "perf", "profiler")]
    [InlineData("/perf/profiler?do=status", "perf", "profiler", "status")]
    [InlineData("/perf/profiler?do=start", "perf", "profiler", "start")]
    [InlineData("/perf/profiler?do=start&file=", "perf", "profiler", "start", "--file")]
    [InlineData("/perf/profiler?do=start&file=Library%2Frun.raw&callstacks=true&editor=true", "perf", "profiler", "start", "--file", "Library/run.raw", "--callstacks", "--editor")]
    [InlineData("/perf/profiler?do=stop", "perf", "profiler", "stop")]
    [InlineData("/perf/profiler?do=areas", "perf", "profiler", "areas")]
    [InlineData("/perf/profiler?do=areas&areas=CPU%3Don%2CAudio%3Doff", "perf", "profiler", "areas", "CPU=on", "Audio=off")]
    [InlineData("/perf/counters", "perf", "counters")]
    [InlineData("/perf/counters?names=Draw%20Calls%20Count%7CTotal%20Used%20Memory", "perf", "counters", "Draw Calls Count", "Total Used Memory")]
    [InlineData("/perf/counters?category=Scripts&markers=true&all=true", "perf", "counters", "--category", "Scripts", "--markers", "--all")]
    [InlineData("/perf/timing", "perf", "timing")]
    [InlineData("/perf/memory", "perf", "memory")]
    [InlineData("/perf/memory?top=30&type=Texture2D", "perf", "memory", "--top", "30", "--type", "Texture2D")]
    [InlineData("/perf/memory?object=Main%3APlayer%2FGun", "perf", "memory", "--object", "Main:Player/Gun")]
    [InlineData("/perf/snapshot", "perf", "snapshot", "take")]
    [InlineData("/perf/snapshot?path=Library%2Fbefore.snap", "perf", "snapshot", "take", "Library/before.snap")]
    [InlineData("/perf/frame-debugger?do=enable", "perf", "frame-debugger", "enable")]
    [InlineData("/perf/frame-debugger?do=events", "perf", "frame-debugger", "events")]
    [InlineData("/perf/frame-debugger?do=events&event=12", "perf", "frame-debugger", "events", "12")]
    [InlineData("/perf/frame-debugger?do=events&from=50&top=100", "perf", "frame-debugger", "events", "--from", "50", "--top", "100")]
    [InlineData("/perf/frame-debugger?do=disable", "perf", "frame-debugger", "disable")]
    public void Perf_requests_carry_their_options(string request, params string[] args)
    {
        _bridge.Publish(_root, bridge: "0.8.0");
        _bridge.Handle = path => path == "/status" ? (200, FakeBridge.Status("idle", 3)) : (200, "{\"lines\":[\"profiler  off, holds no frames\"]}");

        var (exitCode, output) = Run(args);

        Assert.Equal(0, exitCode);
        Assert.Equal("profiler  off, holds no frames\n", output);
        Assert.Equal(["/status", request], _bridge.Requests);
    }

    [Fact]
    public void A_bridge_before_the_profiler_refuses_the_perf_additions_and_keeps_plain_perf()
    {
        _bridge.Publish(_root, bridge: "0.7.0");
        _bridge.Handle = path => path == "/status" ? (200, FakeBridge.Status("idle", 3)) : (200, "{\"lines\":[\"edit mode\"]}");

        Assert.Equal("bridge 0.7.0 is too old for ug perf --record: run ug bridge install again\n", Run("perf", "--record", "1").Output);
        Assert.Equal("bridge 0.7.0 is too old for ug perf --frames: run ug bridge install again\n", Run("perf", "--frames", "all").Output);
        Assert.Equal("bridge 0.7.0 is too old for ug perf profiler start: run ug bridge install again\n", Run("perf", "profiler", "start").Output);
        Assert.Equal("bridge 0.7.0 is too old for ug perf counters: run ug bridge install again\n", Run("perf", "counters").Output);
        Assert.Equal("bridge 0.7.0 is too old for ug perf snapshot take: run ug bridge install again\n", Run("perf", "snapshot", "take").Output);
        Assert.Equal("bridge 0.7.0 is too old for ug perf frame-debugger enable: run ug bridge install again\n", Run("perf", "frame-debugger", "enable").Output);
        Assert.Equal("edit mode\n", Run("perf").Output);
        Assert.Equal("/stats", _bridge.Requests.Last());
    }

    [Fact]
    public void Snapshot_list_and_compare_run_here_without_the_bridge()
    {
        Assert.Equal("no snapshots in Library/ungrep/snapshots/ or MemoryCaptures/: ug perf snapshot take\n", Run("perf", "snapshot", "list").Output);
        Assert.Equal("no folder Captures\n", Run("perf", "snapshot", "list", "--folder", "Captures").Output);
        Assert.Equal("no snapshot before (ug perf snapshot list)\n", Run("perf", "snapshot", "compare", "before", "after").Output);
        Assert.Equal("--top is 1 to 200\n", Run("perf", "snapshot", "compare", "before", "after", "--top", "0").Output);

        Directory.CreateDirectory(Path.Combine(_root, "Library", "ungrep", "snapshots"));
        File.WriteAllText(Path.Combine(_root, "Library", "ungrep", "snapshots", "before.snap"), "not one");
        File.WriteAllText(Path.Combine(_root, "Library", "ungrep", "snapshots", "after.snap"), "not two");

        Assert.Equal("cannot read the snapshots: not a memory snapshot (no snapshot signature at the start)\n", Run("perf", "snapshot", "compare", "before", "after").Output);
        Assert.Empty(_bridge.Requests);
    }

    [Fact]
    public void The_commands_obey_the_module_switch()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".claude"));
        File.WriteAllText(Path.Combine(_root, ".claude", "ungrep.json"), "{\"modules\":{\"editor-link\":false}}");

        Assert.Equal("editor-link is off in .claude/ungrep.json\n", Run("tree").Output);
        Assert.Equal("editor-link is off in .claude/ungrep.json\n", Run("find", "--live", "--tag", "Player").Output);
    }

    private static (int, string) Slow(int milliseconds, (int, string) answer)
    {
        Thread.Sleep(milliseconds);
        return answer;
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
