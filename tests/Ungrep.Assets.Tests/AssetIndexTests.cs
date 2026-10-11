using Microsoft.Data.Sqlite;
using Ungrep.Core;
using Ungrep.Core.Graph;

namespace Ungrep.Assets.Tests;

/// <summary>Scanning, full and incremental indexing into a real store (SPEC-asset-index, Incremental reindex).</summary>
public sealed class AssetIndexTests : IDisposable
{
    private const string LabMat = "Assets/Materials/Lab.mat";
    private const string Turntable = "Assets/Prefabs/Turntable.prefab";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-assets-" + Guid.NewGuid().ToString("N")[..8]);

    public AssetIndexTests()
    {
        Copy(AssetFileIndexerTests.Source, _root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is harmless.
        }
    }

    private AssetWorkspace Scan() => AssetWorkspace.Scan(_root, VendoredFolders.Default);

    private GraphStore Open() => GraphStore.Open(_root, StoreMode.ReadWrite);

    private IndexSummary Update(IEnumerable<string>? extra = null)
    {
        using GraphStore store = Open();
        if (store.Created)
        {
            using BulkWriter writer = store.BeginBulk();
            IndexSummary full = AssetIndex.Full(Scan(), writer);
            writer.Commit();
            return full;
        }

        return AssetIndex.Update(Scan(), store, store.TakeDirty(), extra);
    }

