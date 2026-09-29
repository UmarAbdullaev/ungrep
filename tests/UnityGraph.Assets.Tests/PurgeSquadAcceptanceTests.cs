using System.Diagnostics;
using Microsoft.Data.Sqlite;
using UnityGraph.Core;
using UnityGraph.Core.Graph;

namespace UnityGraph.Assets.Tests;

/// <summary>
/// SPEC-asset-index purge-squad acceptance. Runs when `UG_PURGE_SQUAD` names the project; reads
/// it only, and keeps the graph in a temp folder. Prints the numbers for the spec's Measured section.
/// </summary>
public sealed class PurgeSquadAcceptanceTests : IDisposable
{
    private readonly string _graph = Path.Combine(Path.GetTempPath(), "ug-ps-assets-" + Guid.NewGuid().ToString("N")[..8]);

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
        AssetWorkspace workspace = AssetWorkspace.Scan(root, UnityGraphConfig.Load(root).Vendored);
        TimeSpan scan = clock.Elapsed;
        IndexSummary summary;
        using (GraphStore store = GraphStore.Open(root, StoreMode.ReadWrite, graphDirectory: _graph))
        {
            using BulkWriter writer = store.BeginBulk();
            summary = AssetIndex.Full(workspace, writer);
            writer.Commit();
        }

        TimeSpan full = clock.Elapsed;
        var output = TestContext.Current.TestOutputHelper!;
        foreach (string problem in summary.Problems)
        {
            output.WriteLine("problem: " + problem);
        }

        Assert.True(full < TimeSpan.FromSeconds(20), $"full asset index took {full}");

        using GraphStore read = GraphStore.Open(root, StoreMode.ReadOnly, graphDirectory: _graph);
        string topBar = Assert.Single(read.KeysInFile("Assets/Prefabs/UI/TopBar.prefab.meta"));
        Page<EdgeHit> into = read.EdgesIntoAsset(topBar[2..], EdgeKindSet.Of(EdgeKind.InstanceOf), 1000);
        Assert.Contains(into.Items, e => e.Edge.File == "Assets/Scenes/Hub.unity" && e.Resolved);

        string content = Path.Combine(root, "Assets", "Scenes", "GalaxyMap.unity");
        clock.Restart();
        _ = workspace.IndexMany(["Assets/Scenes/GalaxyMap.unity"]);
        TimeSpan scene = clock.Elapsed;

        int metas = workspace.TrackedFiles.Count(f => f.Path.EndsWith(".meta", StringComparison.Ordinal));
        output.WriteLine($"files {summary.Files} ({metas} .meta, {summary.Files - metas} content; {summary.Vendored} vendored), nodes {summary.Nodes}, edges {summary.Edges}");
        output.WriteLine($"scan {scan.TotalSeconds:0.00} s, full {full.TotalSeconds:0.0} s, problems {summary.Problems.Count}");
        output.WriteLine($"GalaxyMap.unity {new FileInfo(content).Length / 1024} KiB indexed in {scene.TotalMilliseconds:0} ms");
        output.WriteLine($"graph.db {new FileInfo(Path.Combine(_graph, "graph.db")).Length / 1024} KiB");
    }
}
