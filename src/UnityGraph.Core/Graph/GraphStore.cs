using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;

namespace UnityGraph.Core.Graph;

/// <summary>
/// The graph database at `Library/unitygraph/graph.db` (SPEC-graph-core). Indexers replace one
/// file's nodes and edges at a time; queries read nodes, names and edges. The database is a
/// cache: an unusable one is deleted and rebuilt, never migrated.
/// </summary>
public sealed class GraphStore : IDisposable
{
    public const int SchemaVersion = 1;
    public const string RelativePath = "Library/unitygraph/graph.db";

    private const string Schema = """
        CREATE TABLE meta (
          key   TEXT PRIMARY KEY,
          value TEXT NOT NULL
        );
        CREATE TABLE files (
          id       INTEGER PRIMARY KEY,
          path     TEXT NOT NULL UNIQUE,
          size     INTEGER NOT NULL,
          mtime_ns INTEGER NOT NULL,
          hash     BLOB NOT NULL,
          indexer  TEXT NOT NULL,
          status   TEXT NOT NULL
        );
        CREATE TABLE nodes (
          key        TEXT PRIMARY KEY,
          kind       TEXT NOT NULL,
          name       TEXT NOT NULL,
          file_id    INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
          line_start INTEGER NOT NULL DEFAULT 0,
          line_end   INTEGER NOT NULL DEFAULT 0,
          props      TEXT
        ) WITHOUT ROWID;
        CREATE TABLE edges (
          src        TEXT NOT NULL,
          kind       TEXT NOT NULL,
          dst        TEXT NOT NULL,
          confidence INTEGER NOT NULL,
          file_id    INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
          line       INTEGER NOT NULL DEFAULT 0,
          props      TEXT
        );
        CREATE INDEX edges_src  ON edges(src, kind);
        CREATE INDEX edges_dst  ON edges(dst, kind);
        CREATE INDEX edges_file ON edges(file_id);
        CREATE INDEX nodes_file ON nodes(file_id);
        CREATE INDEX nodes_kind ON nodes(kind, name);
        CREATE VIRTUAL TABLE names USING fts5(
          name, key UNINDEXED, kind UNINDEXED,
          tokenize = 'unicode61 separators ''._'''
        );
        """;

    private readonly SqliteConnection _db;
    private SqliteTransaction? _bulk;

    private GraphStore(SqliteConnection db, StoreMode mode, bool created)
    {
        _db = db;
        Mode = mode;
        Created = created;
    }

    public StoreMode Mode { get; }

    /// <summary>True when this open created the database (new, or rebuilt): it needs a full index.</summary>
    public bool Created { get; }

    /// <exception cref="GraphRebuildRequiredException">Read-only, and there is no usable database.</exception>
    public static GraphStore Open(string projectRoot, StoreMode mode) =>
        OpenFile(Path.Combine(Path.GetFullPath(projectRoot), RelativePath), projectRoot, mode);

    internal static GraphStore OpenFile(string path, string projectRoot, StoreMode mode)
    {
        if (mode == StoreMode.ReadOnly)
        {
            if (!File.Exists(path))
            {
                throw new GraphRebuildRequiredException("no graph at " + path);
            }

            SqliteConnection db = Connect(path, SqliteOpenMode.ReadOnly);
            try
            {
                string? version = ReadSchemaVersion(db);
                if (version != SchemaVersion.ToString(CultureInfo.InvariantCulture))
                {
                    db.Dispose();
                    Delete(path);
                    throw new GraphRebuildRequiredException($"schema version {version ?? "missing"}, expected {SchemaVersion}");
                }
            }
            catch (SqliteException e)
            {
                db.Dispose();
                throw new GraphRebuildRequiredException("unreadable graph: " + e.Message);
            }

            return new GraphStore(db, mode, created: false);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            SqliteConnection existing = Connect(path, SqliteOpenMode.ReadWrite);
            try
            {
                if (ReadSchemaVersion(existing) == SchemaVersion.ToString(CultureInfo.InvariantCulture))
                {
                    return new GraphStore(existing, mode, created: false);
                }
            }
            catch (SqliteException)
            {
                // Corrupt or not a graph: rebuilt below.
            }

            existing.Dispose();
            Delete(path);
        }

        SqliteConnection db2 = Connect(path, SqliteOpenMode.ReadWriteCreate);
        using (SqliteTransaction tx = db2.BeginTransaction())
        {
            Execute(db2, tx, Schema);
            string tool = typeof(GraphStore).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            foreach (var (key, value) in new[]
            {
                ("schema_version", SchemaVersion.ToString(CultureInfo.InvariantCulture)),
                ("tool_version", tool),
                ("project_root", Path.GetFullPath(projectRoot)),
                ("created_by", "ug"),
            })
            {
                using SqliteCommand insert = Command(db2, tx, "INSERT INTO meta(key, value) VALUES ($key, $value)");
                insert.Parameters.AddWithValue("$key", key);
                insert.Parameters.AddWithValue("$value", value);
                insert.ExecuteNonQuery();
            }

            tx.Commit();
        }

        return new GraphStore(db2, mode, created: true);
    }