    private void Edit(string path, string from, string to)
    {
        string full = Path.Combine(_root, path);
        string text = File.ReadAllText(full);
        Assert.Contains(from, text, StringComparison.Ordinal);
        File.WriteAllText(full, text.Replace(from, to, StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(full, File.GetLastWriteTimeUtc(full).AddSeconds(5));
    }

    private void Write(string path, string text)
    {
        string full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    [Fact]
    public void Scan_tracks_metas_and_own_content_only()
    {
        Write("Assets/Samples~/Hidden.prefab", "%YAML 1.1\n");
        Write("Assets/Samples~/Hidden.prefab.meta", "guid: 11111111111111111111111111111111\n");
        Write("Assets/.git/x.prefab.meta", "guid: 22222222222222222222222222222222\n");
        Write("Packages/com.lab.tools/Runtime/Tool.cs.meta", "guid: 33333333333333333333333333333333\n");
        Write("Packages/com.lab.tools/Runtime/Tool.prefab", "%YAML 1.1\n");
        Write("Packages/manifest.json", "{}");

        AssetWorkspace workspace = Scan();
        string[] paths = [.. workspace.TrackedFiles.Select(f => f.Path)];

        Assert.Equal(paths.Order(StringComparer.Ordinal), paths);
        Assert.Contains("Assets/3rd Party/Tweens/TweenDot.prefab.meta", paths);
        Assert.DoesNotContain("Assets/3rd Party/Tweens/TweenDot.prefab", paths);
        Assert.DoesNotContain("Assets/Prefabs.meta", paths);
        Assert.Contains("Assets/Scenes/New.unity", paths);
        Assert.DoesNotContain("Assets/Scripts/Lab/Turntable.cs", paths);
        Assert.Contains("Packages/com.lab.tools/Runtime/Tool.cs.meta", paths);
        Assert.DoesNotContain("Packages/com.lab.tools/Runtime/Tool.prefab", paths);
        Assert.DoesNotContain(paths, p => p.Contains('~', StringComparison.Ordinal) || p.Contains(".git", StringComparison.Ordinal));
        Assert.True(workspace.IsVendored("Packages/com.lab.tools/Runtime/Tool.cs.meta"));
        Assert.Throws<ArgumentException>(() => workspace.IndexMany(["Assets/Scripts/Lab/Turntable.cs"]));
    }

    [Fact]
    public void A_new_graph_gets_a_full_index_then_nothing_to_do()
    {
        IndexSummary first = Update();

        Assert.Equal(19, first.Files);
        Assert.Equal(1, first.Vendored);
        Assert.Contains("Assets/Scenes/New.unity: no .meta with a guid (not imported by Unity yet?)", first.Problems);

        IndexSummary second = Update();

        Assert.Equal(0, second.Files);
        Assert.Equal(0, second.Removed);
    }

    [Fact]
    public void An_edited_file_is_reindexed_alone()
    {
        Update();
        Edit(Turntable, "_speed: 1.5", "_speed: 2");
        Edit(Turntable, "m_Name: Platter", "m_Name: Disc");

        IndexSummary summary = Update();

        Assert.Equal(1, summary.Files);
        using GraphStore store = Open();
        Assert.Equal("Disc", store.GetNode("o:b1000000000000000000000000000001:200")?.Name);
    }

    [Fact]
    public void A_changed_meta_reindexes_its_content_file()
    {
        Update();
        Edit(LabMat + ".meta", "d4000000000000000000000000000004", "d4000000000000000000000000000044");

        IndexSummary summary = Update();

        Assert.Equal(2, summary.Files);
        using GraphStore store = Open();
        Assert.Null(store.GetNode("a:d4000000000000000000000000000004"));
        Assert.NotNull(store.GetNode("a:d4000000000000000000000000000044"));
        EdgeHit texture = Assert.Single(store.EdgesFrom("a:d4000000000000000000000000000044", EdgeKindSet.Of(EdgeKind.References), 10).Items);
        Assert.Equal("a:e5000000000000000000000000000005", texture.Edge.Dst);

        // The prefab still points at the old GUID: the edge stays, into a key no node has.
        Assert.Single(store.EdgesTo("a:d4000000000000000000000000000004", EdgeKindSet.All, 10).Items, e => e.Edge.File == Turntable);
    }

    [Fact]
    public void A_removed_meta_turns_its_content_file_into_an_error()
    {
        Update();
        File.Delete(Path.Combine(_root, LabMat + ".meta"));

        IndexSummary summary = Update();

        Assert.Equal(1, summary.Files);
        Assert.Equal(1, summary.Removed);
        Assert.Contains(LabMat + ": no .meta with a guid (not imported by Unity yet?)", summary.Problems);
        using GraphStore store = Open();
        Assert.Null(store.GetNode("a:d4000000000000000000000000000004"));
    }

    [Fact]
    public void Dirty_and_extra_files_are_reindexed_when_tracked()
    {
        Update();

        IndexSummary summary = Update([Turntable, "Assets/Scripts/Lab/Turntable.cs", "Assets/Nowhere.prefab"]);

        Assert.Equal(1, summary.Files);
    }

    [Fact]
    public void A_copied_meta_keeps_the_guid_with_its_first_owner()
    {
        File.Copy(Path.Combine(_root, LabMat + ".meta"), Path.Combine(_root, "Assets/Materials/Copy.mat.meta"));
        File.Copy(Path.Combine(_root, LabMat), Path.Combine(_root, "Assets/Materials/Copy.mat"));

        IndexSummary summary = Update();

        Assert.Contains("Assets/Materials/Lab.mat.meta: a:d4000000000000000000000000000004 is also declared in Assets/Materials/Copy.mat.meta; kept there", summary.Problems);
    }

    [Fact]
    public void Incremental_rows_match_a_full_index()
    {
        Update();
        Edit(Turntable, "m_Name: Platter", "m_Name: Disc");
        Edit(LabMat + ".meta", "d4000000000000000000000000000004", "d4000000000000000000000000000044");
        Update();
        string incremental = Dump();

        SqliteConnection.ClearAllPools();
        Directory.Delete(Path.Combine(_root, "Library", "ungrep"), recursive: true);
        Update();

        Assert.Equal(Dump(), incremental);
    }

    private string Dump()
    {
        using GraphStore store = Open();
        var lines = new List<string>();
        foreach (FileStat file in Scan().TrackedFiles)
        {
            foreach (string key in store.KeysInFile(file.Path))
            {
                lines.Add(file.Path + " " + key + " " + store.GetNode(key)?.Name);
                lines.AddRange(store.EdgesFrom(key, EdgeKindSet.All, 1000).Items.Select(e => "  " + e.Edge));
            }
        }

        return string.Join('\n', lines);
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }

        foreach (string dir in Directory.EnumerateDirectories(from))
        {
            if (Path.GetFileName(dir) != "Library")
            {
                Copy(dir, Path.Combine(to, Path.GetFileName(dir)));
            }
        }
    }
}
