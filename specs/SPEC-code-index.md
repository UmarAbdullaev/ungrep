# Spec: code-index

Part of unitygraph (see `../SPEC.md`). Status: **draft** (2026-09-29). Writes into the store
described in `SPEC-graph-core.md`.

## Objective

Turn a Unity project's C# into graph nodes and edges with Roslyn, from the `.csproj` files Unity
generates, without MSBuild and without Unity running. Every symbol edge is exact (Roslyn bound
it); string dispatch is the only heuristic. A full index of purge-squad takes at most 60 s, and
reindexing one edited file at most 1 s including process start.

Users: `query` (`ug find/refs/explain`, reindex on edit), `asset-index` (MonoScript GUID → type
through `script_defines`), `guards` (serialized fields and their former names).

## Scope

In:

- Reading Unity's generated legacy `.csproj` files: `Compile`, `Reference`/`HintPath`,
  `ProjectReference`, `DefineConstants`, `LangVersion`, `AllowUnsafeBlocks`.
- One `CSharpCompilation` per project, project references as compilation references (compiled
  from source, never from `Library/ScriptAssemblies`, which can be stale). The exception is a
  package project whose `Compile` items are all under `Library/` (PackageCache code can't
  change): its `Library/ScriptAssemblies/<Assembly>.dll` is used when it exists.
- Nodes for types, methods, fields, properties, events declared in indexed files.
- Edges `contains`, `inherits`, `implements`, `overrides`, `calls`, `uses_type`, `dispatches`,
  `script_defines`.
- Unity facts in props: serialized fields by Unity's rules, former serialized names, engine
  message methods, editor entry points.
- Full index and incremental reindex of changed files, including files whose callers' edges
  a rename left pointing at a removed key.
- `ug index` (CLI), used by people, tests and the first query of a session.

Out: YAML and `.meta` files other than a script's own `.meta` GUID (`asset-index`); formatting
answers (`query`); source generators and analyzers (not run); a project without generated
`.csproj` files (reported, see Failure handling).

## Which files are indexed

- Projects: every `*.csproj` in the project root. A project is **loaded** when an indexed file
  is in it or it is referenced (directly or not) by one that is.
- Files: a project's `Compile` items under `Assets/` or `Packages/` (embedded packages), plus
  `.cs` files on disk under `Assets/` and `Packages/*/` that no project lists yet (folders
  starting with `.` or ending in `~` are skipped, as Unity skips them). Items
  under `Library/` (PackageCache) are compiled for binding but not indexed: their symbols have
  no nodes, and edges to them are dropped (see `calls` scope).
- **Vendored** files (third-party code checked into `Assets/`) get declarations only: nodes and
  `contains`, `inherits`, `implements`, `overrides`, `script_defines`. No `calls`, `uses_type` or
  `dispatches` from their bodies. On purge-squad that is 787 of 864 `Assembly-CSharp` files
  (Feel). A file is vendored when its path starts with an entry of the `vendored` setting, or,
  when the setting is absent, with one of these defaults (case-insensitive, whole folder names):
  `Assets/Plugins/`, `Assets/3rd Party/`, `Assets/3rdParty/`, `Assets/Third Party/`,
  `Assets/ThirdParty/`, `Assets/Vendor/`, `Assets/AssetStore/`, `Packages/`.
  The same list is what `guards` protects (SPEC Boundaries: never edit vendored folders).

Config addition (`.claude/unitygraph.json`, read by `UnityGraphConfig`):

```jsonc
{
  "vendored": ["Assets/Plugins/", "Assets/3rd Party/", "Assets/Tools/Imported/"] // replaces the defaults
}
```

Entries are project-relative folder prefixes with forward slashes, ending in `/`; anything else
is a config error that `ug doctor` names.

`docs` (editor-link, SPEC-editor-ops rule 11): `{ "docs": { "online": false } }` stops `ug api` and
`ug docs` from fetching pages from docs.unity3d.com; the default is `true`.

## Loading a project

- Parse the XML directly. `Compile Include` paths use backslashes and may be XML-escaped
  (`&apos;`); they are normalised to project-relative forward-slash paths. `Analyzer` items and
  `None` items are ignored.
- References: `HintPath` (absolute, or relative to the project root). A missing reference file
  is skipped and counted. If none of `mscorlib`, `netstandard` or `System.Runtime` resolves (the
  editor moved, or a test fixture), the running .NET runtime's assemblies are added instead so
  `System.Object` binds; doc IDs of BCL types are the same either way.
- Parse options: `LangVersion` from the project (default 9.0), preprocessor symbols from
  `DefineConstants`, `DocumentationMode.None`. Compilation: `OutputKind.DynamicallyLinkedLibrary`,
  `AllowUnsafe` from the project, concurrent build on.
