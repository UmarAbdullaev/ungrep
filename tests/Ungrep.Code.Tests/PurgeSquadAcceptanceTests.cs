using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Data.Sqlite;
using Ungrep.Core;
using Ungrep.Core.Graph;

namespace Ungrep.Code.Tests;

/// <summary>
/// SPEC-code-index purge-squad acceptance. Runs when `UG_PURGE_SQUAD` names the project; reads
/// it only, and keeps the graph in a temp folder. Prints the numbers for the spec's Measured section.
/// </summary>
public sealed class PurgeSquadAcceptanceTests : IDisposable
{
    private readonly string _graph = Path.Combine(Path.GetTempPath(), "ug-ps-graph-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_graph))
        {
            Directory.Delete(_graph, recursive: true);
        }
    }

    [Fact]
    public void Indexes_purge_squad_within_the_targets()
    {
        string? root = Environment.GetEnvironmentVariable("UG_PURGE_SQUAD");
        if (root is null)
        {
            Assert.Skip("UG_PURGE_SQUAD not set");
        }

        var clock = Stopwatch.StartNew();
        CodeWorkspace workspace = CodeWorkspace.Load(root, UngrepConfig.Load(root).Vendored);
        IndexSummary summary;
        using (GraphStore store = GraphStore.Open(root, StoreMode.ReadWrite, graphDirectory: _graph))
        {
            summary = CodeIndex.Update(workspace, store);
        }

        TimeSpan full = clock.Elapsed;
        Assert.Empty(summary.Problems);
        Assert.True(full < TimeSpan.FromSeconds(60), $"full index took {full}");

        using GraphStore read = GraphStore.Open(root, StoreMode.ReadOnly, graphDirectory: _graph);
        Assert.Equal("Assets/Scripts/Lab/LabStage.cs", read.GetNode(NodeKeys.Symbol("T:LabStage"))?.File);

        var output = TestContext.Current.TestOutputHelper!;
        output.WriteLine($"files {summary.Files} ({summary.Files - summary.Vendored} own, {summary.Vendored} vendored), nodes {summary.Nodes}, edges {summary.Edges}, full {full.TotalSeconds:0.0} s");
        output.WriteLine($"graph.db {new FileInfo(Path.Combine(_graph, "graph.db")).Length / 1024} KiB");
        output.WriteLine($"calls dropped by scope (deduped like edges): {DroppedCalls(workspace)}");
    }

    // Calls from own code into symbols with no node (engine, BCL, packages), counted per
    // (file, caller, target) as the calls edges are.
    private static int DroppedCalls(CodeWorkspace workspace)
    {
        var dropped = new HashSet<(string, ISymbol?, ISymbol)>();
        foreach (string path in workspace.TrackedFiles.Where(p => !workspace.IsVendored(p)))
        {
            if (workspace.ModelOf(path) is not { } model)
            {
                continue;
            }

            foreach (SyntaxNode node in model.SyntaxTree.GetRoot().DescendantNodes())
            {
                if (node is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax
                    && model.GetSymbolInfo(node).Symbol is IMethodSymbol target
                    && !workspace.IsIndexedSource(target.OriginalDefinition))
                {
                    dropped.Add((path, model.GetEnclosingSymbol(node.SpanStart), target.OriginalDefinition));
                }
            }
        }

        return dropped.Count;
    }
}
