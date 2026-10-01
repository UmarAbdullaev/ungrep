# Spec: graph-core

Part of unitygraph (see `../SPEC.md`). Status: **draft** (2026-09-29).

## Objective

One local store that every indexer writes into and every query reads from. It holds nodes
(code symbols, assets, objects inside assets), edges between them with a confidence tag, and
the file hashes that say whether the store is fresh.

graph-core knows nothing about C# or Unity YAML. It stores what `code-index` and `asset-index`
give it, replaces one file's contribution atomically, and answers lookups fast enough that a
`ug` query stays under its 500 ms p95 including process start.

Users of this module: `code-index`, `asset-index` (writers); `query`, `guards`,
`viewer` (readers).

## Scope

In:

- SQLite schema, creation, version check, rebuild on version mismatch.
- Stable node keys, node and edge kind vocabulary, confidence.
- Per-file ownership: replacing a file's nodes and edges in one transaction.
- Freshness: which tracked files changed, appeared or disappeared since they were indexed.
- Read primitives: node by key, name search, edges in and out, filtered by kind.
- Concurrency between a running query, a hook reindexing one file and a full index.

Out: parsing (indexers), answer formatting and budgets (`query`), anything that talks to Unity.

## Location and lifetime

- Database: `<UnityProject>/Library/unitygraph/graph.db`. `Library/` is ignored by every Unity
  `.gitignore` and is safe to delete; deleting it only costs a cold index.
- The database is a cache. Nothing in it is user data. Any doubt (version mismatch, corruption,
  failed migration) is resolved by deleting it and rebuilding.
- SQLite settings: `journal_mode=WAL`, `synchronous=NORMAL`, `busy_timeout=5000`,
  `foreign_keys=ON`.

## Node keys

Every node has a string key that is stable across reindexing, so an edge can point at a node in
another file without that file being reindexed at the same time.

| Prefix | Meaning | Example |
| --- | --- | --- |
| `cs:` + Roslyn documentation ID | C# symbol | `cs:T:Purge.Lab.LabStage`, `cs:M:Purge.Lab.LabStage.Open(System.Int32)`, `cs:F:Purge.Lab.LabStage._stageGun` |
| `a:` + GUID | Asset with a `.meta` file | `a:4f1c0d2b9e8a4c6d8b7a6f5e4d3c2b1a` |
| `o:` + GUID + `:` + fileID | Object inside an asset (GameObject, component, prefab instance, sub-asset) | `o:4f1c...2b1a:1845920371` |
| `p:` + project-relative path | File without a GUID (e.g. `.csproj`, `ProjectSettings/*.asset`) | `p:ProjectSettings/TagManager.asset` |

Rules: keys are case-sensitive and compared ordinally. Documentation IDs come from Roslyn
(`ISymbol.GetDocumentationCommentId()`), so `code-index` and `asset-index` can both produce the
same key for a type: `asset-index` resolves a MonoScript GUID to a type name through the
`script_defines` edge that `code-index` writes (see Edge kinds).

## Node kinds

| Kind | Written by | Notes |
| --- | --- | --- |
| `type` | code-index | `props.subkind`: class, struct, interface, enum, delegate, record |
| `method` | code-index | includes constructors and operators; local functions are not nodes |
| `field` | code-index | `props.serialized`: true/false by Unity's rules |
| `property` | code-index | `props.serialized` for `[field: SerializeField]` |
| `event` | code-index | |
| `asset` | asset-index | `props.asset_type`: script, scene, prefab, scriptable_object, material, shader, animator_controller, animation_clip, input_actions, asmdef, texture, audio, model, other |
| `game_object` | asset-index | `props.path`: hierarchy path, e.g. `HubStation/LabStage/Turntable` |
| `component` | asset-index | `props.class_id`: Unity class ID; `props.script`: type key for MonoBehaviours |
| `prefab_instance` | asset-index | a nested prefab placed in a scene or prefab |
| `sub_asset` | asset-index | objects inside an asset that are not GameObjects or components (animator states, SO sub-assets) |

