using System.Text.RegularExpressions;
using UnityGraph.EditorLink.Tests;

namespace UnityGraph.Cli.Tests;

/// <summary>
/// `ug status/compile/run/bridge` against a fake bridge, a recorded Editor.log and a bare
/// Unity project folder: what Claude reads for each answer and each way the editor is out of reach.
/// </summary>
[Collection(nameof(EditorCommandTests))]
public sealed partial class EditorCommandTests : IDisposable
{
    private const string OneError = "{\"compile\":4,\"started\":\"2026-09-29T14:00:00Z\",\"finished\":\"2026-09-29T14:00:04Z\",\"messages\":["
        + "{\"assembly\":\"Assembly-CSharp\",\"file\":\"Assets/Scripts/Turntable.cs\",\"line\":17,\"column\":9,\"severity\":\"error\",\"code\":\"CS0103\",\"text\":\"The name 'spd' does not exist in the current context\"}]}";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-editor-cli-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeBridge _bridge = new();

    public EditorCommandTests()
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

    [Fact]
    public void Status_shows_the_editor_and_its_last_compile()
    {
        _bridge.Publish(_root);
        _bridge.Handle = path => path == "/compile" ? (200, OneError) : (200, FakeBridge.Status("idle", 4, errors: 1));

        var (exitCode, output) = Run("status");

        Assert.Equal(0, exitCode);
        Assert.Equal("Unity 6000.3.14f1  idle  bridge 0.1.0\n  last compile  1 error  <hh:mm:ss> (4.0 s)\n", output);
    }

    [Fact]
    public void Status_before_any_compile_says_so()
    {
        _bridge.Publish(_root);
        _bridge.Handle = _ => (200, FakeBridge.Status("compiling", 0));

        Assert.Equal("Unity 6000.3.14f1  compiling  bridge 0.1.0\n  last compile  none since the editor opened\n", Run("status").Output);
    }

    [Fact]
    public void Status_without_the_bridge_points_at_bridge_install()
    {
        Assert.Equal("""
            Unity not reachable (no bridge.json: Unity is closed or the bridge is not installed)
              the bridge is not in this project; `ug bridge install` shows how to add it

            """.ReplaceLineEndings("\n"), Run("status").Output);
    }

    [Fact]
    public void Status_with_an_installed_bridge_and_a_dead_editor_names_the_pid()
    {
        _bridge.Publish(_root, pid: 4242);
        EditorCommands.ProcessCheck = _ => false;

        Assert.Equal("Unity not reachable (stale bridge.json: Unity (pid 4242) is not running)\n", Run("status").Output);
    }

    [Fact]
    public void Compile_with_no_script_changes_repeats_the_last_result()
    {
        _bridge.Publish(_root);
        _bridge.Handle = path => path.Split('?')[0] switch
        {
            "/refresh" => (200, "{\"compiling\":false,\"compile\":4}"),
            "/compile" => (200, OneError),
            _ => (200, FakeBridge.Status("idle", 4, errors: 1)),
        };

        Assert.Equal(
            "no script changes: 1 error\n  Assets/Scripts/Turntable.cs:17:9  CS0103  The name 'spd' does not exist in the current context\n",
            Run("compile").Output);
    }

