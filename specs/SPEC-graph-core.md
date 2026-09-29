# Spec: graph-core

Part of unitygraph (see `../SPEC.md`). Status: **draft** (2026-09-29).

## Objective

One local store that every indexer writes into and every query reads from. It holds nodes
(code symbols, assets, objects inside assets), edges between them with a confidence tag, and
the file hashes that say whether the store is fresh.

graph-core knows nothing about C# or Unity YAML. It stores what `code-index` and `asset-index`
give it, replaces one file's contribution atomically, and answers lookups fast enough that a
`ug` query stays under its 500 ms p95 including process start.

Users of this module: `code-index`, `asset-index` (writers); `query`, `guards`, `project-map`,
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
| `instance_of` | component instance_of type (MonoBehaviour through its script GUID); prefab_instance instance_of prefab asset | exact |
| `places` | scene or prefab asset places prefab_instance | exact |
| `variant_of` | prefab asset variant_of prefab asset | exact |
| `overrides_value` | prefab_instance overrides a field on an object in the source prefab; `props.property_path` | exact |
| `references` | component or asset → asset or object, through a serialized field; `props.field` = field key | exact |
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
    public static GraphStore Open(string projectRoot, StoreMode mode);   // ReadOnly | ReadWrite
    public FreshnessReport CheckFreshness(IEnumerable<FileStat> current);
    public void ReplaceFile(FileRecord file, IReadOnlyList<Node> nodes, IReadOnlyList<Edge> edges);
    public void RemoveFile(string path);
    public BulkWriter BeginBulk();                                        // cold index
    public Node? GetNode(string key);
    public IReadOnlyList<Node> FindByName(string query, NodeKindSet kinds, int limit);
    public IReadOnlyList<Edge> EdgesFrom(string key, EdgeKindSet kinds, int limit);
    public IReadOnlyList<Edge> EdgesTo(string key, EdgeKindSet kinds, int limit);
}
```

- `ReplaceFile` runs in one transaction: delete the file's nodes and edges, insert the new ones,
  update `files` and `names`. A reader sees the old or the new state, never a mix.
- `CheckFreshness` compares size and mtime first and hashes only files whose size or mtime
  changed. It returns `changed`, `added` and `removed` lists; the caller decides what to reindex.
- Every list is ordered deterministically (kind, then key, then line) and capped by `limit`; the
  result says whether more exist.
- Values are immutable records. No static mutable state.

## Concurrency

- Readers never block: WAL lets `ug` queries read while a hook writes.
- One writer at a time: writers take `Library/unitygraph/write.lock` (an OS file lock, released
  if the process dies), then the SQLite write transaction. A hook that can't get the lock within
  1 s gives up and marks the file dirty in `Library/unitygraph/dirty.txt`; the next query picks
  it up (design rule 5).
- A full index writes into `graph.db.new` and swaps it in atomically at the end, so queries keep
  working on the old graph while a cold index runs.

## Performance targets

On a purge-squad-sized graph (estimate: ~40k nodes, ~200k edges):

| Operation | Target |
| --- | --- |
| Open read-only + one `GetNode` | ≤ 30 ms |
| `EdgesFrom` / `EdgesTo`, 100 results | ≤ 5 ms |
| `FindByName`, prefix, 20 results | ≤ 10 ms |
| `ReplaceFile`, one C# file (~300 nodes/edges) | ≤ 50 ms |
| `CheckFreshness`, ~3,000 files, nothing changed | ≤ 100 ms |
| Bulk insert of the whole graph | ≤ 5 s |
| Database size | ≤ 50 MB |

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

1. Sizes above are estimates. `code-index` step 2 records real counts on purge-squad; the
   targets are revisited then.
2. Whether `calls` from every method body is worth its size, or only calls between project types
   (not into `UnityEngine`/BCL). Decided in `SPEC-code-index.md` with measured numbers.