Every node row also has: `name` (short, used by search), `file_id` (owning file), `line_start`,
`line_end` (1-based; 0 when unknown) and `props` (JSON, at most ~512 bytes; bigger data stays in
the source file and is read on demand).

## Edge kinds

Direction reads left to right: `src kind dst`.

| Kind | Example | Usual confidence |
| --- | --- | --- |
| `contains` | type contains method; asset contains game_object; game_object contains component | exact |
| `child_of` | game_object child_of game_object (Transform parent) | exact |
| `inherits` | type inherits type | exact |
| `implements` | type implements interface | exact |
| `overrides` | method overrides method | exact |
| `calls` | method calls method | exact (Roslyn) |
| `uses_type` | method or field uses type | exact |
| `dispatches` | method → method through `SendMessage`, `Invoke`, `StartCoroutine("name")`, `nameof`-free strings | heuristic |
| `script_defines` | script asset defines type | exact |
| `instance_of` | component or ScriptableObject instance_of its script asset (the type is one `script_defines` hop away, so a class rename needs no asset reindex); prefab_instance instance_of prefab asset | exact |
| `places` | scene or prefab asset places prefab_instance | exact |
| `variant_of` | prefab asset variant_of prefab asset | exact |
| `overrides_value` | prefab_instance overrides a field on an object in the source prefab; `props.property_path` | exact |
| `references` | component or asset → asset or object, through a serialized field; `props.field` = the field path in the file (`_waves[2].prefab`) | exact |
| `event_calls` | component → method through a persistent UnityEvent listener | exact when the target type has one method of that name, heuristic otherwise |
| `anim_event_calls` | animation_clip → method named in an animation event | heuristic |
| `in_assembly` | script asset in_assembly asmdef asset | exact |
| `assembly_refs` | asmdef → asmdef | exact |
| `addressable` | asset is addressable; `props.address`, `props.group` | exact |

The vocabulary is closed: adding a kind is a change to this spec (Ask first after v1, per
`../SPEC.md` Boundaries). Unknown kinds are rejected on write, so a typo in an indexer fails a
test instead of silently producing an edge no query asks for.

Edges point at keys, not row ids. An edge whose `dst` key has no node (the target file isn't
indexed, or the symbol was deleted) is kept; readers report it as `unresolved`. It resolves by
itself once the target is indexed.

## Schema (version 1)

```sql
CREATE TABLE meta (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);  -- schema_version, tool_version, project_root, created_by

CREATE TABLE files (
  id         INTEGER PRIMARY KEY,
  path       TEXT NOT NULL UNIQUE,   -- project-relative, forward slashes
  size       INTEGER NOT NULL,
  mtime_ns   INTEGER NOT NULL,
  hash       BLOB NOT NULL,          -- SHA-256 of the content
  indexer    TEXT NOT NULL,          -- 'code' | 'asset'
  status     TEXT NOT NULL           -- 'ok' | 'binary' | 'error'
);

CREATE TABLE nodes (
  key        TEXT PRIMARY KEY,
  kind       TEXT NOT NULL,
  name       TEXT NOT NULL,
  file_id    INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
  line_start INTEGER NOT NULL DEFAULT 0,
  line_end   INTEGER NOT NULL DEFAULT 0,
  props      TEXT                    -- JSON object or NULL
) WITHOUT ROWID;

CREATE TABLE edges (
  src        TEXT NOT NULL,
  kind       TEXT NOT NULL,
  dst        TEXT NOT NULL,
  confidence INTEGER NOT NULL,       -- 1 = exact, 2 = heuristic
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
```

Notes:

- The owning file is the file whose parsing produced the row. An edge from a scene to a script's
  type belongs to the scene: reindexing the script never touches it.
- `names` is kept in step with `nodes` by the store (not triggers), so a bulk index can fill it
  once at the end.
- `schema_version` mismatch → the store deletes the database and reports `rebuild required`;
  there are no migrations before v1.

## Public surface (`UnityGraph.Core`)

