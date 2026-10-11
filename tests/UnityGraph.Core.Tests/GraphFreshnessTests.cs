using Microsoft.Data.Sqlite;
using UnityGraph.Core.Graph;

namespace UnityGraph.Core.Tests;

/// <summary>SPEC-graph-core, Testing: freshness. Real files on disk, compared with what was indexed.</summary>
public sealed class GraphFreshnessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-fresh-" + Guid.NewGuid().ToString("N"));

    public GraphFreshnessTests() => Directory.CreateDirectory(Path.Combine(_root, "Assets"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private string Full(string path) => Path.Combine(_root, path);

    private void WriteFile(string path, string content) => File.WriteAllText(Full(path), content);

    private FileStat Stat(string path) => FileStat.Of(_root, path)!;

    private IEnumerable<FileStat> Stats(params string[] paths) => paths.Select(Stat);

    private GraphStore Indexed(params string[] paths)
    {
        GraphStore store = GraphStore.Open(_root, StoreMode.ReadWrite);
        foreach (string path in paths)
        {
            FileStat stat = Stat(path);
            store.ReplaceFile(new FileRecord(path, stat.Size, stat.MtimeNs, FileHash.Of(Full(path)), Indexer.Code), [], []);
        }

        return store;
    }

    private void Touch(string path) => File.SetLastWriteTimeUtc(Full(path), File.GetLastWriteTimeUtc(Full(path)).AddMinutes(1));

    [Fact]
    public void File_hash_is_lowercase_sha256()
    {
        WriteFile("Assets/a.cs", "abc");
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", FileHash.Of(Full("Assets/a.cs")));
    }

    [Fact]
    public void File_stat_reads_size_and_mtime()
    {
        WriteFile("Assets/a.cs", "abc");
        File.SetLastWriteTimeUtc(Full("Assets/a.cs"), new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new FileStat("Assets/a.cs", 3, 1_790_683_200_000_000_000), Stat("Assets/a.cs"));
        Assert.Null(FileStat.Of(_root, "Assets/missing.cs"));
    }

    [Fact]
    public void Unchanged_files_are_fresh()
    {
        WriteFile("Assets/a.cs", "class A {}");
        WriteFile("Assets/b.cs", "class B {}");
        using GraphStore store = Indexed("Assets/a.cs", "Assets/b.cs");

        FreshnessReport report = store.CheckFreshness(Stats("Assets/a.cs", "Assets/b.cs"));

        Assert.True(report.IsFresh);
        Assert.Empty(report.Touched);
    }

    [Fact]
    public void Touch_without_change_is_not_changed_and_is_recorded()
    {
        WriteFile("Assets/a.cs", "class A {}");
        using GraphStore store = Indexed("Assets/a.cs");
        Touch("Assets/a.cs");

        FreshnessReport report = store.CheckFreshness(Stats("Assets/a.cs"));
        Assert.True(report.IsFresh);
        Assert.Equal([Stat("Assets/a.cs")], report.Touched);

        store.UpdateStats(report.Touched);
        Assert.Empty(store.CheckFreshness(Stats("Assets/a.cs")).Touched);
    }

    [Fact]
    public void Content_changes_are_changed_with_or_without_a_size_change()
    {
        WriteFile("Assets/a.cs", "class A {}");
        WriteFile("Assets/b.cs", "class B {}");
        using GraphStore store = Indexed("Assets/a.cs", "Assets/b.cs");
        WriteFile("Assets/b.cs", "class C {}");
        Touch("Assets/b.cs");
        WriteFile("Assets/a.cs", "class A { int x; }");

        FreshnessReport report = store.CheckFreshness(Stats("Assets/b.cs", "Assets/a.cs"));

        Assert.Equal(["Assets/a.cs", "Assets/b.cs"], report.Changed);
        Assert.Empty(report.Touched);
        Assert.False(report.IsFresh);
    }

    [Fact]
    public void New_files_are_added_and_missing_ones_removed()
    {
        WriteFile("Assets/a.cs", "a");
        WriteFile("Assets/b.cs", "b");
        WriteFile("Assets/c.cs", "c");
        using GraphStore store = Indexed("Assets/b.cs", "Assets/c.cs");
        WriteFile("Assets/d.cs", "d");

        FreshnessReport report = store.CheckFreshness(Stats("Assets/d.cs", "Assets/c.cs", "Assets/a.cs"));

        Assert.Equal(["Assets/a.cs", "Assets/d.cs"], report.Added);
        Assert.Equal(["Assets/b.cs"], report.Removed);
        Assert.Empty(report.Changed);
    }

    [Fact]
    public void A_file_gone_since_it_was_listed_is_changed()
    {
        WriteFile("Assets/a.cs", "a");
        using GraphStore store = Indexed("Assets/a.cs");
        FileStat listed = Stat("Assets/a.cs") with { MtimeNs = 1 };
        File.Delete(Full("Assets/a.cs"));

        Assert.Equal(["Assets/a.cs"], store.CheckFreshness([listed]).Changed);
    }

    [Fact]
    public void Duplicate_paths_are_rejected()
    {
        WriteFile("Assets/a.cs", "a");
        using GraphStore store = Indexed("Assets/a.cs");

        Assert.Throws<ArgumentException>(() => store.CheckFreshness(Stats("Assets/a.cs", "Assets/a.cs")));
    }

    [Fact]
    public void Read_only_store_checks_but_cannot_record()
    {
        WriteFile("Assets/a.cs", "a");
        Indexed("Assets/a.cs").Dispose();
        Touch("Assets/a.cs");
        using GraphStore reader = GraphStore.Open(_root, StoreMode.ReadOnly);

        FreshnessReport report = reader.CheckFreshness(Stats("Assets/a.cs"));

        Assert.Single(report.Touched);
        Assert.Throws<InvalidOperationException>(() => reader.UpdateStats(report.Touched));
    }

    [Fact]
    public void A_graph_from_before_whole_values_has_every_file_indexed_again_once()
    {
        WriteFile("Assets/a.cs", "class A {}");
        WriteFile("Assets/b.cs", "class B {}");
        Indexed("Assets/a.cs", "Assets/b.cs").Dispose();
        using (var db = new SqliteConnection($"Data Source={Full(GraphStore.RelativePath)};Pooling=False"))
        {
            db.Open();
            using SqliteCommand command = db.CreateCommand();
            command.CommandText = $"DELETE FROM meta WHERE key = '{GraphStore.ValuesKey}'";
            command.ExecuteNonQuery();
        }

        using (GraphStore reader = GraphStore.Open(_root, StoreMode.ReadOnly))
        {
            Assert.True(reader.CheckFreshness(Stats("Assets/a.cs", "Assets/b.cs")).IsFresh);
        }

        using (GraphStore store = GraphStore.Open(_root, StoreMode.ReadWrite))
        {
            Assert.False(store.Created);
            Assert.Equal(["Assets/a.cs", "Assets/b.cs"], store.CheckFreshness(Stats("Assets/a.cs", "Assets/b.cs")).Changed.Order(StringComparer.Ordinal));
        }

        Indexed("Assets/a.cs", "Assets/b.cs").Dispose();
        using GraphStore again = GraphStore.Open(_root, StoreMode.ReadWrite);
        Assert.True(again.CheckFreshness(Stats("Assets/a.cs", "Assets/b.cs")).IsFresh);
    }
}
