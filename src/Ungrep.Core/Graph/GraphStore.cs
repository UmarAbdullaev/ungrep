using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Ungrep.Core.Graph;

/// <summary>
/// The graph database at `Library/ungrep/graph.db` (SPEC-graph-core). Indexers replace one
/// file's nodes and edges at a time; queries read nodes, names and edges. The database is a
/// cache: an unusable one is deleted and rebuilt, never migrated.
/// </summary>
public sealed class GraphStore : IDisposable
{
    public const int SchemaVersion = 1;
    public const string RelativePath = "Library/ungrep/graph.db";

    /// <summary>
    /// The meta key whose value <see cref="IndexRules"/> says the graph was indexed by the current
    /// rules: values whole (C1), declarations from their line, not their attributes' (B1e), and
    /// `calls` edges that say how a member is used, fields included (B1g). Named for the first.
    /// </summary>
    public const string ValuesKey = "values";
    public const string IndexRules = "whole, declaration lines, uses";

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
        CREATE VIRTUAL TABLE names USING fts5(
          name, key UNINDEXED, kind UNINDEXED,
          tokenize = 'unicode61 separators ''._'''
        );
        """ + Indexes;

    // Secondary indexes. A bulk write drops them and builds them again at commit: faster, and
    // indexes built from sorted input pack their pages (~15% smaller graph).
    internal const string Indexes = """

