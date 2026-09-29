namespace UnityGraph.Code.Tests;

public sealed class CodeIndexGoldenTests : IDisposable
{
    private readonly CodeFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Fixture_indexes_to_the_golden_graph()
    {
        CodeWorkspace workspace = _fixture.Load();

        Golden.Match(CodeFixture.Render(workspace.IndexMany()));
    }

    [Fact]
    public void One_file_indexes_the_same_alone_as_in_a_full_run()
    {
        string full = CodeFixture.Render(_fixture.Load().IndexMany().Where(f => f.File.Path == "Assets/Scripts/Lab/LabStage.Debug.cs"));

        string alone = CodeFixture.Render([_fixture.Load().Index("Assets/Scripts/Lab/LabStage.Debug.cs")]);

        Assert.Equal(full, alone);
    }

    [Fact]
    public void Output_is_deterministic()
    {
        string first = CodeFixture.Render(_fixture.Load().IndexMany());
        string second = CodeFixture.Render(_fixture.Load().IndexMany());

        Assert.Equal(first, second);
    }
}
