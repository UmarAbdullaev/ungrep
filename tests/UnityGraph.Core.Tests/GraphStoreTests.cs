using Microsoft.Data.Sqlite;
using UnityGraph.Core.Graph;

namespace UnityGraph.Core.Tests;

/// <summary>
/// The graph store (SPEC-graph-core, Testing: unit and version): per-file replacement,
/// unresolved edges, ordering, limits, name search, bulk writes and rebuilds.
/// </summary>
public sealed class GraphStoreTests : IDisposable
{
    private const string Health = "Assets/Scripts/PlayerHealth.cs";
    private const string Damage = "Assets/Scripts/Damage.cs";
    private const string Scene = "Assets/Scenes/Main.unity";

    private static readonly string _typeHealth = NodeKeys.Symbol("T:Game.PlayerHealth");
    private static readonly string _takeDamage = NodeKeys.Symbol("M:Game.PlayerHealth.TakeDamage(System.Int32)");
    private static readonly string _maxHealth = NodeKeys.Symbol("F:Game.PlayerHealth.maxHealth");
    private static readonly string _typeDamage = NodeKeys.Symbol("T:Game.Damage");
    private static readonly string _apply = NodeKeys.Symbol("M:Game.Damage.Apply");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-graph-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string DbPath => Path.Combine(_root, GraphStore.RelativePath);

    private static FileRecord File(string path, Indexer indexer = Indexer.Code) =>
        new(path, 100, 1_000, new string('a', 64), indexer);

    private static (FileRecord, Node[], Edge[]) HealthFile(string methodName = "TakeDamage") =>
        (File(Health),
        [
            new(_typeHealth, NodeKind.Type, "PlayerHealth", Health, 3, 20, """{"subkind":"class"}"""),
            new(_takeDamage, NodeKind.Method, methodName, Health, 10, 14),
            new(_maxHealth, NodeKind.Field, "maxHealth", Health, 5, 5, """{"serialized":true}"""),
        ],
        [
            new(_typeHealth, EdgeKind.Contains, _takeDamage, Confidence.Exact, Health, 10),
            new(_typeHealth, EdgeKind.Contains, _maxHealth, Confidence.Exact, Health, 5),
            new(_takeDamage, EdgeKind.Calls, _apply, Confidence.Exact, Health, 12),
        ]);

    private static (FileRecord, Node[], Edge[]) DamageFile() =>
        (File(Damage),
        [
            new(_typeDamage, NodeKind.Type, "Damage", Damage, 1, 9),
            new(_apply, NodeKind.Method, "Apply", Damage, 4, 8),
        ],
        [
            new(_typeDamage, EdgeKind.Contains, _apply, Confidence.Exact, Damage, 4),
        ]);

    private GraphStore OpenWrite() => GraphStore.Open(_root, StoreMode.ReadWrite);

    private GraphStore Seeded()
    {
        GraphStore store = OpenWrite();
        var (file, nodes, edges) = HealthFile();
        store.ReplaceFile(file, nodes, edges);
        return store;
    }

    [Fact]
    public void Key_builders()
    {
        Assert.Equal("cs:T:Game.PlayerHealth", NodeKeys.Symbol("T:Game.PlayerHealth"));
        Assert.Equal("a:4f1c0d2b", NodeKeys.Asset("4f1c0d2b"));
        Assert.Equal("o:4f1c0d2b:1845920371", NodeKeys.Object("4f1c0d2b", 1845920371));
        Assert.Equal("o:4f1c0d2b:-42", NodeKeys.Object("4f1c0d2b", -42));
        Assert.Equal("p:ProjectSettings/TagManager.asset", NodeKeys.File("ProjectSettings/TagManager.asset"));
        Assert.False(NodeKeys.IsValid("cs:"));
        Assert.False(NodeKeys.IsValid("T:Game.PlayerHealth"));
    }

