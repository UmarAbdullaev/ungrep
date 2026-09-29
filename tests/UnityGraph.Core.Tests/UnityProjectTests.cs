namespace UnityGraph.Core.Tests;

/// <summary>
/// Finding a Unity project on disk and reading its editor version and serialization mode.
/// </summary>
public sealed class UnityProjectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-unity-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void MakeProject(string version, string? serializationMode)
    {
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "Scripts"));
        Directory.CreateDirectory(Path.Combine(_root, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_root, "ProjectSettings", "ProjectVersion.txt"),
            $"m_EditorVersion: {version}\nm_EditorVersionWithRevision: {version} (abc123)\n");
        if (serializationMode is not null)
        {
            File.WriteAllText(Path.Combine(_root, "ProjectSettings", "EditorSettings.asset"),
                $"%YAML 1.1\n--- !u!159 &1\nEditorSettings:\n  m_ObjectHideFlags: 0\n  m_SerializationMode: {serializationMode}\n");
        }
    }

    [Fact]
    public void Finds_the_project_from_a_folder_inside_it()
    {
        MakeProject("6000.3.14f1", "2");

        UnityProject? project = UnityProject.Find(Path.Combine(_root, "Assets", "Scripts"));

        Assert.NotNull(project);
        Assert.Equal(Path.GetFullPath(_root), project.Root);
        Assert.Equal("6000.3.14f1", project.EditorVersion);
        Assert.Equal(AssetSerialization.ForceText, project.Serialization);
        Assert.True(project.IsSupportedVersion);
        Assert.Equal(Path.Combine(project.Root, "Library", "unitygraph", "graph.db"), project.GraphPath);
    }

    [Fact]
    public void Needs_both_assets_and_project_version()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Assets"));

        Assert.Null(UnityProject.Find(_root));
    }

    [Fact]
    public void Serialization_is_unknown_without_editor_settings()
    {
        MakeProject("2022.3.62f1", null);

        Assert.Null(UnityProject.Find(_root)!.Serialization);
    }

    [Theory]
    [InlineData("0", AssetSerialization.Mixed)]
    [InlineData("1", AssetSerialization.ForceBinary)]
    [InlineData("2", AssetSerialization.ForceText)]
    [InlineData("7", null)]
    public void Parses_serialization_mode(string mode, AssetSerialization? expected)
    {
        Assert.Equal(expected, UnityProject.ParseSerialization($"EditorSettings:\n  m_SerializationMode: {mode}\n"));
    }

    [Theory]
    [InlineData("2022.3.62f1", true)]
    [InlineData("6000.0.23f1", true)]
    [InlineData("2022.2.21f1", false)]
    [InlineData("2021.3.45f1", false)]
    [InlineData(null, false)]
    [InlineData("garbage", false)]
    public void Supports_2022_3_and_newer(string? version, bool supported)
    {
        Assert.Equal(supported, new UnityProject("/p", version, AssetSerialization.ForceText).IsSupportedVersion);
    }

    [Fact]
    public void Editor_version_is_null_when_missing()
    {
        Assert.Null(UnityProject.ParseEditorVersion("something else\n"));
    }
}
