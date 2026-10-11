using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Ungrep.Core.Graph;

namespace Ungrep.Core.Tests;

/// <summary>
/// The generated purge-squad-sized graph, built once for <see cref="GraphPerformanceTests"/>: a
/// full index staged and published, the way a cold index runs.
/// </summary>
public sealed class PerformanceGraphFixture : IDisposable
{
    public PerformanceGraphFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "ug-perf-" + Guid.NewGuid().ToString("N"));
        Graph = SyntheticGraph.Create(Root);

        var clock = Stopwatch.StartNew();
        using (GraphStore staged = GraphStore.OpenStaging(Root))
        {
            using (BulkWriter bulk = staged.BeginBulk())
            {
                foreach (var (file, nodes, edges) in Graph.Files)
                {
                    bulk.Add(file, nodes, edges);
                }

                bulk.Commit();
            }

            BulkTime = clock.Elapsed;
            staged.Publish();
        }

        PublishTime = clock.Elapsed - BulkTime;
        DatabaseBytes = new FileInfo(Path.Combine(Root, GraphStore.RelativePath)).Length;
    }

    public string Root { get; }

    internal SyntheticGraph Graph { get; }

    public TimeSpan BulkTime { get; }

    public TimeSpan PublishTime { get; }

    public long DatabaseBytes { get; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(Root, recursive: true);
    }
}

/// <summary>
/// SPEC-graph-core, Performance targets, each checked on the median of repeated runs. Targets
/// hold in Release on the dev machine; Debug builds get a 2x margin and CI (`CI` set) 4x, since
/// shared runners' timings swing (Ubuntu once took 67 ms on a 25 ms line). They run alone, after the parallel tests, so other tests' disk and CPU use doesn't skew the timings.
/// </summary>
[Collection(nameof(GraphPerformanceTests))]
public sealed class GraphPerformanceTests(PerformanceGraphFixture fixture) : IClassFixture<PerformanceGraphFixture>
{
    private const int Runs = 21;

    private static double Margin =>
#if DEBUG
        2;
#else
        Environment.GetEnvironmentVariable("CI") is null ? 1 : 4;
#endif

    private static void Report(string what, TimeSpan measured, double targetMs)
    {
        TestContext.Current.TestOutputHelper?.WriteLine($"{what}: {measured.TotalMilliseconds:0.00} ms (target {targetMs} ms, margin {Margin}x)");
        Assert.True(measured.TotalMilliseconds <= targetMs * Margin, $"{what}: {measured.TotalMilliseconds:0.00} ms, target {targetMs} ms x{Margin}");
    }

    private static TimeSpan Median(Action run)
    {
        run();
        var times = new List<TimeSpan>(Runs);
        for (int i = 0; i < Runs; i++)
        {
            var clock = Stopwatch.StartNew();
            run();
            times.Add(clock.Elapsed);
        }

        times.Sort();
        return times[Runs / 2];
    }

    [Fact]
    public void Graph_is_purge_squad_sized()
    {
        TestContext.Current.TestOutputHelper?.WriteLine($"{fixture.Graph.Files.Count} files, {fixture.Graph.NodeCount} nodes, {fixture.Graph.EdgeCount} edges");
        Assert.InRange(fixture.Graph.Files.Count, 2_900, 3_100);
        Assert.InRange(fixture.Graph.NodeCount, 38_000, 42_000);
        Assert.InRange(fixture.Graph.EdgeCount, 190_000, 210_000);
    }

    [Fact]
    public void Bulk_insert_of_the_whole_graph()
    {
        TestContext.Current.TestOutputHelper?.WriteLine($"publish (backup copy into graph.db): {fixture.PublishTime.TotalMilliseconds:0} ms");
        Report("bulk insert", fixture.BulkTime, 5_000);
    }

