namespace UnityGraph.Code.Tests;

/// <summary>`ug eval`'s fallback references on a fake project and editor folder (SPEC-editor-link, Running code).</summary>
public sealed class SnippetReferencesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-refs-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _managed = typeof(SnippetCompiler).Assembly.Location;

    [Fact]
    public void Fallback_takes_script_assemblies_then_the_editors_managed_folders_one_per_name()
    {
        string project = Path.Combine(_root, "Game");
        string data = Path.Combine(_root, "Editor", "Data");
        Copy("Game/Library/ScriptAssemblies/Assembly-CSharp.dll");
        Write("Game/Library/ScriptAssemblies/lib_burst_generated.dll", "not a managed file");
        Copy("Editor/Data/Managed/UnityEngine/UnityEngine.dll");
        Copy("Editor/Data/Managed/UnityEngine.dll");
        Copy("Editor/Data/Managed/UnityEditor.Graphs.dll");
        Copy("Editor/Data/MonoBleedingEdge/lib/mono/unityjit-win32/mscorlib.dll");
        Copy("Editor/Data/MonoBleedingEdge/lib/mono/unityjit-win32/Facades/netstandard.dll");
        Copy("Editor/Data/MonoBleedingEdge/lib/mono/4.5/mscorlib.dll");

        IEnumerable<string> found = SnippetReferences.Fallback(project, data).Select(p => Path.GetRelativePath(_root, p).Replace('\\', '/'));

        Assert.Equal(
            [
                "Game/Library/ScriptAssemblies/Assembly-CSharp.dll",
                "Editor/Data/Managed/UnityEngine/UnityEngine.dll",
                "Editor/Data/Managed/UnityEditor.Graphs.dll",
                "Editor/Data/MonoBleedingEdge/lib/mono/unityjit-win32/mscorlib.dll",
                "Editor/Data/MonoBleedingEdge/lib/mono/unityjit-win32/Facades/netstandard.dll",
            ],
            found);
        Assert.Equal(["Game/Library/ScriptAssemblies/Assembly-CSharp.dll"], SnippetReferences.Fallback(project, null).Select(p => Path.GetRelativePath(_root, p).Replace('\\', '/')));
    }

    [Fact]
    public void The_editor_is_the_running_one_else_the_projects_version_under_a_hub_folder()
    {
        string project = Path.Combine(_root, "Game");
        Write("Game/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.3.14f1\nm_EditorVersionWithRevision: 6000.3.14f1 (abc)\n");
        Directory.CreateDirectory(Path.Combine(_root, "Running", "Editor", "Data", "Managed"));
        Directory.CreateDirectory(Path.Combine(_root, "Hub", "6000.3.14f1", "Editor", "Data", "Managed"));
        Directory.CreateDirectory(Path.Combine(_root, "Mac", "6000.3.14f1", "Unity.app", "Contents", "Managed"));
        string[] hubs = [Path.Combine(_root, "Missing"), Path.Combine(_root, "Hub")];

        Assert.Equal(Path.Combine(_root, "Running", "Editor", "Data"), SnippetReferences.EditorData(project, Path.Combine(_root, "Running", "Editor", "Unity.exe"), hubs));
        Assert.Equal(Path.Combine(_root, "Hub", "6000.3.14f1", "Editor", "Data"), SnippetReferences.EditorData(project, Path.Combine(_root, "Gone", "Unity.exe"), hubs));
        Assert.Equal(Path.Combine(_root, "Hub", "6000.3.14f1", "Editor", "Data"), SnippetReferences.EditorData(project, null, hubs));
        Assert.Equal(Path.Combine(_root, "Mac", "6000.3.14f1", "Unity.app", "Contents"), SnippetReferences.EditorData(project, null, [Path.Combine(_root, "Mac")]));
        Assert.Equal(
            Path.Combine(_root, "Mac", "6000.3.14f1", "Unity.app", "Contents"),
            SnippetReferences.EditorData(project, Path.Combine(_root, "Mac", "6000.3.14f1", "Unity.app", "Contents", "MacOS", "Unity"), []));
        Assert.Null(SnippetReferences.EditorData(Path.Combine(_root, "NoProject"), null, hubs));
        Assert.Equal("6000.3.14f1", SnippetReferences.UnityVersion(project));
    }

    [Fact]
    public void Hubs_install_folder_is_a_json_string()
    {
        Write("hub/secondaryInstallPath.json", "\"D:\\\\Program Files\\\\Unity Editors\"");
        Write("hub/empty.json", "\"\"");
        Write("hub/broken.json", "{");

        Assert.Equal(@"D:\Program Files\Unity Editors", SnippetReferences.SecondaryInstallPath(Path.Combine(_root, "hub", "secondaryInstallPath.json")));
        Assert.Null(SnippetReferences.SecondaryInstallPath(Path.Combine(_root, "hub", "empty.json")));
        Assert.Null(SnippetReferences.SecondaryInstallPath(Path.Combine(_root, "hub", "broken.json")));
        Assert.Null(SnippetReferences.SecondaryInstallPath(Path.Combine(_root, "hub", "missing.json")));
        Assert.NotEmpty(SnippetReferences.HubFolders());
    }

    [Fact]
    public void Defines_come_from_the_editor_csproj_else_UNITY_EDITOR()
    {
        string project = Path.Combine(_root, "Game");
        Directory.CreateDirectory(project);
        Assert.Equal(["UNITY_EDITOR"], SnippetReferences.Defines(project));

        Write("Game/Assembly-CSharp-Editor.csproj", "<Project><PropertyGroup><DefineConstants>UNITY_6000_3; UNITY_EDITOR;ODIN_INSPECTOR;</DefineConstants></PropertyGroup></Project>");
        Assert.Equal(["UNITY_6000_3", "UNITY_EDITOR", "ODIN_INSPECTOR"], SnippetReferences.Defines(project));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is harmless.
        }
    }

    private void Copy(string relative)
    {
        string target = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(_managed, target);
    }

    private void Write(string relative, string text)
    {
        string target = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, text);
    }
}