- Metadata references are shared between projects (one `MetadataReference` per file path).
- A compilation references the transitive closure of its `ProjectReference`s (Unity compiles
  against referenced assemblies' references too; `Assembly-CSharp-Editor` sees types that
  `Assembly-CSharp` exposes from an asmdef it references).
- Projects form a DAG in `ProjectReference`; a cycle is an error for the projects in it (their
  files are indexed without the cyclic reference).

## Node rules

| Symbol | Kind | `name` | Lines | `props` |
| --- | --- | --- | --- | --- |
| class, struct, interface, enum, delegate, record | `type` | metadata name without arity (`List`) | declaration | `subkind`, `access`, `static`/`abstract`/`sealed` when true, `generic` arity when > 0, `unity` (see below) |
| method, operator, conversion, destructor | `method` | symbol name | declaration | `sig` (e.g. `Open(int, bool)`, whole), `access`, `static`/`virtual`/`abstract`/`override`/`async` when true, `message` / `entry` (see below) |
| constructor | `method` | the type's name | declaration | as method, plus `subkind: constructor` |
| field, enum member | `field` | field name | declarator | `type` (display string, whole), `access`, `static`/`const`/`readonly` when true, `serialized`, `serialize_reference`, `former` (list) |
| property, indexer | `property` | property name (`this[]` for indexers) | declaration | `type`, `access`, `serialized` for `[field: SerializeField]`, `former` |
| event | `event` | event name | declaration | `type`, `access` |

- Keys: `NodeKeys.Symbol(symbol.GetDocumentationCommentId())` on the original definition.
  Symbols without a doc ID (local functions, lambdas, anonymous types) are not nodes; code
  inside them belongs to the enclosing member.
- Partial types: the type node is owned by the part in the file named like the type
  (`LabStage.cs` over `LabStage.Debug.cs`), else by the part in the ordinally first file path;
  every part's file owns the members it declares and their `contains` edges. The rule only
  needs the compilation, so a single-file reindex gets the same owner.
- Partial methods: the implementing declaration is the node.
- Explicit interface implementations are named by the member name (`Dispose`, not
  `System.IDisposable.Dispose`); the key keeps the full doc ID.