    [Fact]
    public void Database_size()
    {
        double mb = fixture.DatabaseBytes / (1024.0 * 1024.0);
        TestContext.Current.TestOutputHelper?.WriteLine($"graph.db: {mb:0.0} MB");
        Assert.True(mb <= 50, $"graph.db is {mb:0.0} MB, target 50 MB");
    }

    [Fact]
    public void Open_read_only_and_get_one_node()
    {
        string key = fixture.Graph.Files[1234].Nodes[0].Key;
        Report("open read-only + GetNode", Median(() =>
        {
            using GraphStore store = GraphStore.Open(fixture.Root, StoreMode.ReadOnly);
            Assert.NotNull(store.GetNode(key));
        }), 30);
    }

    [Fact]
    public void Edges_from_and_to_with_100_results()
    {
        using GraphStore store = GraphStore.Open(fixture.Root, StoreMode.ReadOnly);
        string hub = fixture.Graph.HubType;
        Assert.True(store.EdgesTo(hub, EdgeKindSet.All, 100).More, "the hub should have more than 100 incoming edges");
        Report("EdgesTo, 100 results", Median(() => store.EdgesTo(hub, EdgeKindSet.All, 100)), 5);

        string type = fixture.Graph.Files[42].Nodes[0].Key;
        Assert.True(store.EdgesFrom(type, EdgeKindSet.All, 100).Items.Count >= 20);
        Report("EdgesFrom, one type", Median(() => store.EdgesFrom(type, EdgeKindSet.All, 100)), 5);
    }

    [Fact]
    public void Find_by_name_prefix_with_20_results()
    {
        using GraphStore store = GraphStore.Open(fixture.Root, StoreMode.ReadOnly);
        Assert.Equal(20, store.FindByName("Type12", NodeKindSet.All, 20).Items.Count);
        Report("FindByName prefix, 20 results", Median(() => store.FindByName("Type12", NodeKindSet.All, 20)), 10);
        // Not a spec target: a short prefix that matches 15,600 nodes (e.g. `ug find Get`).
        Report("FindByName prefix matching every method", Median(() => store.FindByName("Meth", NodeKindSet.All, 20)), 25);
    }

    [Fact]
    public void Replace_one_large_file()
    {
        using GraphStore store = GraphStore.Open(fixture.Root, StoreMode.ReadWrite, TimeSpan.FromSeconds(30));
        const string path = "Assets/Scripts/Big/BigType.cs";
        int version = 0;
        var (file, nodes, edges) = fixture.Graph.LargeFile(path, 0);
        TestContext.Current.TestOutputHelper?.WriteLine($"{nodes.Count} nodes, {edges.Count} edges");
        try
        {
            Report("ReplaceFile, one large C# file", Median(() =>
            {
                var (f, n, e) = fixture.Graph.LargeFile(path, ++version);
                store.ReplaceFile(f, n, e);
            }), 50);
        }
        finally
        {
            store.RemoveFile(path);
        }
    }

    [Fact]
    public void Freshness_with_nothing_changed()
    {
        using GraphStore store = GraphStore.Open(fixture.Root, StoreMode.ReadOnly);
        List<FileStat> stats = fixture.Graph.Files.Select(f => FileStat.Of(fixture.Root, f.File.Path)!).ToList();
        Assert.True(store.CheckFreshness(stats).IsFresh);
        Report("CheckFreshness, 3,000 files", Median(() => store.CheckFreshness(stats)), 100);

        var clock = Stopwatch.StartNew();
        foreach (var (file, _, _) in fixture.Graph.Files)
        {
            FileStat.Of(fixture.Root, file.Path);
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"stat of 3,000 files (caller's side, not in the target): {clock.Elapsed.TotalMilliseconds:0} ms");
    }
}

/// <summary>Runs <see cref="GraphPerformanceTests"/> with nothing else in parallel.</summary>
[CollectionDefinition(nameof(GraphPerformanceTests), DisableParallelization = true)]
public sealed class GraphPerformanceCollection;
