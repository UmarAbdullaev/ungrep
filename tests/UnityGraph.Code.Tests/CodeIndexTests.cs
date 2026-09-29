using Microsoft.Data.Sqlite;
using UnityGraph.Core.Graph;

namespace UnityGraph.Code.Tests;

/// <summary>Full and incremental indexing into a real store (SPEC-code-index, Incremental reindex).</summary>
public sealed class CodeIndexTests : IDisposable
{
    private const string Turntable = "Assets/Scripts/Lab/Turntable.cs";
    private const string Broken = "Assets/Scripts/Broken.cs";
    private const string LabStage = "Assets/Scripts/Lab/LabStage.cs";
    private const string LabDebug = "Assets/Scripts/Lab/LabStage.Debug.cs";

    private static readonly string _stop = NodeKeys.Symbol("M:Game.Lab.Turntable.Stop");
    private static readonly string _halt = NodeKeys.Symbol("M:Game.Lab.Turntable.Halt");
    private static readonly string _resetStage = NodeKeys.Symbol("M:Game.Lab.LabStage.ResetStage");

    private readonly CodeFixture _fixture = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _fixture.Dispose();
    }

    private GraphStore Open() => GraphStore.Open(_fixture.Root, StoreMode.ReadWrite);

    private IndexSummary Update(IEnumerable<string>? extra = null)
    {
        using GraphStore store = Open();
        return CodeIndex.Update(_fixture.Load(), store, extra);
    }

    // Edit a file and move its mtime on, so the change shows even on a coarse clock.
    private void Edit(string path, string from, string to)
    {
        string full = Path.Combine(_fixture.Root, path);
        string text = File.ReadAllText(full);
        Assert.Contains(from, text, StringComparison.Ordinal);
        File.WriteAllText(full, text.Replace(from, to, StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(full, File.GetLastWriteTimeUtc(full).AddSeconds(5));
    }

    [Fact]
    public void A_new_graph_gets_a_full_index_then_nothing_to_do()
    {
        IndexSummary first = Update();

        Assert.Equal(9, first.Files);
        Assert.Equal(1, first.Vendored);
        Assert.True(first.Nodes > 50);
        Assert.Contains("missing reference Library/ScriptAssemblies/Missing.dll", first.Problems);

        IndexSummary second = Update();

        Assert.Equal(0, second.Files);
        Assert.Equal(0, second.Removed);
    }

    [Fact]
    public void Full_through_a_staged_bulk_writer()
    {
        using (GraphStore staging = GraphStore.OpenStaging(_fixture.Root))
        {
            using (BulkWriter writer = staging.BeginBulk())
            {
                CodeIndex.Full(_fixture.Load(), writer);
                writer.Commit();
            }

            staging.Publish();
        }

        using GraphStore store = GraphStore.Open(_fixture.Root, StoreMode.ReadOnly);
        Assert.Equal(Turntable, store.GetNode(_stop)?.File);
        Assert.Contains(store.EdgesTo(_stop, EdgeKindSet.Of(EdgeKind.Calls), 10).Items, e => e.Edge.File == Broken);
    }

    [Fact]
    public void An_incremental_run_leaves_the_same_rows_as_a_full_one()
    {
        Update();
        Edit(Turntable, "public void Stop()", "public void Halt()");
        Edit(Broken, "table.Stop();", "table.Halt();");
        Update();
        string incremental = Dump(_fixture.Root);

        using var other = new CodeFixture();
        foreach (string path in new[] { Turntable, Broken })
        {
            File.Copy(Path.Combine(_fixture.Root, path), Path.Combine(other.Root, path), overwrite: true);
        }

        using (GraphStore store = GraphStore.Open(other.Root, StoreMode.ReadWrite))
        {
            CodeIndex.Update(other.Load(), store);
        }

        Assert.Equal(Dump(other.Root), incremental);
    }

    private static string Dump(string root)
    {
        var text = new System.Text.StringBuilder();
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "Library", "unitygraph", "graph.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        foreach (string query in new[]
        {
            "SELECT path, size, hash, indexer, status FROM files ORDER BY path",
            "SELECT n.key, n.kind, n.name, f.path, n.line_start, n.line_end, n.props FROM nodes n JOIN files f ON f.id = n.file_id ORDER BY n.key",
            "SELECT e.src, e.kind, e.dst, f.path, e.line, e.confidence, e.props FROM edges e JOIN files f ON f.id = e.file_id ORDER BY e.src, e.kind, e.dst, f.path, e.line",
        })
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = query;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    text.Append(reader.IsDBNull(i) ? "null" : reader.GetValue(i).ToString()).Append('|');
                }

                text.Append('\n');
            }
        }

        return text.ToString();
    }

    [Fact]
    public void A_rename_reindexes_the_files_that_pointed_at_the_old_name()
    {
        Update();
        Edit(Turntable, "public void Stop()", "public void Halt()");
        Edit(Broken, "table.Stop();", "table.Halt();");

        IndexSummary summary = Update();

        // LabStage.cs didn't change, but its lambda called Stop.
        Assert.Equal(3, summary.Files);
        using GraphStore store = Open();
        Assert.Null(store.GetNode(_stop));
        Assert.Empty(store.EdgesTo(_stop, EdgeKindSet.All, 10).Items);
        Assert.Equal([Broken], store.EdgesTo(_halt, EdgeKindSet.Of(EdgeKind.Calls), 10).Items.Select(e => e.Edge.File));
    }

    [Fact]
    public void A_deleted_file_goes_and_edges_to_it_become_unresolved()
    {
        Update();
        File.Delete(Path.Combine(_fixture.Root, Turntable));

        IndexSummary summary = Update();

        Assert.Equal(1, summary.Removed);
        using GraphStore store = Open();
        Assert.Null(store.GetNode(_stop));
        Assert.Null(store.GetNode(NodeKeys.Symbol("T:Game.Lab.Turntable")));
    }

    [Fact]
    public void A_new_file_not_in_a_project_is_indexed()
    {
        Update();
        _fixture.Write("Assets/Scripts/Lab/Spinner.cs", "namespace Game.Lab { public class Spinner : Turntable { void Go() => Stop(); } }");

        IndexSummary summary = Update();

        Assert.Equal(1, summary.Files);
        using GraphStore store = Open();
        Assert.Contains(store.EdgesTo(_stop, EdgeKindSet.Of(EdgeKind.Calls), 10).Items, e => e.Edge.File == "Assets/Scripts/Lab/Spinner.cs");
    }

    [Fact]
    public void Extra_paths_are_reindexed_even_when_unchanged()
    {
        Update();

        IndexSummary summary = Update([Turntable, "Assets/Nope.cs"]);

        Assert.Equal(1, summary.Files);
    }

    [Fact]
    public void A_member_moving_between_two_changed_files_ends_up_in_its_new_file()
    {
        Update();
        Edit(LabStage, "private void ResetStage() => _opened = 0;", "");
        Edit(LabDebug, "public void Trace(int level) => Debug.Log(level);", "public void Trace(int level) => Debug.Log(level);\n        private void ResetStage() { }");

        IndexSummary summary = Update();

        Assert.DoesNotContain(summary.Problems, p => p.Contains("also declared", StringComparison.Ordinal));
        using GraphStore store = Open();
        Assert.Equal(LabDebug, store.GetNode(_resetStage)?.File);
    }

    [Fact]
    public void The_same_type_in_two_assemblies_is_kept_once_and_reported()
    {
        _fixture.Write("Assets/Editor/Notes.cs", "namespace Game.Lab { public static class OwnersNotes { } }");

        IndexSummary summary = Update();

        Assert.Contains("Assets/Scripts/Lab/Owner's Notes.cs: cs:T:Game.Lab.OwnersNotes is also declared in Assets/Editor/Notes.cs; kept there", summary.Problems);
    }
}
