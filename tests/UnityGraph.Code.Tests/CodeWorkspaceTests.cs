namespace UnityGraph.Code.Tests;

public sealed class CodeWorkspaceTests : IDisposable
{
    private readonly CodeFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Tracks_indexable_files_of_every_project()
    {
        CodeWorkspace workspace = _fixture.Load();

        Assert.Equal(
        [
            "Assets/3rd Party/Tweens/Tween.cs",
            "Assets/Editor/LabTools.cs",
            "Assets/Scripts/Broken.cs",
            "Assets/Scripts/Core/Pool.cs",
            "Assets/Scripts/Core/Stage.cs",
            "Assets/Scripts/Lab/LabStage.Debug.cs",
            "Assets/Scripts/Lab/LabStage.cs",
            "Assets/Scripts/Lab/Owner's Notes.cs",
            "Assets/Scripts/Lab/Turntable.cs",
        ], workspace.TrackedFiles);
        Assert.True(workspace.HasProjects);
        Assert.Equal("Assembly-CSharp-Editor.csproj", workspace.ProjectOf("Assets/Editor/LabTools.cs"));
        Assert.Null(workspace.ProjectOf("Library/Stubs/UnityEngine.cs"));
    }

    [Fact]
    public void Loads_with_no_errors_in_own_files_but_the_broken_one()
    {
        CodeWorkspace workspace = _fixture.Load();

        var errors = workspace.SourceErrors().Select(e => $"{e.Location.SourceTree!.FilePath} {e.Id}").ToList();

        Assert.Equal(["Assets/Scripts/Broken.cs CS1061"], errors);
    }

    [Fact]
    public void Reports_missing_references()
    {
        CodeWorkspace workspace = _fixture.Load();
        workspace.IndexMany();

        Assert.Contains("missing reference Library/ScriptAssemblies/Missing.dll", workspace.Problems);
        Assert.Contains("missing reference Library/Bcl/mscorlib.dll", workspace.Problems);
        Assert.Contains(workspace.Problems, p => p.StartsWith("no BCL reference found", StringComparison.Ordinal));
    }

    [Fact]
    public void Files_not_in_a_project_yet_go_to_the_project_with_the_closest_folder()
    {
        _fixture.Write("Assets/Scripts/Core/Extra.cs", "namespace Game.Core { public class Extra { } }");
        _fixture.Write("Assets/Scripts/Lab/More/Extra.cs", "namespace Game.Lab { public class MoreExtra { } }");
        _fixture.Write("Assets/Elsewhere/Extra.cs", "public class Elsewhere { }");
        _fixture.Write("Assets/Samples~/Hidden.cs", "public class Hidden { }");
        _fixture.Write("Packages/com.game.tools/Runtime/Tool.cs", "public class Tool { }");

        CodeWorkspace workspace = _fixture.Load();

        Assert.Equal("Game.Core.csproj", workspace.ProjectOf("Assets/Scripts/Core/Extra.cs"));
        Assert.Equal("Assembly-CSharp.csproj", workspace.ProjectOf("Assets/Scripts/Lab/More/Extra.cs"));
        Assert.Equal("Assembly-CSharp.csproj", workspace.ProjectOf("Assets/Elsewhere/Extra.cs"));
        Assert.Equal("Assembly-CSharp.csproj", workspace.ProjectOf("Packages/com.game.tools/Runtime/Tool.cs"));
        Assert.Null(workspace.ProjectOf("Assets/Samples~/Hidden.cs"));
        Assert.Single(workspace.Index("Assets/Scripts/Core/Extra.cs").Nodes);
    }

    [Fact]
    public void Without_projects_nothing_is_tracked()
    {
        foreach (string project in Directory.EnumerateFiles(_fixture.Root, "*.csproj"))
        {
            File.Delete(project);
        }

        CodeWorkspace workspace = _fixture.Load();

        Assert.False(workspace.HasProjects);
        Assert.Empty(workspace.TrackedFiles);
    }

    [Fact]
    public void An_unreadable_project_is_a_problem_not_a_failure()
    {
        _fixture.Write("Broken.csproj", "<Project><oops></Project>");

        CodeWorkspace workspace = _fixture.Load();

        Assert.Contains(workspace.Problems, p => p.StartsWith("Broken.csproj: not readable", StringComparison.Ordinal));
        Assert.NotEmpty(workspace.TrackedFiles);
    }

    [Fact]
    public void A_reference_cycle_is_dropped_and_reported()
    {
        string path = Path.Combine(_fixture.Root, "UnityEngine.csproj");
        File.WriteAllText(path, File.ReadAllText(path).Replace(
            "</Project>",
            "<ItemGroup><ProjectReference Include=\"Game.Core.csproj\" /></ItemGroup></Project>",
            StringComparison.Ordinal));

        CodeWorkspace workspace = _fixture.Load();
        workspace.Index("Assets/Scripts/Core/Stage.cs");

        Assert.Contains(workspace.Problems, p => p.Contains("reference cycle", StringComparison.Ordinal));
    }

    [Fact]
    public void Untracked_files_are_rejected()
    {
        CodeWorkspace workspace = _fixture.Load();

        Assert.Throws<ArgumentException>(() => workspace.Index("Assets/Nope.cs"));
        Assert.Throws<ArgumentException>(() => workspace.IndexMany(["Assets/Nope.cs"]));
    }

    [Theory]
    [InlineData("fileFormatVersion: 2\nguid: 5a1b2c3d4e5f60718293a4b5c6d7e8f9\n", "5a1b2c3d4e5f60718293a4b5c6d7e8f9")]
    [InlineData("guid: 5A1B2C3D4E5F60718293A4B5C6D7E8F9\n", null)]
    [InlineData("guid: short\n", null)]
    [InlineData("fileFormatVersion: 2\n", null)]
    public void Reads_meta_guids(string meta, string? guid) => Assert.Equal(guid, CodeWorkspace.MetaGuid(meta));
}