- Nested types: `contains` from the outer type.
- Lines are 1-based, from the declaration's span without its attribute lists and leading trivia:
  the line that declares it (B1e, 2026-10-11: `[Serializable]` above a class put the class on the
  attribute's line). A field or event field spans its whole declaration statement, so `int a, b;`
  gives both the same lines.
- `unity` on a type: `mono_behaviour`, `scriptable_object`, `editor`, `editor_window`,
  `property_drawer` or `object` (another `UnityEngine.Object`), from the base type chain;
  `serializable` for `[Serializable]` classes and structs. Absent otherwise.
- `message: true` on a method of a `MonoBehaviour` (or `ScriptableObject`, `Editor`,
  `EditorWindow`) subclass whose name is a Unity message (`Awake`, `Start`, `Update`,
  `FixedUpdate`, `LateUpdate`, `OnEnable`, `OnDisable`, `OnDestroy`, `OnValidate`, `Reset`,
  `OnTrigger*`, `OnCollision*`, `OnGUI`, `OnDrawGizmos*`, the full list in code, table-tested).
- `entry` on a method: the attribute that makes the editor or runtime call it, one of
  `MenuItem`, `ContextMenu`, `RuntimeInitializeOnLoadMethod`, `InitializeOnLoadMethod`,
  `DidReloadScripts`, `OnOpenAsset`, `PostProcessBuild`. `entry: InitializeOnLoad` on a type.

### Serialized fields (Unity's rules)

A field is `serialized: true` when all hold:

1. The containing type is a `UnityEngine.Object` subclass, or a class or struct marked
   `[Serializable]`.
2. It is an instance field: not `static`, `const` or `readonly`.
3. It is `public`, or has `[SerializeField]` or `[SerializeReference]`.
4. It has no `[NonSerialized]`.
5. Its type is serializable: a primitive, `string`, an enum, a `UnityEngine.Object` subclass, a
   Unity value type (`Vector2/3/4`, `Vector2Int/3Int`, `Quaternion`, `Color`, `Color32`, `Rect`,
   `RectInt`, `Bounds`, `BoundsInt`, `Matrix4x4`, `LayerMask`, `AnimationCurve`, `Gradient`,
   `RectOffset`, `GUIStyle`, `Hash128`), a `[Serializable]` class or struct (including closed
   generics), or a one-dimensional array or `List<T>` of one of those (not of an array or list).
   With `[SerializeReference]` the type may also be an interface or abstract class, and
   `serialize_reference: true` is set. Not serializable: dictionaries, sets, delegates,
   multi-dimensional and jagged arrays, nested collections, `object`, and open generic type
   parameters (Unity decides those per closed type; v1 reports them as not serialized).

`[field: SerializeField]` on an auto-property marks the property `serialized: true`.
`[FormerlySerializedAs("x")]` values go in `former`, sorted. Every rule has a row in the
serialization table test (SPEC Testing strategy).

## Edge rules

| Edge | Source → target | Owned by | Line |
| --- | --- | --- | --- |
| `contains` | type → member or nested type | the member's file | member's first line |
| `inherits` | type → base class (not `object`); interface → interface it extends | file of that base list | base list |
| `implements` | type → each interface in its own base list | file of that base list | base list |
| `overrides` | method/property/event → overridden member, and explicit or implicit interface implementations of it | member's file | member's first line |
| `calls` | enclosing member → invoked method, constructor, property accessor (as the property), event add/remove (as the event), field read or written (as the field), method named without a call (a method group); how, in `use` (Uses, below) | call site's file | call site |
| `uses_type` | enclosing member → type in `typeof`, `GetComponent<T>`-style type arguments, `new T()`, casts, `is`/`as`, declarations of locals, parameters and fields; `[RequireComponent(typeof(T))]` from the type | usage's file | usage |
| `dispatches` | enclosing member → method named by a string | call site's file | call site |
| `script_defines` | `a:<guid>` of `<file>.cs.meta` → the file's top-level type named like the file | the `.cs` file | type's first line |

- **Targets:** only symbols declared in indexed files (own or vendored) get `calls` and
  `uses_type` edges. Calls into `UnityEngine`, the BCL and package code are dropped. This
  settles SPEC-graph-core open point 2 (see Measured): keeping them would add ~60% more `calls`
  edges and a node per engine or BCL target, and no v1 query asks "who calls `Debug.Log`".
- Implicit (compiler-generated) constructors have no node and get no `calls` edge; `new T()`
  still gives `uses_type T`.
- Generic targets point at the original definition (`List<int>.Add` → `List`1.Add`), so edges
  land on nodes.
- One edge per (src, kind, dst, props) per file, at the first line it occurs: the graph answers
  "who calls X", not "how often". A member that reads and writes a field has two edges; one that
  reads it in three places has one.
- **Uses (B1g, 2026-10-11).** A `calls` edge that is not a plain call says how, in props.
  `use`:
  - `read` / `write`: a field or property read, or written: `=`, a compound assignment, `++` and
    `--` (each a write), an `out` or `ref` argument, an object initializer, a deconstruction.
  - `subscribe` / `unsubscribe` / `raise`: an event, a field or property of a delegate type, or
    one of a type derived from `UnityEngine.Events.UnityEventBase`: `+=` / `-=`;
    `AddListener` / `RemoveListener`, and `RemoveAllListeners` (no handler); invoking it
    (`E(x)`, `E.Invoke(x)`, `E?.Invoke(x)`) raises. `=` on a delegate is a `write`.
  - `delegate`: a method named without being called (`list.Sort(Compare)`, `Action a = Stop`).
  - none: a call.

  A subscription names its handler: `handler` is the handler method's key for a method group of
  an indexed method, `lambda` for a lambda or anonymous method. The method group gets its own
  edge, from the same member, with the same `use` and `event`: the event's key, or `Type.member`
  when the event is not indexed (`Button.onClick`). So `_health.Died += OnDied` in `Awake` gives
  `Awake → Health.Died {use: subscribe, handler: OnDied}` and `Awake → OnDied {use: subscribe,
  event: Health.Died}`. Before B1g the handler's edge was a plain call, fields had no `calls`
  edges, and `ug refs` on an event listed `+=`, `-=` and the raise alike as `called from`
  (N3's agent run grepped `Died [+-]=` for the handlers).
- A member's own type (return type, field type) is a `uses_type` from the member.
- **Dispatch (heuristic unless noted):** a string literal or `const` string argument to
  `SendMessage`, `SendMessageUpwards`, `BroadcastMessage`, `Invoke`, `InvokeRepeating`,
  `CancelInvoke`, `IsInvoking`, `StartCoroutine`, `StopCoroutine` declared on `Component`,
  `MonoBehaviour` or `GameObject`. For `Invoke*` and `*Coroutine` the receiver's static type (or
  `this`) and its base types are searched (an override hides the method it overrides), and a
  single match is `exact`; `nameof(M)` is always exact. `SendMessage` family: every method with
  that name on an indexed `MonoBehaviour` subclass in the caller's compilation or one it
  references (a message can't reach a type the caller's assembly can't see), `heuristic`, every
  target in key order (no cap since C1; a graph built before is indexed again
  on its first write open, see SPEC-graph-core). No match → no edge.
- **`script_defines`:** only when the `.meta` exists, has a `guid:` line, and the file declares
  a top-level (non-nested) class whose name equals the file name; the asset node itself comes
  from `asset-index` (the edge is unresolved until then).

## Incremental reindex

Tracked files: the indexed `.cs` files. `.csproj` files are not tracked: Unity rewrites them on
every refresh, and each `Update` re-reads them anyway (loading is cheap next to compiling).

- `.cs` changed or added → reindex it. `.cs` removed → `RemoveFile`.
- A reindexed file whose declared keys lost some (removed or renamed members): the files with
  `calls`, `uses_type`, `overrides`, `inherits`, `implements` or `dispatches` edges into the lost
  keys are reindexed too, so their edges don't keep pointing at the old names. Added keys do
  not trigger this (edges to a new key already exist as unresolved ones only if something
  named it).
- A new `.cs` file not yet in any `.csproj` (Unity regenerates them only on its next refresh) is
  added to the project whose files share the longest folder below `Assets/` or
  `Packages/<name>/` with it, or `Assembly-CSharp` when none does.
- A file that moved between projects without changing is not reindexed; its edges are correct
  again after its next edit or `ug index --full`.
- Reindex builds the compilation of the file's project and its references from the files on
  disk; only the reindexed files are walked for edges.

## Public surface (`UnityGraph.Code`)

```csharp
public sealed class CodeWorkspace                                   // the loaded projects
{
    public static CodeWorkspace Load(string projectRoot, VendoredFolders vendored);
    public bool HasProjects { get; }                                  // false: no .csproj in the root
    public IReadOnlyList<string> TrackedFiles { get; }               // indexed .cs files, sorted
    public IReadOnlyList<string> Problems { get; }                    // missing references, cycles; sorted
    public bool IsVendored(string path);
    public FileContribution Index(string path);                       // one tracked file
    public IReadOnlyList<FileContribution> IndexMany(IEnumerable<string>? paths = null); // all when null; parallel, path order
}

public sealed record FileContribution(FileRecord File, IReadOnlyList<Node> Nodes, IReadOnlyList<Edge> Edges);

// In UnityGraph.Core, shared with guards and config.
public sealed class VendoredFolders { public IReadOnlyList<string> Prefixes { get; } public bool Contains(string path); public static VendoredFolders Default { get; } }

public static class CodeIndex
{
    public static IndexSummary Full(CodeWorkspace workspace, BulkWriter writer);
    public static IndexSummary Update(CodeWorkspace workspace, GraphStore store, IEnumerable<string>? extra = null);
}

public sealed record IndexSummary(int Files, int Vendored, int Nodes, int Edges, int Removed, TimeSpan Elapsed, IReadOnlyList<string> Problems);
```

- `Full` writes every tracked file through a bulk writer on a staging store; the CLI opens the
  staging store and publishes it.
- `Update` runs `CheckFreshness(stats, Indexer.Code)` over `TrackedFiles`, takes the dirty list,
  reindexes what changed (plus `extra`, the files a hook just edited, and the lost-key
  dependents) and records touched files. A graph that was just `Created` gets a full index
  instead.
- Rows are deterministic: files in path order, each file's nodes and edges in source order.

CLI:

```
ug index [--project <dir>] [--full]
indexed 1,160 files (412 own, 748 vendored): 14,203 nodes, 61,877 edges in 11.4 s
```

One line on success; problems follow as `  warning: ...` lines, at most 10 and a count. Past 10
the count line names the file that holds every warning: `  ... and 32 more warnings:
Library/unitygraph/index-warnings.txt` (the graph directory, one warning per line; rewritten by
each `ug index`, so it is empty after an index without warnings and nothing is deleted, C1). Exit 0
unless the graph can't be written. `--graph-dir <dir>` (hidden) puts the graph and its lock
somewhere other than `Library/unitygraph/`, so purge-squad can be measured without writing into
it; `GraphStore.Open`/`OpenStaging` gain an optional `graphDirectory` for it.

## Failure handling

- No `.csproj` in the root: `ug index` prints `no .csproj files: in Unity, Preferences > External
  Tools > Regenerate project files` and exits 0 with nothing indexed. Queries then fall back to
  asset data only.
- A file that fails to read: indexed as `status: error` with no nodes, and a warning.
- Compile errors don't stop indexing: Roslyn binds what it can; an unbound call produces no edge.

## Performance targets

| Operation | Target |
| --- | --- |
| Full index of purge-squad (21 projects, ~1,000 indexed files) | ≤ 60 s (goal ≤ 20 s) |
| `Update` after one own file changed, in a fresh process | ≤ 1 s including start |
| `Update` with nothing changed | ≤ 300 ms including start |

## Testing

- **Fixture** `fixtures/CodeProject/`: a minimal Unity-shaped project with hand-written legacy
  `.csproj` files (backslash and XML-escaped `Compile` paths, `DefineConstants`, a missing
  `HintPath`, a `ProjectReference`), and a stub `UnityEngine` project under `Library/` (compiled
  for binding, never indexed) in place of the editor's DLLs, so no Unity install is needed.
  Covers: partial types, nested types, generics, interfaces explicit and implicit, overrides,
  properties with `[field: SerializeField]`, events, operators, constructors, local functions
  and lambdas, `SendMessage`/`Invoke`/`StartCoroutine` with literal, const and `nameof`, a
  vendored folder, a `.meta` with a GUID, a compile error.
- **Serialization table:** one row per rule in Serialized fields, positive and negative.
- **Golden:** the fixture's full index as sorted text (one line per node and edge), committed.
- **Incremental:** rename a method → callers in another file are reindexed and point at the new
  key; delete a file → its nodes go and edges to it become unresolved; add a file not in a
  `.csproj` → assigned by folder; a `.csproj` change → project re-read.
- **Determinism:** two full indexes produce identical databases (rows compared).
- **purge-squad acceptance** (runs when `UG_PURGE_SQUAD` is set, graph in a temp folder via
  `graphDirectory`, nothing written into purge-squad): 0 unexpected problems, known types
  present (`LabStage`), serialized field count recorded, targets above met.
- Coverage ≥ 80% lines.

## Boundaries

- Always: read-only on the Unity project (the only write is the graph under
  `Library/unitygraph/`); deterministic rows; exact unless marked heuristic.
- Ask first: new node or edge kinds; indexing `Library/PackageCache`; running analyzers or
  source generators; a new package besides `Microsoft.CodeAnalysis.CSharp` (approved
  2026-09-29).
- Never: MSBuild or the .NET SDK at runtime; network; writing any file outside the graph folder.

## Measured

purge-squad, 2026-09-29, dev machine (Windows 11, Release build, `ug index --graph-dir` into a
temp folder; update times on a copy of its index inputs, fresh process each run, median of 11).
Acceptance test: `UG_PURGE_SQUAD=<project> dotnet test tests/UnityGraph.Code.Tests`.

| What | Measured |
| --- | --- |
| Projects | 21 `.csproj` (package-only ones bound from `Library/ScriptAssemblies`) |
| Indexed files | 1,380: 161 own, 1,219 vendored (`Assets/3rd Party`, `Assets/Plugins`) |
| Nodes | 24,402 (own 3,081): 11,998 field, 8,001 method, 2,121 type, 2,119 property, 163 event |
| Edges | 32,680 (own files 7,984): 22,779 contains, 2,966 calls, 2,720 overrides, 1,751 uses_type, 1,154 script_defines, 1,120 inherits, 190 implements, 0 dispatches |
| Serialized fields | 4,341 (own 720); 89 own MonoBehaviours |
| `calls` dropped by scope | 1,765 (own code into engine, BCL, packages; deduped like edges) vs 2,966 kept |
| Problems | none |
| `graph.db` | 21 MB, mostly vendored declarations |
| Full index | 3.0 s in-process, 3.7 s wall (6 s on a cold disk cache); target ≤ 60 s, goal ≤ 20 s: met |
| `Update`, nothing changed | 261 ms wall; target ≤ 300 ms: met |
| `Update`, one own file changed | 1.21 s wall; target ≤ 1 s: **missed** |

The one-file miss is Roslyn's cold start, not our work: loading and parsing every project the
file needs takes ~120 ms, then binding the one file takes ~750 ms (declaration tables and
metadata import of ~300 referenced DLLs, mostly JIT), and the walk itself 11 ms once warm.
Runtime knobs (tiered PGO, quick JIT for loops, GC) move it by under 15%. Options, in order:
ReadyToRun-compile the published `ug` for the desktop RIDs (needs the crossgen pack, a new
download: ask first); or have the hook run the update in the background so its time never
blocks a turn (it's already fail-open). Until then the target stands as missed.

## Open points

1. Whether edges from vendored bodies are ever needed (e.g. a Feel callback calling into own
   code). Revisit if `bench` shows a question that needs them.
