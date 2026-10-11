namespace UnityGraph.Cli.Tests;

/// <summary>
/// `ug doctor` end to end on a small Unity project built in a temp folder. The project root is
/// replaced with &lt;root&gt; and separators with / so the golden output is the same on every machine.
/// </summary>
public sealed class DoctorCommandTests : IDisposable
{
    private const string Runtime = ".NET 8.0.0 (test)";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-doctor-" + Guid.NewGuid().ToString("N"));

    public DoctorCommandTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "Scripts"));
        Directory.CreateDirectory(Path.Combine(_root, "ProjectSettings"));
        WriteProjectFile("ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.3.14f1\n");
        WriteProjectFile("ProjectSettings/EditorSettings.asset", "EditorSettings:\n  m_SerializationMode: 2\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void WriteProjectFile(string relativePath, string content)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private (int ExitCode, string Output) Run(string project)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exitCode = CliApp.Run(["doctor", "--project", project], output, error, runtime: Runtime);
        Assert.Equal("", error.ToString());
        string text = output.ToString().ReplaceLineEndings("\n").Replace(Path.GetFullPath(_root), "<root>", StringComparison.Ordinal);
        return (exitCode, text.Replace('\\', '/'));
    }

    [Fact]
    public void Healthy_project_without_a_graph()
    {
        var (exitCode, output) = Run(Path.Combine(_root, "Assets", "Scripts"));

        Assert.Equal(0, exitCode);
        Golden.Match(output);
    }

    [Fact]
    public void Warnings_and_config_overrides()
    {
        WriteProjectFile("ProjectSettings/EditorSettings.asset", "EditorSettings:\n  m_SerializationMode: 0\n");
        WriteProjectFile(".claude/unitygraph.json", """{ "answerBudget": 800, "modules": { "viewer": false, "project-map": true } }""");
        WriteProjectFile("Library/unitygraph/graph.db", "");

        var (exitCode, output) = Run(_root);

        Assert.Equal(0, exitCode);
        Golden.Match(output);
    }

    [Fact]
    public void Old_editor_and_bad_config_fail()
    {
        WriteProjectFile("ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 2021.3.45f1\n");
        WriteProjectFile(".claude/unitygraph.json", """{ "modules": { "graph": false } }""");

        var (exitCode, output) = Run(_root);

        Assert.Equal(1, exitCode);
        Golden.Match(output);
    }

    [Fact]
    public void No_unity_project_fails()
    {
        string outside = Path.Combine(_root, "Assets", "Scripts");
        Directory.Delete(Path.Combine(_root, "ProjectSettings"), recursive: true);

        var (exitCode, output) = Run(outside);

        Assert.Equal(1, exitCode);
        Golden.Match(output);
    }

    [Fact]
    public void Bridge_copy_behind_the_plugin_warns()
    {
        string bundled = Path.Combine(_root, "plugin", "unity", "com.unitygraph.bridge");
        WriteProjectFile("plugin/unity/com.unitygraph.bridge/package.json", """{ "name": "com.unitygraph.bridge", "version": "0.16.0" }""");
        WriteProjectFile("Assets/Plugins/UnityGraphBridge/package.json", """{ "name": "com.unitygraph.bridge", "version": "0.15.0" }""");

        Assert.Equal(
            new DoctorCommand.Check(DoctorCommand.Status.Warn, "bridge", "Assets/Plugins/UnityGraphBridge/ is bridge 0.15.0, this unitygraph ships 0.16.0: run ug bridge install --copy again"),
            DoctorCommand.CheckBridge(_root, bundled));

        WriteProjectFile("plugin/unity/com.unitygraph.bridge/package.json", """{ "name": "com.unitygraph.bridge", "version": "0.15.0" }""");
        Assert.Equal(
            new DoctorCommand.Check(DoctorCommand.Status.Ok, "bridge", "Assets/Plugins/UnityGraphBridge/ (bridge 0.15.0)"),
            DoctorCommand.CheckBridge(_root, bundled));
    }

    [Fact]
    public void Bridge_link_into_the_plugin_cache_warns_and_a_dead_link_fails()
    {
        string cached = Path.Combine(_root, "home", ".claude", "plugins", "cache", "unitygraph", "unitygraph", "0.9.0", "unity", "com.unitygraph.bridge");
        WriteProjectFile(Path.Combine(Path.GetRelativePath(_root, cached), "package.json"), """{ "version": "0.15.0" }""");
        string link = "file:" + cached.Replace('\\', '/');
        WriteProjectFile("Packages/manifest.json", $$"""{ "dependencies": { "com.unitygraph.bridge": "{{link}}" } }""");

        DoctorCommand.Check inCache = DoctorCommand.CheckBridge(_root, bundled: null);
        Assert.Equal(DoctorCommand.Status.Warn, inCache.Status);
        Assert.Equal($"Packages/manifest.json links {link}, inside Claude Code's plugin cache, which the next unitygraph update deletes: remove it in Window > Package Manager, then run ug bridge install --copy", inCache.Detail);

        Directory.Delete(Path.Combine(_root, "home"), recursive: true);
        DoctorCommand.Check gone = DoctorCommand.CheckBridge(_root, bundled: null);
        Assert.Equal(DoctorCommand.Status.Fail, gone.Status);
        Assert.StartsWith($"Packages/manifest.json links {link}, which is gone", gone.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Bridge_linked_relative_to_Packages_or_by_version_is_fine()
    {
        WriteProjectFile("bridge/package.json", """{ "version": "0.15.0" }""");
        WriteProjectFile("Packages/manifest.json", """{ "dependencies": { "com.unitygraph.bridge": "file:../bridge" } }""");
        Assert.Equal(new DoctorCommand.Check(DoctorCommand.Status.Ok, "bridge", "Packages/manifest.json: file:../bridge"), DoctorCommand.CheckBridge(_root, bundled: null));

        WriteProjectFile("Packages/manifest.json", """{ "dependencies": { "com.unitygraph.bridge": "0.15.0" } }""");
        Assert.Equal(new DoctorCommand.Check(DoctorCommand.Status.Ok, "bridge", "Packages/manifest.json: 0.15.0"), DoctorCommand.CheckBridge(_root, bundled: null));
    }
}