    [Fact]
    public void Kind_names_round_trip_and_unknown_names_are_rejected()
    {
        Assert.All(Enum.GetValues<NodeKind>(), k => Assert.Equal(k, KindNames.ParseNode(KindNames.Of(k))));
        Assert.All(Enum.GetValues<EdgeKind>(), k => Assert.Equal(k, KindNames.ParseEdge(KindNames.Of(k))));
        Assert.Equal("game_object", KindNames.Of(NodeKind.GameObject));
        Assert.Equal("anim_event_calls", KindNames.Of(EdgeKind.AnimEventCalls));
        Assert.Throws<FormatException>(() => KindNames.ParseEdge("call"));
    }

    [Fact]
    public void New_database_is_created_with_meta_and_reopened()
    {
        using (GraphStore store = OpenWrite())
        {
            Assert.True(store.Created);
        }

        using (GraphStore again = OpenWrite())
        {
            Assert.False(again.Created);
        }

        using GraphStore reader = GraphStore.Open(_root, StoreMode.ReadOnly);
        Assert.False(reader.Created);
        Assert.Null(reader.GetNode(_typeHealth));
    }

    [Fact]
    public void Round_trips_nodes_and_edges()
    {
        using GraphStore store = Seeded();

        Assert.Equal(new Node(_typeHealth, NodeKind.Type, "PlayerHealth", Health, 3, 20, """{"subkind":"class"}"""), store.GetNode(_typeHealth));
        Page<EdgeHit> from = store.EdgesFrom(_typeHealth, EdgeKindSet.All, 10);
        Assert.False(from.More);
        Assert.Equal([_maxHealth, _takeDamage], from.Items.Select(h => h.Edge.Dst));
        Assert.All(from.Items, h => Assert.True(h.Resolved));
        Assert.Equal(new Edge(_typeHealth, EdgeKind.Contains, _takeDamage, Confidence.Exact, Health, 10), from.Items[1].Edge);
    }

    [Fact]
    public void Replace_file_replaces_only_its_own_rows()
    {
        using GraphStore store = Seeded();
        var (damage, damageNodes, damageEdges) = DamageFile();
        store.ReplaceFile(damage, damageNodes, damageEdges);

        var (file, nodes, edges) = HealthFile("Hit");
        store.ReplaceFile(file, nodes[..2], edges[..1]);

        Assert.Equal("Hit", store.GetNode(_takeDamage)!.Name);
        Assert.Null(store.GetNode(_maxHealth));
        Assert.Single(store.EdgesFrom(_typeHealth, EdgeKindSet.All, 10).Items);
        Assert.Empty(store.EdgesFrom(_takeDamage, EdgeKindSet.All, 10).Items);
        Assert.NotNull(store.GetNode(_apply));
        Assert.Single(store.EdgesFrom(_typeDamage, EdgeKindSet.All, 10).Items);
    }

    [Fact]
    public void Remove_file_takes_its_rows_and_names()
    {
        using GraphStore store = Seeded();

        store.RemoveFile(Health);
        store.RemoveFile("Assets/Never/Indexed.cs");

        Assert.Null(store.GetNode(_typeHealth));
        Assert.Empty(store.EdgesFrom(_typeHealth, EdgeKindSet.All, 10).Items);
        Assert.Empty(store.FindByName("PlayerHealth", NodeKindSet.All, 10).Items);
    }

    [Fact]
    public void Edges_to_missing_nodes_are_unresolved_until_the_target_is_indexed()
    {
        using GraphStore store = Seeded();

        EdgeHit call = Assert.Single(store.EdgesFrom(_takeDamage, EdgeKindSet.All, 10).Items);
        Assert.False(call.Resolved);

        var (damage, nodes, edges) = DamageFile();
        store.ReplaceFile(damage, nodes, edges);

        Assert.True(Assert.Single(store.EdgesFrom(_takeDamage, EdgeKindSet.All, 10).Items).Resolved);
        EdgeHit caller = Assert.Single(store.EdgesTo(_apply, EdgeKindSet.Of(EdgeKind.Calls), 10).Items);
        Assert.Equal(_takeDamage, caller.Edge.Src);
        Assert.True(caller.Resolved);
    }