```csharp
public sealed class GraphStore : IDisposable
{
    // graphDirectory: default <root>/Library/unitygraph; elsewhere only to measure a project read-only.
    public static GraphStore Open(string projectRoot, StoreMode mode, TimeSpan? lockTimeout = null, string? graphDirectory = null);
    public static GraphStore OpenStaging(string projectRoot, TimeSpan? lockTimeout = null, string? graphDirectory = null); // full index
    public void Publish();                                                // staged graph becomes the graph
    public bool Created { get; }                                          // this open made a new database
    public FreshnessReport CheckFreshness(IEnumerable<FileStat> current, Indexer? indexer = null); // only that indexer's files
    public void UpdateStats(IReadOnlyList<FileStat> stats);               // record touched-only files
    public IReadOnlyList<string> TakeDirty();                             // and clear dirty.txt
    public void ReplaceFile(FileRecord file, IReadOnlyList<Node> nodes, IReadOnlyList<Edge> edges);
    public void RemoveFile(string path);
    public BulkWriter BeginBulk();                                        // cold index
    public IReadOnlyList<string> KeysInFile(string path);                 // node keys a file owns
    public IReadOnlyList<string> FilesWithEdgesTo(IEnumerable<string> keys, EdgeKindSet kinds); // sorted
    public Node? GetNode(string key);
    public Page<Node> FindByName(string query, NodeKindSet kinds, int limit);
    public Page<EdgeHit> EdgesFrom(string key, EdgeKindSet kinds, int limit);
    public Page<EdgeHit> EdgesTo(string key, EdgeKindSet kinds, int limit);
}

public sealed record Page<T>(IReadOnlyList<T> Items, bool More);
public sealed record EdgeHit(Edge Edge, bool Resolved);                 // Resolved: the far end has a node
public sealed record FileStat(string Path, long Size, long MtimeNs);     // FileStat.Of(root, path)
public sealed record FreshnessReport(IReadOnlyList<string> Changed, IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed, IReadOnlyList<FileStat> Touched);
public static class DirtyFiles { public static bool Mark(string projectRoot, string path); }
public static class FileHash { public static string Of(string fullPath); }  // SHA-256, lowercase hex
```

- Kinds are enums (`NodeKind`, `EdgeKind`) stored by their snake_case names; `NodeKeys` builds
  keys. Rows are validated before any write (relative forward-slash paths, 64-hex hashes, known
  kinds, valid keys, nodes owned by the file, props a JSON object ≤ 1 KB); a bad row throws
  `ArgumentException` and nothing is written. The indexers write through `Contributions`,
  which turns that into a problem line and keeps the file's record without rows, so one odd
  file never stops an index (fail open).
- A read-only open with no graph, a corrupt one or another schema version throws
  `GraphRebuildRequiredException` (a mismatched file is deleted first); a read-write open
  rebuilds it empty and sets `Created`.
- `FindByName` splits the query on whitespace, `.` and `_`, matches every word as a prefix, and
  puts exact name matches (case-insensitive) first, then kind, then key.
- During a bulk write, other reads and writes on the same store throw.

- `ReplaceFile` runs in one transaction: delete the file's nodes and edges, insert the new ones,
  update `files` and `names`. A reader sees the old or the new state, never a mix.
- `CheckFreshness` compares size and mtime first and hashes only files whose size is equal but
  mtime changed (a size change is a change). It returns `changed`, `added` and `removed` lists;
  the caller decides what to reindex. Files with a new mtime but the same content come back as
  `touched`; a writer records them with `UpdateStats` so they aren't hashed again. A file that
  can't be read while hashing counts as changed.
- Every list is ordered deterministically (kind, then key, then line) and capped by `limit`; the
  result says whether more exist.
- Values are immutable records. No static mutable state.

## Concurrency

- Readers never block: WAL lets `ug` queries read while a hook writes.
- One writer at a time: a read-write store holds `Library/unitygraph/write.lock` (a share-none
  handle on Windows, flock on macOS/Linux, so it goes with the process) from open to dispose,
  and each write is one SQLite transaction. `Open` waits up to `lockTimeout` (1 s) and then
  throws `GraphLockedException`; a hook catches it and calls `DirtyFiles.Mark`, which appends
  to `Library/unitygraph/dirty.txt`; the next writer calls `TakeDirty` and reindexes them
  (design rule 5).
