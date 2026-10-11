using Microsoft.Data.Sqlite;

namespace Ungrep.Core.Graph;

/// <summary>
/// Prepared insert and delete commands for one write transaction, shared by
/// <see cref="GraphStore.ReplaceFile"/> and <see cref="BulkWriter"/>. Rows are validated before.
/// </summary>
internal sealed class RowWriter : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly SqliteTransaction _tx;
    private readonly SqliteCommand _findFile;
    private readonly SqliteCommand _deleteNames;
    private readonly SqliteCommand _deleteFile;
    private readonly SqliteCommand _insertFile;
    private readonly SqliteCommand _insertNode;
    private readonly SqliteCommand _insertName;
    private readonly SqliteCommand _insertEdge;

    public RowWriter(SqliteConnection db, SqliteTransaction tx)
    {
        _db = db;
        _tx = tx;
        _findFile = Prepare("SELECT id FROM files WHERE path = $path", "$path");
        _deleteNames = Prepare("DELETE FROM names WHERE key IN (SELECT key FROM nodes WHERE file_id = $id)", "$id");
        // nodes and edges go with it (ON DELETE CASCADE).
        _deleteFile = Prepare("DELETE FROM files WHERE id = $id", "$id");
        _insertFile = Prepare(
            "INSERT INTO files(path, size, mtime_ns, hash, indexer, status) VALUES ($path, $size, $mtime, $hash, $indexer, $status) RETURNING id",
            "$path", "$size", "$mtime", "$hash", "$indexer", "$status");
        _insertNode = Prepare(
            "INSERT INTO nodes(key, kind, name, file_id, line_start, line_end, props) VALUES ($key, $kind, $name, $file, $start, $end, $props)",
            "$key", "$kind", "$name", "$file", "$start", "$end", "$props");
        _insertName = Prepare("INSERT INTO names(name, key, kind) VALUES ($name, $key, $kind)", "$name", "$key", "$kind");
        _insertEdge = Prepare(
            "INSERT INTO edges(src, kind, dst, confidence, file_id, line, props) VALUES ($src, $kind, $dst, $confidence, $file, $line, $props)",
            "$src", "$kind", "$dst", "$confidence", "$file", "$line", "$props");
    }

    public void Dispose()
    {
        foreach (SqliteCommand command in new[] { _findFile, _deleteNames, _deleteFile, _insertFile, _insertNode, _insertName, _insertEdge })
        {
            command.Dispose();
        }
    }

    public void Write(FileRecord file, IReadOnlyList<Node> nodes, IReadOnlyList<Edge> edges, bool updateNames)
    {
        Remove(file.Path, updateNames);

        long fileId = (long)Scalar(_insertFile, file.Path, file.Size, file.MtimeNs, Convert.FromHexString(file.Hash),
            GraphStore.IndexerName(file.Indexer), file.Status.ToString().ToLowerInvariant())!;

        foreach (Node node in nodes)
        {
            string kind = KindNames.Of(node.Kind);
            Exec(_insertNode, node.Key, kind, node.Name, fileId, node.LineStart, node.LineEnd, node.Props);
            if (updateNames)
            {
                Exec(_insertName, node.Name, node.Key, kind);
            }
        }

        foreach (Edge edge in edges)
        {
            Exec(_insertEdge, edge.Src, KindNames.Of(edge.Kind), edge.Dst, (int)edge.Confidence, fileId, edge.Line, edge.Props);
        }
    }

    public void Remove(string path, bool updateNames)
    {
        if (Scalar(_findFile, path) is not long id)
        {
            return;
        }

        if (updateNames)
        {
            Exec(_deleteNames, id);
        }

        Exec(_deleteFile, id);
    }

    private SqliteCommand Prepare(string sql, params string[] parameters)
    {
        SqliteCommand command = GraphStore.Command(_db, _tx, sql);
        foreach (string name in parameters)
        {
            command.Parameters.Add(new SqliteParameter { ParameterName = name });
        }

        command.Prepare();
        return command;
    }

    private static void Exec(SqliteCommand command, params object?[] values) => Bind(command, values).ExecuteNonQuery();

    private static object? Scalar(SqliteCommand command, params object?[] values) => Bind(command, values).ExecuteScalar();

    private static SqliteCommand Bind(SqliteCommand command, object?[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            command.Parameters[i].Value = values[i] ?? DBNull.Value;
        }

        return command;
    }
}