        CREATE INDEX edges_src  ON edges(src, kind);
        CREATE INDEX edges_dst  ON edges(dst, kind);
        CREATE INDEX edges_file ON edges(file_id);
        CREATE INDEX nodes_file ON nodes(file_id);
        CREATE INDEX nodes_kind ON nodes(kind, name);
        """;

    internal const string DropIndexes = """
        DROP INDEX edges_src; DROP INDEX edges_dst; DROP INDEX edges_file; DROP INDEX nodes_file; DROP INDEX nodes_kind;
        """;

    /// <summary>Where a full index is built before <see cref="Publish"/> copies it over the graph.</summary>
    public const string StagingRelativePath = "Library/ungrep/graph.db.new";

    /// <summary>How long <see cref="Open"/> waits for another writer by default.</summary>
    public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(1);

    private const string DatabaseName = "graph.db";
    private const string StagingName = "graph.db.new";

    private readonly SqliteConnection _db;
    private readonly string _projectRoot;
    private readonly string _directory;
    private readonly WriteLock? _lock;
    private readonly string? _publishTo;
    private SqliteTransaction? _bulk;
    private bool _disposed;

    private GraphStore(SqliteConnection db, string projectRoot, string directory, StoreMode mode, bool created, WriteLock? writeLock, string? publishTo)
    {
        _db = db;
        _projectRoot = projectRoot;
        _directory = directory;
        Mode = mode;
        Created = created;
        _lock = writeLock;
        _publishTo = publishTo;
    }

    public StoreMode Mode { get; }

    /// <summary>True when this open created the database (new, or rebuilt): it needs a full index.</summary>
    public bool Created { get; }

    /// <summary>
    /// Opens the graph. Read-only never waits. Read-write takes the write lock first and holds it
    /// until the store is disposed; it waits up to <paramref name="lockTimeout"/> (default 1 s).
    /// </summary>
    /// <exception cref="GraphRebuildRequiredException">Read-only, and there is no usable database.</exception>
    /// <exception cref="GraphLockedException">Read-write, and another writer kept the lock.</exception>
    public static GraphStore Open(string projectRoot, StoreMode mode, TimeSpan? lockTimeout = null, string? graphDirectory = null)
    {
        string root = Path.GetFullPath(projectRoot);
        string dir = GraphDirectory(root, graphDirectory);
        string path = Path.Combine(dir, DatabaseName);
        if (mode == StoreMode.ReadOnly)
        {
            return OpenReadOnly(path, root, dir);
        }

        WriteLock writeLock = Lock(dir, lockTimeout);
        try
        {
            return OpenFile(path, root, dir, writeLock, publishTo: null);
        }
        catch
        {
            writeLock.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Starts a full index in `graph.db.new`, holding the write lock throughout, while readers keep
    /// using the current graph. <see cref="Publish"/> makes it the graph; disposing without
    /// publishing throws the staged graph away.
    /// </summary>
    /// <exception cref="GraphLockedException">Another writer kept the lock.</exception>
    public static GraphStore OpenStaging(string projectRoot, TimeSpan? lockTimeout = null, string? graphDirectory = null)
    {
        string root = Path.GetFullPath(projectRoot);
        string dir = GraphDirectory(root, graphDirectory);
        WriteLock writeLock = Lock(dir, lockTimeout);
        try
        {
            string staging = Path.Combine(dir, StagingName);
            Delete(staging);
            return OpenFile(staging, root, dir, writeLock, publishTo: Path.Combine(dir, DatabaseName));
        }
        catch
        {
            writeLock.Dispose();
            throw;
        }
    }

    // `Library/ungrep/` unless the caller keeps the graph elsewhere (measuring a project
    // without writing into it).
    public static string GraphDirectory(string root, string? graphDirectory) =>
        graphDirectory is null ? Path.Combine(root, "Library", "ungrep") : Path.GetFullPath(graphDirectory);

    private static WriteLock Lock(string directory, TimeSpan? lockTimeout)
    {
        TimeSpan timeout = lockTimeout ?? DefaultLockTimeout;
        return WriteLock.TryAcquire(directory, timeout) ?? throw new GraphLockedException(timeout);
    }

    private static GraphStore OpenReadOnly(string path, string root, string directory)
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
                // Only when no writer is busy with it; otherwise the writer deals with it.
                using (WriteLock? writeLock = WriteLock.TryAcquire(directory, TimeSpan.Zero))
                {
                    if (writeLock is not null)
                    {
                        Delete(path);
                    }
                }

                throw new GraphRebuildRequiredException($"schema version {version ?? "missing"}, expected {SchemaVersion}");
            }
        }
        catch (SqliteException e)
        {
            db.Dispose();
            throw new GraphRebuildRequiredException("unreadable graph: " + e.Message);
        }

        return new GraphStore(db, root, directory, StoreMode.ReadOnly, created: false, writeLock: null, publishTo: null);
    }

    // Read-write open of `path` under a lock the caller already holds.
    private static GraphStore OpenFile(string path, string projectRoot, string directory, WriteLock writeLock, string? publishTo)
    {
        StoreMode mode = StoreMode.ReadWrite;
        Directory.CreateDirectory(directory);
        if (File.Exists(path))
        {
            SqliteConnection existing = Connect(path, SqliteOpenMode.ReadWrite);
            try
            {
                // A graph built by older index rules is rebuilt like one of another schema: one bulk
                // index takes seconds where indexing every file again one by one took minutes.
                if (ReadSchemaVersion(existing) == SchemaVersion.ToString(CultureInfo.InvariantCulture) && ReadRules(existing) == IndexRules)
                {
                    return new GraphStore(existing, projectRoot, directory, mode, created: false, writeLock, publishTo);
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
                (ValuesKey, IndexRules),
            })
            {
                using SqliteCommand insert = Command(db2, tx, "INSERT INTO meta(key, value) VALUES ($key, $value)");
                insert.Parameters.AddWithValue("$key", key);
                insert.Parameters.AddWithValue("$value", value);
                insert.ExecuteNonQuery();
            }

            tx.Commit();
        }

        return new GraphStore(db2, projectRoot, directory, mode, created: true, writeLock, publishTo);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _bulk?.Dispose();
        _db.Dispose();
        if (_publishTo is not null)
        {
            // Never published: the staged graph goes.
            Delete(Path.Combine(_directory, StagingName));
        }

        _lock?.Dispose();
    }

    /// <summary>
    /// Makes a staged graph (<see cref="OpenStaging"/>) the graph, then closes this store. The copy
    /// is one SQLite write transaction on `graph.db`, so a reader sees the old or the new graph.
    /// </summary>
    public void Publish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_publishTo is null)
        {
            throw new InvalidOperationException("only a store from OpenStaging can be published");
        }

        EnsureWritable();
        try
        {
            CopyTo(_publishTo);
        }
        catch (SqliteException)
        {
            // The current graph is corrupt (a mismatched version is simply overwritten).
            Delete(_publishTo);
            CopyTo(_publishTo);
        }

        Dispose();
    }

    private void CopyTo(string path)
    {
        using SqliteConnection target = Connect(path, SqliteOpenMode.ReadWriteCreate);
        _db.BackupDatabase(target);
    }

    /// <summary>
    /// Compares <paramref name="current"/> (every file the indexers track, as on disk now) with what
    /// was indexed. Size and mtime first; a file whose size is equal but mtime differs is hashed.
    /// </summary>
    /// <param name="current">Every file the indexer tracks, as on disk now.</param>
    /// <param name="indexer">Compare only the files this indexer owns; null compares all.</param>
    /// <exception cref="ArgumentException">A path appears twice.</exception>
    public FreshnessReport CheckFreshness(IEnumerable<FileStat> current, Indexer? indexer = null)
    {
        var indexed = new Dictionary<string, (long Size, long MtimeNs, byte[] Hash)>(StringComparer.Ordinal);
        using (SqliteCommand command = ReadCommand("SELECT path, size, mtime_ns, hash FROM files" + (indexer is null ? "" : " WHERE indexer = $indexer")))
        {
            if (indexer is { } only)
            {
                command.Parameters.AddWithValue("$indexer", IndexerName(only));
            }

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                indexed[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2), (byte[])reader.GetValue(3));
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var changed = new List<string>();
        var added = new List<string>();
        var touched = new List<FileStat>();
        foreach (FileStat stat in current)
        {
            if (!seen.Add(stat.Path))
            {
                throw new ArgumentException("duplicate path: " + stat.Path, nameof(current));
            }

            if (!indexed.TryGetValue(stat.Path, out var old))
            {
                added.Add(stat.Path);
            }
            else if (old.Size != stat.Size)
            {
                changed.Add(stat.Path);
            }
            else if (old.MtimeNs != stat.MtimeNs)
            {
                if (SameContent(stat.Path, old.Hash))
                {
                    touched.Add(stat);
                }
                else
                {
                    changed.Add(stat.Path);
                }
            }
        }

        List<string> removed = indexed.Keys.Where(p => !seen.Contains(p)).ToList();
        changed.Sort(StringComparer.Ordinal);
        added.Sort(StringComparer.Ordinal);
        removed.Sort(StringComparer.Ordinal);
        touched.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new FreshnessReport(changed, added, removed, touched);
    }

    private bool SameContent(string path, byte[] hash)
    {
        try
        {
            return FileHash.Bytes(Path.Combine(_projectRoot, path)).AsSpan().SequenceEqual(hash);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Gone or unreadable since it was listed: let the indexer find out.
            return false;
        }
    }

    /// <summary>Records new sizes and mtimes for files whose content did not change.</summary>
    public void UpdateStats(IReadOnlyList<FileStat> stats)
    {
        EnsureWritable();
        using SqliteTransaction tx = _db.BeginTransaction();
        using SqliteCommand update = Command(_db, tx, "UPDATE files SET size = $size, mtime_ns = $mtime WHERE path = $path");
        SqliteParameter size = update.Parameters.Add("$size", SqliteType.Integer);
        SqliteParameter mtime = update.Parameters.Add("$mtime", SqliteType.Integer);
        SqliteParameter path = update.Parameters.Add("$path", SqliteType.Text);
        foreach (FileStat stat in stats)
        {
            (size.Value, mtime.Value, path.Value) = (stat.Size, stat.MtimeNs, stat.Path);
            update.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// Takes the files hooks marked dirty (<see cref="DirtyFiles.Mark"/>) while another writer held
    /// the graph, and clears the list. The caller reindexes them.
    /// </summary>
    public IReadOnlyList<string> TakeDirty()
    {
        EnsureWritable();
        return DirtyFiles.Take(_directory);
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
        Execute(_db, _bulk, DropIndexes);
        return new BulkWriter(this, _db, _bulk);
    }

    internal void EndBulk() => _bulk = null;

    internal static string IndexerName(Indexer indexer) => indexer == Indexer.Code ? "code" : "asset";

    /// <summary>Keys of the nodes <paramref name="path"/> owns, ordered.</summary>
    public IReadOnlyList<string> KeysInFile(string path)
    {
        using SqliteCommand command = ReadCommand("SELECT n.key FROM nodes n JOIN files f ON f.id = n.file_id WHERE f.path = $path ORDER BY n.key");
        command.Parameters.AddWithValue("$path", path);
        return ReadStrings(command);
    }

    /// <summary>Indexed files whose path is, or ends in a folder then, <paramref name="fileName"/>, ordered.</summary>
    public IReadOnlyList<string> FilesNamed(string fileName)
    {
        using SqliteCommand command = ReadCommand("SELECT path FROM files WHERE path = $name OR substr(path, -length($name) - 1) = '/' || $name ORDER BY path");
        command.Parameters.AddWithValue("$name", fileName);
        return ReadStrings(command);
    }

    /// <summary>The record of an indexed file, or null when the graph does not hold it.</summary>
    public FileRecord? GetFile(string path)
    {
        using SqliteCommand command = ReadCommand("SELECT size, mtime_ns, hash, indexer, status FROM files WHERE path = $path");
        command.Parameters.AddWithValue("$path", path);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        string indexer = reader.GetValue(3).ToString()!;
        string status = reader.GetValue(4).ToString()!;
        return new FileRecord(
            path, reader.GetInt64(0), reader.GetInt64(1), Convert.ToHexString((byte[])reader.GetValue(2)).ToLowerInvariant(),
            indexer == "code" ? Indexer.Code : Indexer.Asset,
            status == "binary" ? FileStatus.Binary : status == "error" ? FileStatus.Error : FileStatus.Ok);
    }

    /// <summary>
    /// The graph's revision (SPEC-viewer): the first 12 hex characters of a SHA-256 over every
    /// indexed file's path and content hash, in path order. Only a reindex that changed, added or
    /// removed a file changes it.
    /// </summary>
    public string Revision()
    {
        using SqliteCommand command = ReadCommand("SELECT path, hash FROM files ORDER BY path");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            sha.AppendData(Encoding.UTF8.GetBytes(reader.GetString(0) + "\n"));
            sha.AppendData((byte[])reader.GetValue(1));
        }

        return Convert.ToHexString(sha.GetHashAndReset())[..12].ToLowerInvariant();
    }

    /// <summary>Indexed files whose path starts with <paramref name="prefix"/> (a folder path ending in `/`), ordered.</summary>
    public IReadOnlyList<string> FilesUnder(string prefix)
    {
        using SqliteCommand command = ReadCommand("SELECT path FROM files WHERE substr(path, 1, length($prefix)) = $prefix ORDER BY path");
        command.Parameters.AddWithValue("$prefix", prefix);
        return ReadStrings(command);
    }

    /// <summary>Files owning an edge of one of <paramref name="kinds"/> into any of <paramref name="keys"/>, ordered.</summary>
    public IReadOnlyList<string> FilesWithEdgesTo(IEnumerable<string> keys, EdgeKindSet kinds)
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);
        using SqliteCommand command = ReadCommand($"""
            SELECT DISTINCT f.path FROM edges e JOIN files f ON f.id = e.file_id
            WHERE e.dst = $key{KindFilter("e.kind", kinds.IsAll, kinds.Names)}
            """);
        SqliteParameter key = command.Parameters.Add("$key", SqliteType.Text);
        foreach (string k in keys)
        {
            key.Value = k;
            files.UnionWith(ReadStrings(command));
        }

        return files.ToList();
    }

    private static List<string> ReadStrings(SqliteCommand command)
    {
        var items = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(reader.GetString(0));
        }

        return items;
    }

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
            FROM (SELECT key FROM names
                  WHERE names MATCH $match{KindFilter("kind", kinds.IsAll, kinds.Names)}
                  ORDER BY lower(name) = lower($exact) DESC, kind, key
                  LIMIT $limit) s
            JOIN nodes n ON n.key = s.key JOIN files f ON f.id = n.file_id
            ORDER BY lower(n.name) = lower($exact) DESC, n.kind, n.key
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

    /// <summary>
    /// Edges arriving at the asset `a:<paramref name="guid"/>` or at any of its objects
    /// (`o:<paramref name="guid"/>:…`), ordered by kind, source, line.
    /// </summary>
    public Page<EdgeHit> EdgesIntoAsset(string guid, EdgeKindSet kinds, int limit)
    {
        // `;` follows `:` in ASCII, so the range is every key with the `o:<guid>:` prefix.
        using SqliteCommand command = EdgeCommand(
            "src", "(e.dst = $asset OR (e.dst >= $low AND e.dst < $high))" + KindFilter("e.kind", kinds.IsAll, kinds.Names), "e.kind, e.src", limit);
        command.Parameters.AddWithValue("$asset", NodeKeys.Asset(guid));
        command.Parameters.AddWithValue("$low", "o:" + guid + ":");
        command.Parameters.AddWithValue("$high", "o:" + guid + ";");
        return ReadEdges(command, limit);
    }

    /// <summary>Edges <paramref name="path"/> owns, ordered by line, source, kind, destination.</summary>
    public Page<EdgeHit> EdgesInFile(string path, EdgeKindSet kinds, int limit)
    {
        using SqliteCommand command = EdgeCommand("dst", "f.path = $path" + KindFilter("e.kind", kinds.IsAll, kinds.Names), "e.line, e.src, e.kind, e.dst", limit);
        command.Parameters.AddWithValue("$path", path);
        return ReadEdges(command, limit);
    }

    private Page<EdgeHit> Edges(string near, string far, string key, EdgeKindSet kinds, int limit)
    {
        using SqliteCommand command = EdgeCommand(far, $"e.{near} = $key{KindFilter("e.kind", kinds.IsAll, kinds.Names)}", $"e.kind, e.{far}", limit);
        command.Parameters.AddWithValue("$key", key);
        return ReadEdges(command, limit);
    }

    // `far` is the end whose node existence becomes EdgeHit.Resolved.
    private SqliteCommand EdgeCommand(string far, string where, string order, int limit)
    {
        CheckLimit(limit);
        SqliteCommand command = ReadCommand($"""
            SELECT e.src, e.kind, e.dst, e.confidence, f.path, e.line, e.props,
                   EXISTS (SELECT 1 FROM nodes n WHERE n.key = e.{far})
            FROM edges e JOIN files f ON f.id = e.file_id
            WHERE {where}
            ORDER BY {order}, e.line, f.path, e.props
            LIMIT $limit
            """);
        command.Parameters.AddWithValue("$limit", limit + 1);
        return command;
    }

    private static Page<EdgeHit> ReadEdges(SqliteCommand command, int limit) =>
        ReadPage(command, limit, r => new EdgeHit(
            new Edge(r.GetString(0), KindNames.ParseEdge(r.GetString(1)), r.GetString(2), (Confidence)r.GetInt32(3),
                r.GetString(4), r.GetInt32(5), r.IsDBNull(6) ? null : r.GetString(6)),
            r.GetBoolean(7)));

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

    // The index rules a graph was built by: before C1 it cut long values (signatures, message
    // targets, paths, override values, labels); before B1e a declaration started at its
    // attributes; before B1g `calls` edges had no `use` and fields none.
    private static string? ReadRules(SqliteConnection db)
    {
        using SqliteCommand read = Command(db, null, "SELECT value FROM meta WHERE key = $key");
        read.Parameters.AddWithValue("$key", ValuesKey);
        return read.ExecuteScalar() as string;
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