    [Fact]
    public void Compile_without_the_bridge_reads_this_projects_Editor_log()
    {
        string log = Path.Combine(_root, "Editor.log");
        File.WriteAllText(log, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "logs", "unity6-failed.log"))
            .Replace("D:/Work/BridgeHost", _root.Replace('\\', '/'), StringComparison.Ordinal));

        var (exitCode, output) = Run(["compile"], editorLog: log);

        Assert.Equal(0, exitCode);
        Assert.Equal("""
            Unity not reachable (no bridge.json: Unity is closed or the bridge is not installed); from Editor.log of <time>: 1 error
              Assets/Editor/Broken.cs:1:78  CS0103  The name 'spd' does not exist in the current context

            """.ReplaceLineEndings("\n"), output);
    }

    [Fact]
    public void Compile_without_the_bridge_or_a_log_says_there_is_nothing_to_read()
    {
        Assert.Equal(
            "Unity not reachable (no bridge.json: Unity is closed or the bridge is not installed); no Editor.log or compile.json to read\n",
            Run(["compile"], editorLog: Path.Combine(_root, "missing.log")).Output);
    }

    [Fact]
    public void Run_reports_the_result()
    {
        _bridge.Publish(_root);
        _bridge.Handle = path => path.StartsWith("/run", StringComparison.Ordinal)
            ? (200, "{\"ran\":true,\"result\":\"hello\\nworld\",\"error\":null,\"frame\":null,\"ms\":3.4}")
            : (200, FakeBridge.Status("idle", 1));

        Assert.Equal("ran Tools.Hello in 3 ms\n  result: hello\n    world\n", Run("run", "Tools.Hello").Output);
        Assert.Contains("/run?target=Tools.Hello", _bridge.Requests);
    }

    [Fact]
    public void Run_reports_an_exception_with_its_frame()
    {
        _bridge.Publish(_root);
        _bridge.Handle = path => path.StartsWith("/run", StringComparison.Ordinal)
            ? (200, "{\"ran\":true,\"result\":null,\"error\":\"System.InvalidOperationException: boom\",\"frame\":\"Assets/Editor/Tools.cs:12\",\"ms\":4.2}")
            : (200, FakeBridge.Status("idle", 1));

        Assert.Equal(
            "Tools.Boom threw after 4 ms: System.InvalidOperationException: boom\n  at Assets/Editor/Tools.cs:12\n",
            Run("run", "Tools.Boom").Output);
    }

    [Fact]
    public void Run_reports_a_target_the_bridge_refused()
    {
        _bridge.Publish(_root);
        _bridge.Handle = path => path.StartsWith("/run", StringComparison.Ordinal)
            ? (200, "{\"ran\":false,\"result\":null,\"error\":\"no static method Tools.Nope without parameters\",\"frame\":null,\"ms\":0}")
            : (200, FakeBridge.Status("idle", 1));

        Assert.Equal("did not run Tools.Nope: no static method Tools.Nope without parameters\n", Run("run", "Tools.Nope").Output);
    }

    [Fact]
    public void Run_without_the_bridge_says_it_needs_one()
    {
        Assert.Equal("""
            Unity not reachable (no bridge.json: Unity is closed or the bridge is not installed); ug run needs the bridge
              the bridge is not in this project; `ug bridge install` shows how to add it

            """.ReplaceLineEndings("\n"), Run("run", "Tools.Hello").Output);
    }

    [Fact]
    public void Bridge_install_prints_how_and_writes_nothing()
    {
        var (exitCode, output) = Run("bridge", "install");

        Assert.Equal(0, exitCode);
        Assert.Equal("""
            The bridge is a Unity package in the plugin: <package>/package.json
            Add it one of two ways; both write into the project:
              1. In Unity: Window > Package Manager > + > Install package from disk..., and pick that file.
                 Unity records it in Packages/manifest.json; ug never edits that file.
              2. ug bridge install --copy: copies it to Assets/Plugins/UnityGraphBridge/ (vendored, so not indexed).
            Once Unity has compiled it, ug status shows the editor.

            """.ReplaceLineEndings("\n"), output);
        Assert.False(Directory.Exists(Path.Combine(_root, "Assets", "Plugins")));
    }

    [Fact]
    public void Bridge_install_copy_leaves_out_the_package_tests_and_replaces_an_old_copy()
    {
        string folder = Path.Combine(_root, "Assets", "Plugins", "UnityGraphBridge");

        var (_, first) = Run("bridge", "install", "--copy");
        File.WriteAllText(Path.Combine(folder, "Stale.cs"), "");
        var (_, second) = Run("bridge", "install", "--copy");

        Assert.Matches(@"^copied the bridge \(\d+ files\) to Assets/Plugins/UnityGraphBridge/; Unity compiles it on its next refresh, then ug status shows the editor\n$", first);
        Assert.Equal(first, second);
        Assert.True(File.Exists(Path.Combine(folder, "package.json")));
        Assert.True(File.Exists(Path.Combine(folder, "Editor", "Bridge.cs")));
        Assert.False(Directory.Exists(Path.Combine(folder, "Tests")));
        Assert.False(File.Exists(Path.Combine(folder, "Tests.meta")));
        Assert.False(File.Exists(Path.Combine(folder, "Stale.cs")));
        Assert.StartsWith("the bridge is already in this project (Assets/Plugins/UnityGraphBridge/)\n", Run("bridge", "install").Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Bridge_install_copy_refuses_when_Package_Manager_has_it()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Packages"));
        File.WriteAllText(Path.Combine(_root, "Packages", "manifest.json"), "{\"dependencies\":{\"com.unitygraph.bridge\":\"file:../bridge\"}}");

        Assert.Equal(
            "the bridge is already added through Package Manager (Packages/manifest.json); a copy would clash with it\n",
            Run("bridge", "install", "--copy").Output);
        Assert.False(Directory.Exists(Path.Combine(_root, "Assets", "Plugins")));
        Assert.Equal("the bridge was added through Package Manager: remove it there (Window > Package Manager)\n", Run("bridge", "remove").Output);
    }

    [Fact]
    public void Bridge_remove_deletes_the_copy_and_its_meta()
    {
        Run("bridge", "install", "--copy");
        File.WriteAllText(Path.Combine(_root, "Assets", "Plugins", "UnityGraphBridge.meta"), "");

        Assert.Equal("removed Assets/Plugins/UnityGraphBridge/\n", Run("bridge", "remove").Output);
        Assert.False(Directory.Exists(Path.Combine(_root, "Assets", "Plugins", "UnityGraphBridge")));
        Assert.False(File.Exists(Path.Combine(_root, "Assets", "Plugins", "UnityGraphBridge.meta")));
        Assert.Equal("no bridge copy in Assets/Plugins/UnityGraphBridge/\n", Run("bridge", "remove").Output);
    }

    [Fact]
    public void The_commands_obey_the_module_switch()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".claude"));
        File.WriteAllText(Path.Combine(_root, ".claude", "unitygraph.json"), "{ \"modules\": { \"editor-link\": false } }");

        Assert.Equal("editor-link is off in .claude/unitygraph.json\n", Run("status").Output);
        Assert.Equal("editor-link is off in .claude/unitygraph.json\n", Run("bridge", "install", "--copy").Output);
        Assert.False(Directory.Exists(Path.Combine(_root, "Assets", "Plugins")));
    }

    [Fact]
    public void Outside_a_Unity_project_the_commands_fail()
    {
        string empty = _root + "-empty";
        Directory.CreateDirectory(empty);
        try
        {
            var output = new StringWriter();
            int exitCode = CliApp.Run(["status", "--project", empty], output, new StringWriter());

            Assert.Equal(1, exitCode);
            Assert.StartsWith("no Unity project at or above ", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(empty);
        }
    }

    private (int ExitCode, string Output) Run(params string[] args) => Run(args, editorLog: null);

    private (int ExitCode, string Output) Run(string[] args, string? editorLog)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exitCode = CliApp.Run([.. args, "--project", _root], output, error, editorLog: editorLog ?? Path.Combine(_root, "no-editor.log"));
        Assert.Equal("", error.ToString());
        string text = output.ToString().ReplaceLineEndings("\n");
        text = Clock().Replace(text, "  <hh:mm:ss> (");
        text = Stamp().Replace(text, "<time>");
        string? package = EditorCommands.FindPackage();
        return (exitCode, package == null ? text : text.Replace(package + Path.DirectorySeparatorChar, "<package>/", StringComparison.Ordinal));
    }

    [GeneratedRegex(@"  \d\d:\d\d:\d\d \(")]
    private static partial Regex Clock();

    [GeneratedRegex(@"\d{4}-\d\d-\d\d \d\d:\d\d:\d\d")]
    private static partial Regex Stamp();
}

[CollectionDefinition(nameof(EditorCommandTests), DisableParallelization = true)]
public sealed class EditorCommandCollection;