- A full index writes into `graph.db.new` (`OpenStaging`, holding the write lock throughout),
  so queries keep working on the old graph while a cold index runs. `Publish` copies it into
  `graph.db` with SQLite's backup API: one write transaction, so a reader sees the old or the
  new graph. A file rename was rejected: a leftover `graph.db-wal` of the old file would be
  replayed into the new one, and on Windows SQLite holds `graph.db` open without share-delete,
  so the rename fails while any query runs. Disposing an unpublished staging store deletes
  `graph.db.new`.
- A read-only open that finds another schema version deletes the file only if it can take the
  write lock at once; otherwise a writer is busy and it only reports "rebuild required".

## Performance targets

On a purge-squad-sized graph (estimate: ~40k nodes, ~200k edges):

| Operation | Target | Measured (T8) |
| --- | --- | --- |
| Open read-only + one `GetNode` | ≤ 30 ms | 1–2 ms |
| `EdgesFrom` / `EdgesTo`, 100 results | ≤ 5 ms | 0.1 / 0.4–0.7 ms |
| `FindByName`, prefix, 20 results | ≤ 10 ms | 0.2–0.3 ms |
| `ReplaceFile`, one C# file (~300 nodes/edges) | ≤ 50 ms | 16–18 ms |
| `CheckFreshness`, ~3,000 files, nothing changed | ≤ 100 ms | 3–5 ms |
| Bulk insert of the whole graph | ≤ 5 s | 1.3–1.7 s (+0.4 s publish) |
| Database size | ≤ 50 MB | 49.3 MB |

Measured 2026-09-29 on the dev machine (Windows 11, Release, median of 21 runs) with the
generated graph in `tests/UnityGraph.Core.Tests/SyntheticGraph.cs`: 3,000 files, 39,800 nodes,
203,400 edges, with hub types and methods. Also checked, not a target: a 4-letter prefix that
matches 15,600 nodes takes ~9 ms (bound 25 ms). Size is the tightest: the bulk writer drops
the secondary indexes and builds them at commit (packed pages, 52.0 → 49.3 MB, and bulk
insert 3.0 → 1.5 s). If real purge-squad counts exceed the estimate, size is the target to
revisit first (keys are stored as text in `edges` and in both edge indexes).

## Testing

- Unit: key builders; kind validation (unknown kind rejected); `ReplaceFile` replaces only its
  own file's rows; edges to missing nodes come back `unresolved` and resolve after the target is
  added; deterministic ordering; `limit` and the "more" flag.
- Freshness: touch without change (mtime only) → not changed after hashing; content change →
  changed; new file → added; deleted file → removed.
- Concurrency: a reader running during `ReplaceFile` sees old or new, never partial; two writers
  serialize; a killed writer releases the lock.
- Version: a database with another `schema_version` is rebuilt, not read.
- Performance: a generated graph at the sizes above, checked against the targets in CI (Release
  build, with a margin for slow CI machines).
- Coverage: ≥ 80% lines.

## Boundaries

- Always: one transaction per file; deterministic output; everything under
  `Library/unitygraph/`.
- Ask first: new node or edge kinds; any column change after v1; a second database file.
- Never: write outside `Library/unitygraph/`; store file contents beyond `props`; network.

## Open points

1. Sizes above are estimates. Code measured 2026-09-29: 24,402 nodes, 32,680 edges, 21 MB on
   purge-squad (`SPEC-code-index.md`, Measured), well under the estimate. Revisit once
   asset-index adds its nodes and edges.
2. ~~Whether `calls` from every method body is worth its size.~~ Settled 2026-09-29: only calls
   to symbols declared in indexed files; calls into `UnityEngine`/BCL/packages are dropped
   (purge-squad: 1,765 dropped vs 2,966 kept). See `SPEC-code-index.md`, Measured.