    public void Dispose()
    {
        _bulk?.Dispose();
        _db.Dispose();
    }

    /// <summary>
    /// Replaces everything <paramref name="file"/> contributed with <paramref name="nodes"/> and
    /// <paramref name="edges"/>, in one transaction: readers see the old or the new state.
    /// </summary>
    /// <exception cref="ArgumentException">A row is invalid: unknown kind, bad key, wrong owning file.</exception>
    public void ReplaceFile(FileRecord file, IReadOnlyList<Node> nodes, IReadOnlyList<Edge> edges)
    {
        EnsureWritable();
        Validate(file, nodes, edges);
        using SqliteTransaction tx = _db.BeginTransaction();
        using var writer = new RowWriter(_db, tx);
        writer.Write(file, nodes, edges, updateNames: true);
        tx.Commit();
    }

    /// <summary>Removes a file and everything it contributed; nothing happens if it isn't indexed.</summary>
    public void RemoveFile(string path)
    {
        EnsureWritable();
        using SqliteTransaction tx = _db.BeginTransaction();
        using var writer = new RowWriter(_db, tx);
        writer.Remove(path, updateNames: true);
        tx.Commit();
    }

    /// <summary>
    /// Starts a cold index: one transaction for every file, with the name index built once at
    /// <see cref="BulkWriter.Commit"/>. Other reads and writes on this store wait until it ends.
    /// </summary>
    public BulkWriter BeginBulk()
    {
        EnsureWritable();
        _bulk = _db.BeginTransaction();
        return new BulkWriter(this, _db, _bulk);
    }

    internal void EndBulk() => _bulk = null;

    public Node? GetNode(string key)
    {
        using SqliteCommand command = ReadCommand("""
            SELECT n.key, n.kind, n.name, f.path, n.line_start, n.line_end, n.props
            FROM nodes n JOIN files f ON f.id = n.file_id
            WHERE n.key = $key
            """);
        command.Parameters.AddWithValue("$key", key);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadNode(reader) : null;
    }

    /// <summary>
    /// Nodes whose name has words starting with each word of <paramref name="query"/> (split on
    /// spaces, `.` and `_`, case-insensitive). Exact name matches come first, then kind, then key.
    /// </summary>
    public Page<Node> FindByName(string query, NodeKindSet kinds, int limit)
    {
        CheckLimit(limit);
        string[] words = query.Split([' ', '\t', '.', '_'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return new Page<Node>([], false);
        }

        string match = string.Join(' ', words.Select(w => "\"" + w.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"*"));
        using SqliteCommand command = ReadCommand($"""
            SELECT n.key, n.kind, n.name, f.path, n.line_start, n.line_end, n.props
            FROM names s JOIN nodes n ON n.key = s.key JOIN files f ON f.id = n.file_id
            WHERE names MATCH $match{KindFilter("s.kind", kinds.IsAll, kinds.Names)}
            ORDER BY lower(n.name) = lower($exact) DESC, n.kind, n.key
            LIMIT $limit
            """);
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$exact", query.Trim());
        command.Parameters.AddWithValue("$limit", limit + 1);
        return ReadPage(command, limit, ReadNode);
    }

    /// <summary>Edges leaving <paramref name="key"/>, ordered by kind, destination, line.</summary>
    public Page<EdgeHit> EdgesFrom(string key, EdgeKindSet kinds, int limit) => Edges("src", "dst", key, kinds, limit);

    /// <summary>Edges arriving at <paramref name="key"/>, ordered by kind, source, line.</summary>
    public Page<EdgeHit> EdgesTo(string key, EdgeKindSet kinds, int limit) => Edges("dst", "src", key, kinds, limit);

    private Page<EdgeHit> Edges(string near, string far, string key, EdgeKindSet kinds, int limit)
    {
        CheckLimit(limit);
        using SqliteCommand command = ReadCommand($"""
            SELECT e.src, e.kind, e.dst, e.confidence, f.path, e.line, e.props,
                   EXISTS (SELECT 1 FROM nodes n WHERE n.key = e.{far})
            FROM edges e JOIN files f ON f.id = e.file_id
            WHERE e.{near} = $key{KindFilter("e.kind", kinds.IsAll, kinds.Names)}
            ORDER BY e.kind, e.{far}, e.line, f.path, e.props
            LIMIT $limit
            """);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$limit", limit + 1);
        return ReadPage(command, limit, r => new EdgeHit(
            new Edge(r.GetString(0), KindNames.ParseEdge(r.GetString(1)), r.GetString(2), (Confidence)r.GetInt32(3),
                r.GetString(4), r.GetInt32(5), r.IsDBNull(6) ? null : r.GetString(6)),
            r.GetBoolean(7)));
    }

    // Kind names come from the closed vocabulary, never from input, so inlining them is safe.
    private static string KindFilter(string column, bool all, IEnumerable<string> names) =>
        all ? "" : $" AND {column} IN ({string.Join(", ", names.Select(n => "'" + n + "'").DefaultIfEmpty("''"))})";

    private static Node ReadNode(SqliteDataReader r) =>
        new(r.GetString(0), KindNames.ParseNode(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5), r.IsDBNull(6) ? null : r.GetString(6));

    private static Page<T> ReadPage<T>(SqliteCommand command, int limit, Func<SqliteDataReader, T> read)
    {
        var items = new List<T>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(read(reader));
        }

        bool more = items.Count > limit;
        if (more)
        {
            items.RemoveAt(limit);
        }

        return new Page<T>(items, more);
    }

