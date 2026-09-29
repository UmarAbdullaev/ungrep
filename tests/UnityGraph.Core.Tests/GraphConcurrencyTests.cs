using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using UnityGraph.Core.Graph;

namespace UnityGraph.Core.Tests;

/// <summary>
/// SPEC-graph-core, Testing: concurrency. Readers during writes, one writer at a time, a killed
/// writer, dirty files, and a full index staged in `graph.db.new` then published.
/// </summary>
public sealed class GraphConcurrencyTests : IDisposable
{
    private const string HoldGraphVariable = "UG_TEST_HOLD_GRAPH";
    private const string Health = "Assets/Scripts/PlayerHealth.cs";
    private const string Damage = "Assets/Scripts/Damage.cs";

    private static readonly string _typeHealth = NodeKeys.Symbol("T:Game.PlayerHealth");
    private static readonly string _typeDamage = NodeKeys.Symbol("T:Game.Damage");
    private static readonly TimeSpan _short = TimeSpan.FromMilliseconds(50);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-conc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // A test run started with UG_TEST_HOLD_GRAPH=<root> is the killed writer: it opens the graph
    // read-write, says so, and waits to be killed.
#pragma warning disable CA2255 // Module initializer in a test assembly, used only for the child process.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void HoldGraphWhenAsked()
    {
        string? root = Environment.GetEnvironmentVariable(HoldGraphVariable);
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        GraphStore.Open(root, StoreMode.ReadWrite);
        Console.Out.WriteLine("held");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
    }

    private static FileRecord File(string path) => new(path, 1, 1, new string('0', 64), Indexer.Code);

    private static void WriteType(GraphStore store, string path, string key, string name, int members)
    {
        var nodes = new List<Node> { new(key, NodeKind.Type, name, path) };
        var edges = new List<Edge>();
        for (int i = 0; i < members; i++)
        {
            string member = NodeKeys.Symbol($"F:{name}.f{i}");
            nodes.Add(new Node(member, NodeKind.Field, "f" + i, path));
            edges.Add(new Edge(key, EdgeKind.Contains, member, Confidence.Exact, path));
        }

        store.ReplaceFile(File(path), nodes, edges);
    }

    private void Seed()
    {
        using GraphStore store = GraphStore.Open(_root, StoreMode.ReadWrite);
        WriteType(store, Health, _typeHealth, "PlayerHealth", 2);
    }

    [Fact]
    public void Two_writers_serialize()
    {
        using (GraphStore first = GraphStore.Open(_root, StoreMode.ReadWrite))
        {
            var e = Assert.Throws<GraphLockedException>(() => GraphStore.Open(_root, StoreMode.ReadWrite, _short));
            Assert.StartsWith("graph is locked by another writer (Library/unitygraph/write.lock", e.Message, StringComparison.Ordinal);
            Assert.Throws<GraphLockedException>(() => GraphStore.OpenStaging(_root, _short));
        }

        using GraphStore second = GraphStore.Open(_root, StoreMode.ReadWrite, _short);
        Assert.False(second.Created);
    }

    [Fact]
    public async Task A_waiting_writer_gets_the_lock_when_it_is_released()
    {
        GraphStore first = GraphStore.Open(_root, StoreMode.ReadWrite);
        Task release = Task.Run(async () =>
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            first.Dispose();
        }, TestContext.Current.CancellationToken);

