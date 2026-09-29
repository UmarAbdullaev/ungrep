using Microsoft.Data.Sqlite;

namespace UnityGraph.Core.Graph;

/// <summary>
/// A cold index in one transaction (<see cref="GraphStore.BeginBulk"/>). The name index is
/// rebuilt once at <see cref="Commit"/>; disposing without committing rolls everything back.
/// </summary>
public sealed class BulkWriter : IDisposable
{
    private readonly GraphStore _store;
    private readonly SqliteConnection _db;
    private readonly SqliteTransaction _tx;
    private readonly RowWriter _rows;
    private bool _done;

    internal BulkWriter(GraphStore store, SqliteConnection db, SqliteTransaction tx)
    {
        _store = store;
        _db = db;
        _tx = tx;
        _rows = new RowWriter(db, tx);
    }

    /// <summary>Adds a file, replacing it if it is already in the graph.</summary>
    /// <exception cref="ArgumentException">A row is invalid.</exception>
    public void Add(FileRecord file, IReadOnlyList<Node> nodes, IReadOnlyList<Edge> edges)
    {
        ObjectDisposedException.ThrowIf(_done, this);
        GraphStore.Validate(file, nodes, edges);
        _rows.Write(file, nodes, edges, updateNames: false);
    }

    public void Commit()
    {
        ObjectDisposedException.ThrowIf(_done, this);
        GraphStore.Execute(_db, _tx, "DELETE FROM names; INSERT INTO names(name, key, kind) SELECT name, key, kind FROM nodes;");
        _tx.Commit();
        End();
    }

    public void Dispose()
    {
        if (!_done)
        {
            _tx.Rollback();
            End();
        }
    }

    private void End()
    {
        _done = true;
        _rows.Dispose();
        _tx.Dispose();
        _store.EndBulk();
    }
}