    private static void CheckLimit(int limit) => ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

    private SqliteCommand ReadCommand(string sql)
    {
        if (_bulk is not null)
        {
            throw new InvalidOperationException("a bulk write is in progress on this store");
        }

        return Command(_db, null, sql);
    }

    private void EnsureWritable()
    {
        if (Mode != StoreMode.ReadWrite)
        {
            throw new InvalidOperationException("the graph was opened read-only");
        }

        if (_bulk is not null)
        {
            throw new InvalidOperationException("a bulk write is in progress on this store");
        }
    }

    /// <exception cref="ArgumentException">Names the first invalid row.</exception>
    internal static void Validate(FileRecord file, IReadOnlyList<Node> nodes, IReadOnlyList<Edge> edges)
    {
        if (string.IsNullOrEmpty(file.Path) || file.Path.Contains('\\') || file.Path.StartsWith('/') || Path.IsPathRooted(file.Path))
        {
            throw new ArgumentException($"file path must be project-relative with forward slashes: '{file.Path}'", nameof(file));
        }

        if (file.Hash.Length != 64 || !file.Hash.All(char.IsAsciiHexDigitLower))
        {
            throw new ArgumentException($"{file.Path}: hash must be 64 lowercase hex characters", nameof(file));
        }

        if (!Enum.IsDefined(file.Indexer) || !Enum.IsDefined(file.Status))
        {
            throw new ArgumentException($"{file.Path}: unknown indexer or status", nameof(file));
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Node node in nodes)
        {
            string problem =
                !NodeKeys.IsValid(node.Key) ? "invalid key"
                : !keys.Add(node.Key) ? "duplicate key"
                : !Enum.IsDefined(node.Kind) ? $"unknown node kind {(int)node.Kind}"
                : string.IsNullOrEmpty(node.Name) ? "empty name"
                : node.File != file.Path ? $"owned by '{node.File}', not '{file.Path}'"
                : node.LineStart < 0 || node.LineEnd < 0 ? "negative line"
                : !IsProps(node.Props) ? "props must be a JSON object of at most 1024 characters"
                : "";
            if (problem.Length > 0)
            {
                throw new ArgumentException($"{file.Path}: node '{node.Key}': {problem}", nameof(nodes));
            }
        }

        foreach (Edge edge in edges)
        {
            string problem =
                !NodeKeys.IsValid(edge.Src) || !NodeKeys.IsValid(edge.Dst) ? "invalid key"
                : !Enum.IsDefined(edge.Kind) ? $"unknown edge kind {(int)edge.Kind}"
                : !Enum.IsDefined(edge.Confidence) ? $"unknown confidence {(int)edge.Confidence}"
                : edge.File != file.Path ? $"owned by '{edge.File}', not '{file.Path}'"
                : edge.Line < 0 ? "negative line"
                : !IsProps(edge.Props) ? "props must be a JSON object of at most 1024 characters"
                : "";
            if (problem.Length > 0)
            {
                throw new ArgumentException($"{file.Path}: edge '{edge.Src}' -> '{edge.Dst}': {problem}", nameof(edges));
            }
        }
    }

    private static bool IsProps(string? props) =>
        props is null || props.Length <= 1024 && props.StartsWith('{') && props.EndsWith('}');

    private static SqliteConnection Connect(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            // No pooling: a closed store must release the file so a rebuild can delete it.
            Pooling = false,
        }.ToString());
        connection.Open();
        var pragmas = new StringBuilder("PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;");
        if (mode != SqliteOpenMode.ReadOnly)
        {
            pragmas.Append(" PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        }

        Execute(connection, null, pragmas.ToString());
        return connection;
    }

    private static string? ReadSchemaVersion(SqliteConnection db)
    {
        using SqliteCommand command = Command(db, null, "SELECT value FROM meta WHERE key = 'schema_version'");
        return command.ExecuteScalar() as string;
    }

    private static void Delete(string path)
    {
        foreach (string file in new[] { path, path + "-wal", path + "-shm" })
        {
            File.Delete(file);
        }
    }

    internal static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql)
    {
        SqliteCommand command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        return command;
    }

    internal static void Execute(SqliteConnection db, SqliteTransaction? tx, string sql)
    {
        using SqliteCommand command = Command(db, tx, sql);
        command.ExecuteNonQuery();
    }
}