        using GraphStore second = GraphStore.Open(_root, StoreMode.ReadWrite, TimeSpan.FromSeconds(10));
        await release;
    }

    [Fact]
    public void Readers_do_not_wait_for_a_writer()
    {
        Seed();
        using GraphStore writer = GraphStore.Open(_root, StoreMode.ReadWrite);
        using GraphStore reader = GraphStore.Open(_root, StoreMode.ReadOnly);

        WriteType(writer, Damage, _typeDamage, "Damage", 1);

        Assert.NotNull(reader.GetNode(_typeDamage));
    }

    [Fact]
    public async Task A_reader_during_replace_file_sees_old_or_new_never_partial()
    {
        Seed();
        using GraphStore writer = GraphStore.Open(_root, StoreMode.ReadWrite);
        using GraphStore reader = GraphStore.Open(_root, StoreMode.ReadOnly);
        Task writes = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                WriteType(writer, Health, _typeHealth, "PlayerHealth", i % 2 == 0 ? 5 : 2);
            }
        }, TestContext.Current.CancellationToken);

        var counts = new HashSet<int>();
        while (!writes.IsCompleted)
        {
            counts.Add(reader.EdgesFrom(_typeHealth, EdgeKindSet.All, 100).Items.Count);
        }

        await writes;
        Assert.Subset(new HashSet<int> { 2, 5 }, counts);
    }

    [Fact]
    public async Task A_killed_writer_releases_the_lock()
    {
        Directory.CreateDirectory(_root);
        string host = Environment.ProcessPath!;
        var start = new ProcessStartInfo(host) { RedirectStandardOutput = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(host) == "dotnet")
        {
            start.ArgumentList.Add(typeof(GraphConcurrencyTests).Assembly.Location);
        }

        start.Environment[HoldGraphVariable] = _root;
        using Process child = Process.Start(start)!;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            Assert.Equal("held", await child.StandardOutput.ReadLineAsync(timeout.Token));
            Assert.Throws<GraphLockedException>(() => GraphStore.Open(_root, StoreMode.ReadWrite, _short));
        }
        finally
        {
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(CancellationToken.None);
        }

        using GraphStore store = GraphStore.Open(_root, StoreMode.ReadWrite, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Dirty_files_are_marked_without_the_lock_and_taken_by_the_next_writer()
    {
        using (GraphStore busy = GraphStore.Open(_root, StoreMode.ReadWrite))
        {
            Assert.True(DirtyFiles.Mark(_root, "Assets/b.cs"));
            Assert.True(DirtyFiles.Mark(_root, "Assets/a.cs"));
            Assert.True(DirtyFiles.Mark(_root, "Assets/b.cs"));
        }

        using GraphStore next = GraphStore.Open(_root, StoreMode.ReadWrite);
        Assert.Equal(["Assets/a.cs", "Assets/b.cs"], next.TakeDirty());
        Assert.Empty(next.TakeDirty());
        Assert.Throws<ArgumentException>(() => DirtyFiles.Mark(_root, "a\nb"));
    }

    [Fact]
    public void Read_only_store_cannot_take_dirty_files()
    {
        Seed();
        DirtyFiles.Mark(_root, "Assets/a.cs");
        using GraphStore reader = GraphStore.Open(_root, StoreMode.ReadOnly);

        Assert.Throws<InvalidOperationException>(() => reader.TakeDirty());
    }

    [Fact]
    public void Readers_keep_the_old_graph_until_a_staged_graph_is_published()
    {
        Seed();
        using GraphStore reader = GraphStore.Open(_root, StoreMode.ReadOnly);
        string staging = Path.Combine(_root, GraphStore.StagingRelativePath);

        using (GraphStore staged = GraphStore.OpenStaging(_root))
        {
            Assert.True(staged.Created);
            using (BulkWriter bulk = staged.BeginBulk())
            {
                bulk.Add(File(Damage), [new(_typeDamage, NodeKind.Type, "Damage", Damage)], []);
                bulk.Commit();
            }

            Assert.True(System.IO.File.Exists(staging));
            Assert.NotNull(reader.GetNode(_typeHealth));
            Assert.Null(reader.GetNode(_typeDamage));

            staged.Publish();
            Assert.Throws<ObjectDisposedException>(() => staged.Publish());
        }

        Assert.Null(reader.GetNode(_typeHealth));
        Assert.NotNull(reader.GetNode(_typeDamage));
        Assert.Equal(_typeDamage, Assert.Single(reader.FindByName("damage", NodeKindSet.All, 10).Items).Key);
        Assert.False(System.IO.File.Exists(staging));
        using GraphStore writer = GraphStore.Open(_root, StoreMode.ReadWrite, _short);
        Assert.False(writer.Created);
    }

    [Fact]
    public void A_staged_graph_that_is_not_published_is_thrown_away()
    {
        Seed();

        using (GraphStore staged = GraphStore.OpenStaging(_root))
        {
            WriteType(staged, Damage, _typeDamage, "Damage", 1);
        }

        Assert.False(System.IO.File.Exists(Path.Combine(_root, GraphStore.StagingRelativePath)));
        using GraphStore reader = GraphStore.Open(_root, StoreMode.ReadOnly);
        Assert.NotNull(reader.GetNode(_typeHealth));
        Assert.Null(reader.GetNode(_typeDamage));
    }

    [Fact]
    public void Only_a_staged_store_can_be_published()
    {
        using GraphStore store = GraphStore.Open(_root, StoreMode.ReadWrite);

        Assert.Throws<InvalidOperationException>(() => store.Publish());
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("old version")]
    [InlineData("missing")]
    public void Publish_replaces_any_current_graph(string current)
    {
        string graph = Path.Combine(_root, GraphStore.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(graph)!);
        if (current == "corrupt")
        {
            System.IO.File.WriteAllText(graph, "this is not a database, just some text that is long enough to have a header");
        }
        else if (current == "old version")
        {
            Seed();
            SetSchemaVersion(graph, "0");
        }

        using (GraphStore staged = GraphStore.OpenStaging(_root))
        {
            WriteType(staged, Damage, _typeDamage, "Damage", 1);
            staged.Publish();
        }

        using GraphStore reader = GraphStore.Open(_root, StoreMode.ReadOnly);
        Assert.NotNull(reader.GetNode(_typeDamage));
        Assert.Null(reader.GetNode(_typeHealth));
    }

    [Fact]
    public void A_read_only_open_leaves_an_old_graph_alone_while_a_writer_is_busy()
    {
        Seed();
        string graph = Path.Combine(_root, GraphStore.RelativePath);
        SetSchemaVersion(graph, "0");

        using (GraphStore.OpenStaging(_root))
        {
            Assert.Throws<GraphRebuildRequiredException>(() => GraphStore.Open(_root, StoreMode.ReadOnly));
            Assert.True(System.IO.File.Exists(graph));
        }

        Assert.Throws<GraphRebuildRequiredException>(() => GraphStore.Open(_root, StoreMode.ReadOnly));
        Assert.False(System.IO.File.Exists(graph));
    }

    private static void SetSchemaVersion(string graph, string version)
    {
        using var db = new SqliteConnection($"Data Source={graph};Pooling=False");
        db.Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "UPDATE meta SET value = $v WHERE key = 'schema_version'";
        command.Parameters.AddWithValue("$v", version);
        command.ExecuteNonQuery();
    }
}