    [Fact]
    public void Edges_from_other_files_keep_pointing_at_a_reindexed_node()
    {
        using GraphStore store = Seeded();
        string component = NodeKeys.Object("9d0e", 114);
        store.ReplaceFile(File(Scene, Indexer.Asset),
            [new(component, NodeKind.Component, "PlayerHealth", Scene, 40, 52)],
            [new(component, EdgeKind.InstanceOf, _typeHealth, Confidence.Exact, Scene, 40)]);

        var (file, nodes, edges) = HealthFile();
        store.ReplaceFile(file, nodes, edges);

        EdgeHit hit = Assert.Single(store.EdgesTo(_typeHealth, EdgeKindSet.Of(EdgeKind.InstanceOf), 10).Items);
        Assert.Equal(new Edge(component, EdgeKind.InstanceOf, _typeHealth, Confidence.Exact, Scene, 40), hit.Edge);
    }

    [Fact]
    public void Edges_are_ordered_by_kind_then_key_then_line_and_limited()
    {
        using GraphStore store = OpenWrite();
        const string path = "Assets/Scripts/Many.cs";
        string src = NodeKeys.Symbol("T:Many");
        Edge E(EdgeKind kind, string dst, int line) => new(src, kind, NodeKeys.Symbol(dst), Confidence.Heuristic, path, line);
        store.ReplaceFile(File(path), [new(src, NodeKind.Type, "Many", path)],
        [
            E(EdgeKind.UsesType, "T:B", 9), E(EdgeKind.Calls, "M:Z", 1), E(EdgeKind.Calls, "M:A", 7),
            E(EdgeKind.Calls, "M:A", 3), E(EdgeKind.Contains, "M:C", 2),
        ]);

        Page<EdgeHit> all = store.EdgesFrom(src, EdgeKindSet.All, 10);
        Assert.Equal(
            ["calls cs:M:A 3", "calls cs:M:A 7", "calls cs:M:Z 1", "contains cs:M:C 2", "uses_type cs:T:B 9"],
            all.Items.Select(h => $"{KindNames.Of(h.Edge.Kind)} {h.Edge.Dst} {h.Edge.Line}"));

        Page<EdgeHit> two = store.EdgesFrom(src, EdgeKindSet.All, 2);
        Assert.True(two.More);
        Assert.Equal(all.Items.Take(2), two.Items);

        Page<EdgeHit> calls = store.EdgesFrom(src, EdgeKindSet.Of(EdgeKind.Calls), 3);
        Assert.False(calls.More);
        Assert.Equal(3, calls.Items.Count);

        Assert.Empty(store.EdgesFrom(src, EdgeKindSet.Of(), 10).Items);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.EdgesFrom(src, EdgeKindSet.All, 0));
    }

    [Fact]
    public void Find_by_name_matches_word_prefixes_exact_first()
    {
        using GraphStore store = Seeded();
        string playerType = NodeKeys.Symbol("T:Game.Player");
        store.ReplaceFile(File("Assets/Scripts/Player.cs"), [new(playerType, NodeKind.Type, "Player", "Assets/Scripts/Player.cs")], []);

        Assert.Equal([playerType, _typeHealth], store.FindByName("player", NodeKindSet.All, 10).Items.Select(n => n.Key));
        Assert.Equal([_maxHealth], store.FindByName("max", NodeKindSet.All, 10).Items.Select(n => n.Key));
        Assert.Equal([_maxHealth], store.FindByName("maxHealth", NodeKindSet.Of(NodeKind.Field), 10).Items.Select(n => n.Key));
        Assert.Empty(store.FindByName("maxHealth", NodeKindSet.Of(NodeKind.Method), 10).Items);
        Assert.Empty(store.FindByName(" ._ ", NodeKindSet.All, 10).Items);
        Assert.Empty(store.FindByName("\"quoted*", NodeKindSet.All, 10).Items);

        Page<Node> one = store.FindByName("p", NodeKindSet.All, 1);
        Assert.True(one.More);
        Assert.Single(one.Items);
    }

    [Fact]
    public void Find_by_name_follows_renames()
    {
        using GraphStore store = Seeded();
        var (file, nodes, edges) = HealthFile("Hit");

        store.ReplaceFile(file, nodes, edges);

        Assert.Empty(store.FindByName("TakeDamage", NodeKindSet.All, 10).Items);
        Assert.Equal(_takeDamage, Assert.Single(store.FindByName("hit", NodeKindSet.All, 10).Items).Key);
    }

    [Fact]
    public void Bulk_commit_writes_all_files_and_the_name_index()
    {
        using GraphStore store = OpenWrite();
        using (BulkWriter bulk = store.BeginBulk())
        {
            var (h, hn, he) = HealthFile();
            var (d, dn, de) = DamageFile();
            bulk.Add(h, hn, he);
            bulk.Add(d, dn, de);
            Assert.Throws<InvalidOperationException>(() => store.GetNode(_apply));
            Assert.Throws<InvalidOperationException>(() => store.RemoveFile(Health));
            bulk.Commit();
        }

        Assert.True(Assert.Single(store.EdgesFrom(_takeDamage, EdgeKindSet.All, 10).Items).Resolved);
        Assert.Equal(_apply, Assert.Single(store.FindByName("apply", NodeKindSet.All, 10).Items).Key);
        store.RemoveFile(Damage);
        Assert.Empty(store.FindByName("apply", NodeKindSet.All, 10).Items);
    }

    [Fact]
    public void Bulk_without_commit_rolls_back()
    {
        using GraphStore store = Seeded();
        using (BulkWriter bulk = store.BeginBulk())
        {
            var (d, dn, de) = DamageFile();
            bulk.Add(d, dn, de);
        }

        Assert.Null(store.GetNode(_apply));
        Assert.NotNull(store.GetNode(_typeHealth));
    }

    [Fact]
    public void Invalid_rows_are_rejected_and_nothing_is_written()
    {
        using GraphStore store = Seeded();
        var (file, nodes, edges) = HealthFile();

        void Rejects(string message, FileRecord f, Node[] n, Edge[] e)
        {
            var error = Assert.Throws<ArgumentException>(() => store.ReplaceFile(f, n, e));
            Assert.Contains(message, error.Message, StringComparison.Ordinal);
        }

        Rejects("forward slashes", file with { Path = @"Assets\Scripts\PlayerHealth.cs" }, nodes, edges);
        Rejects("forward slashes", file with { Path = "/abs/PlayerHealth.cs" }, nodes, edges);
        Rejects("64 lowercase hex", file with { Hash = "ABC" }, nodes, edges);
        Rejects("unknown node kind 99", file, [nodes[0] with { Kind = (NodeKind)99 }], edges);
        Rejects("unknown edge kind 42", file, nodes, [edges[0] with { Kind = (EdgeKind)42 }]);
        Rejects("unknown confidence 0", file, nodes, [edges[0] with { Confidence = 0 }]);
        Rejects("invalid key", file, [nodes[0] with { Key = "PlayerHealth" }], edges);
        Rejects("duplicate key", file, [nodes[0], nodes[0]], edges);
        Rejects("owned by 'Other.cs'", file, [nodes[0] with { File = "Other.cs" }], edges);
        Rejects("props must be a JSON object", file, [nodes[0] with { Props = "[1]" }], edges);
        Rejects("props must be a JSON object", file, [nodes[0] with { Props = "{" + new string('x', 1024) + "}" }], edges);

        Assert.Equal("TakeDamage", store.GetNode(_takeDamage)!.Name);
        Assert.Equal(3, store.EdgesFrom(_typeHealth, EdgeKindSet.All, 10).Items.Count + store.EdgesFrom(_takeDamage, EdgeKindSet.All, 10).Items.Count);
    }

    [Fact]
    public void Read_only_store_refuses_writes()
    {
        Seeded().Dispose();
        using GraphStore reader = GraphStore.Open(_root, StoreMode.ReadOnly);
        var (file, nodes, edges) = HealthFile();

        Assert.Throws<InvalidOperationException>(() => reader.ReplaceFile(file, nodes, edges));
        Assert.Throws<InvalidOperationException>(() => reader.BeginBulk());
        Assert.NotNull(reader.GetNode(_typeHealth));
    }

    [Fact]
    public void Read_only_open_without_a_graph_asks_for_a_rebuild()
    {
        var e = Assert.Throws<GraphRebuildRequiredException>(() => GraphStore.Open(_root, StoreMode.ReadOnly));
        Assert.StartsWith("rebuild required: no graph", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_schema_version_is_rebuilt_not_read()
    {
        Seeded().Dispose();
        SetSchemaVersion("0");

        var e = Assert.Throws<GraphRebuildRequiredException>(() => GraphStore.Open(_root, StoreMode.ReadOnly));
        Assert.Equal("rebuild required: schema version 0, expected 1", e.Message);
        Assert.False(System.IO.File.Exists(DbPath));

        Seeded().Dispose();
        SetSchemaVersion("2");
        using GraphStore store = OpenWrite();
        Assert.True(store.Created);
        Assert.Null(store.GetNode(_typeHealth));
    }

    [Fact]
    public void Corrupt_database_is_rebuilt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        System.IO.File.WriteAllText(DbPath, "this is not a database, just some text that is long enough to have a header");

        Assert.Throws<GraphRebuildRequiredException>(() => GraphStore.Open(_root, StoreMode.ReadOnly));
        using GraphStore store = OpenWrite();
        Assert.True(store.Created);
    }

    [Fact]
    public void Keys_in_a_file_and_files_with_edges_to_keys()
    {
        using GraphStore store = Seeded();
        var (damage, damageNodes, damageEdges) = DamageFile();
        store.ReplaceFile(damage, damageNodes, damageEdges);

        Assert.Equal([_maxHealth, _takeDamage, _typeHealth], store.KeysInFile(Health).Order(StringComparer.Ordinal));
        Assert.Empty(store.KeysInFile("Assets/None.cs"));
        Assert.Equal([Health], store.FilesWithEdgesTo([_apply, "cs:T:Nobody"], EdgeKindSet.Of(EdgeKind.Calls)));
        Assert.Empty(store.FilesWithEdgesTo([_apply], EdgeKindSet.Of(EdgeKind.UsesType)));
        Assert.Equal([Damage, Health], store.FilesWithEdgesTo([_apply, _takeDamage], EdgeKindSet.All));
        Assert.Empty(store.FilesWithEdgesTo([], EdgeKindSet.All));
    }

    [Fact]
    public void Freshness_can_be_limited_to_one_indexer()
    {
        using GraphStore store = Seeded();
        store.ReplaceFile(File(Scene, Indexer.Asset), [], []);

        FreshnessReport report = store.CheckFreshness([], Indexer.Code);

        Assert.Equal([Health], report.Removed);
        Assert.Equal(2, store.CheckFreshness([]).Removed.Count);
    }

    [Fact]
    public void A_graph_directory_keeps_the_project_untouched()
    {
        string elsewhere = Path.Combine(_root, "elsewhere");
        using (GraphStore store = GraphStore.Open(Path.Combine(_root, "project"), StoreMode.ReadWrite, graphDirectory: elsewhere))
        {
            var (file, nodes, edges) = HealthFile();
            store.ReplaceFile(file, nodes, edges);
        }

        Assert.True(System.IO.File.Exists(Path.Combine(elsewhere, "graph.db")));
        Assert.False(Directory.Exists(Path.Combine(_root, "project")));
        using GraphStore read = GraphStore.Open(Path.Combine(_root, "project"), StoreMode.ReadOnly, graphDirectory: elsewhere);
        Assert.NotNull(read.GetNode(_typeHealth));
    }

    private void SetSchemaVersion(string version)
    {
        using var db = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        db.Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "UPDATE meta SET value = $v WHERE key = 'schema_version'";
        command.Parameters.AddWithValue("$v", version);
        command.ExecuteNonQuery();
    }
}
