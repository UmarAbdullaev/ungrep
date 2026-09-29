using UnityGraph.Core.Graph;

namespace UnityGraph.Query.Tests;

/// <summary>SPEC-query, Resolving `&lt;symbol&gt;`.</summary>
public sealed class SymbolResolverTests(QueryFixture fixture) : IClassFixture<QueryFixture>
{
    [Theory]
    [InlineData("cs:T:Game.Lab.LabStage", "cs:T:Game.Lab.LabStage")]
    [InlineData("LabStage", "cs:T:Game.Lab.LabStage")]
    [InlineData("Lab.LabStage", "cs:T:Game.Lab.LabStage")]
    [InlineData("Game.Lab.LabStage", "cs:T:Game.Lab.LabStage")]
    [InlineData("labstage", "cs:T:Game.Lab.LabStage")]
    [InlineData("LabStage.Open", "cs:M:Game.Lab.LabStage.Open(System.Int32)")]
    [InlineData("LabStage.Open(int)", "cs:M:Game.Lab.LabStage.Open(System.Int32)")]
    [InlineData("LabStage.Open( int )", "cs:M:Game.Lab.LabStage.Open(System.Int32)")]
    [InlineData("Pool", "cs:T:Game.Core.Pool`1")]
    [InlineData("Pool.Release", "cs:M:Game.Core.Pool`1.Release(`0)")]
    [InlineData("Turntable.Tint", "cs:P:Game.Lab.Turntable.Tint")]
    [InlineData("  Turntable.Rotated ", "cs:E:Game.Lab.Turntable.Rotated")]
    public void Resolves_one_symbol(string text, string key)
    {
        using GraphStore store = fixture.Open();

        Resolution resolution = SymbolResolver.Resolve(store, text);

        Assert.True(resolution.Found);
        Assert.Equal([key], resolution.Nodes.Select(n => n.Key));
    }

    [Theory]
    [InlineData("cs:T:Game.Lab.Gone")]
    [InlineData("abStage")]
    [InlineData("Lab.Stage.Open")]
    [InlineData("LabStage.Open(float)")]
    [InlineData("LabStage.")]
    [InlineData("")]
    public void Finds_nothing(string text)
    {
        using GraphStore store = fixture.Open();

        Resolution resolution = SymbolResolver.Resolve(store, text);

        Assert.False(resolution.Found);
        Assert.False(resolution.Ambiguous);
        Assert.Empty(resolution.Nodes);
    }

    [Fact]
    public void Two_symbols_of_one_name_are_ambiguous()
    {
        using GraphStore store = fixture.Open();

        Resolution resolution = SymbolResolver.Resolve(store, "Trace");

        Assert.True(resolution.Ambiguous);
        Assert.Equal(["cs:M:Game.Lab.ILabDebug.Trace(System.Int32)", "cs:M:Game.Lab.LabStage.Trace(System.Int32)"], resolution.Nodes.Select(n => n.Key));
    }
}
