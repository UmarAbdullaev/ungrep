# Spec: asset-index

Part of unitygraph (see `../SPEC.md`). Status: **draft** (2026-09-29). Writes into the store
described in `SPEC-graph-core.md`, next to `SPEC-code-index.md`.

## Objective

Turn a Unity project's `.meta` files and text-serialized scenes, prefabs and assets into graph
nodes and edges, without Unity running and without a YAML library, so that "which prefabs is
this script on", "who references this prefab", "what calls this method from the inspector" and
"what is GUID X" are one `ug` call instead of a grep through YAML (design rules 1, 2, 6).

Every edge comes from a GUID or a fileID in the files, so it is exact; the one heuristic is a
UnityEvent whose target method's parameters the file does not say. asset-index never reads the
code graph: scripts are linked through their GUID (`script_defines`, written by code-index) and
UnityEvent targets through the documentation ID the file implies, so the two indexers stay
independent and a rename shows up as an unresolved edge, which is the truth (a broken wiring).

Users: `query` (asset answers, "lives on" for types, inspector callers for methods), `guards`
(what a script rename or a field rename breaks), `impact` and `outline` (later).

## Scope

In:

- `.meta` files under `Assets/` and embedded packages (`Packages/<name>/`): one `asset` node per
  asset, keyed by its GUID.
- Text YAML content of own (non-vendored) scenes, prefabs and other Unity YAML assets: game
  objects, components, prefab instances, sub-assets; edges `contains`, `child_of`,
  `instance_of`, `places`, `variant_of`, `overrides_value`, `references`, `event_calls`,
  `addressable`.
- Full index and incremental reindex, in the same store and the same `ug index` run as
  code-index; queries freshen both.

Out (v1): `anim_event_calls` (purge-squad has no own `.anim` with events; revisit with `bench`);
`in_assembly` and `assembly_refs` (code-index already has assemblies from the `.csproj` files);
`ProjectSettings/*.asset` (build scenes, tags); sprite and other sub-assets named only in a
texture's `.meta`; binary-serialized assets (recorded as `binary`, no rows); `Library/PackageCache`
assets (references to them stay unresolved, like calls into packages); content of vendored
assets (their `.meta` gives the node; their YAML is not read).

## Which files are indexed

Walk `Assets/` and every folder in `Packages/` (embedded packages), skipping folders Unity
skips (name starts with `.` or ends with `~`). Two kinds of tracked file, both with
`indexer = 'asset'`:

- **Meta files**: every `<asset>.meta` whose `<asset>` is not a folder. Vendored ones too (same
  `vendored` setting as code-index): an own prefab referencing a Feel texture needs the texture's
  node.
- **Content files**: an own (non-vendored) file with one of these extensions: `.unity`,
  `.prefab`, `.asset`, `.mat`, `.controller`, `.overrideController`, `.anim`, `.mask`,
  `.playable`, `.mixer`, `.spriteatlas`, `.physicMaterial`, `.physicsMaterial2D`,
  `.lighting`, `.preset`, `.signal`, `.renderTexture`, `.terrainlayer`, `.flare`, `.guiskin`,
  `.brush`. A file that doesn't start with `%YAML` is recorded with status `binary` and no rows.

A content file's GUID comes from its own `.meta`. With no `.meta` (Unity hasn't imported it
yet), the file is recorded with status `error`, no rows, and a problem line.

## Meta files

Read line by line until the importer body; only these keys matter:

- `guid: <32 hex>` → node key `a:<guid>`. Missing or malformed → status `error`, problem line.
- `folderAsset: yes` → not an asset (a folder that the walk saw as a file: skipped).
- `mainObjectFileID: <n>` (NativeFormatImporter) → the main object of a `.asset`.

Node: kind `asset`, `name` = file name with extension (`Gun.prefab`), owned by the `.meta` file
(so a 40 MB texture is never hashed), lines 0, `props.asset_type`:

| `asset_type` | Extensions (case-insensitive) |
| --- | --- |
| `script` | `.cs` |
| `scene` | `.unity` |
| `prefab` | `.prefab` |
| `scriptable_object` | `.asset` with `mainObjectFileID: 11400000` |
| `material` | `.mat` |
| `shader` | `.shader`, `.shadergraph`, `.shadersubgraph`, `.compute`, `.hlsl`, `.cginc` |
| `animator_controller` | `.controller`, `.overrideController` |
| `animation_clip` | `.anim` |
| `input_actions` | `.inputactions` |
| `asmdef` | `.asmdef`, `.asmref` |
| `texture` | `.png`, `.jpg`, `.jpeg`, `.tga`, `.psd`, `.tif`, `.tiff`, `.exr`, `.hdr`, `.bmp`, `.gif` |
| `audio` | `.wav`, `.mp3`, `.ogg`, `.aif`, `.aiff`, `.flac` |
| `model` | `.fbx`, `.obj`, `.blend`, `.dae`, `.3ds`, `.max`, `.ma`, `.mb` |
| `other` | anything else |

Two `.meta` files with the same GUID: the first by path keeps the node, the other gets a
problem line (Unity reassigns one on import).

## Reading Unity YAML

Unity writes a small, regular YAML subset; a line reader handles it (no YAML package):

- `%YAML` / `%TAG` directives; documents start `--- !u!<classID> &<fileID>`, optionally
  followed by ` stripped`. The next line is `<ClassName>:`.
- Block mappings indented by two spaces; block sequences written `- ` at the key's indent or
  deeper; flow mappings on one line (`{fileID: 11500000, guid: 5a1b..., type: 3}`, `{x: 0, y: 1}`);
  `[]` and `{}`; plain, single-quoted and double-quoted scalars, which may continue on deeper
  lines (continuations are joined for quoted scalars and ignored for plain ones: nothing
  indexed is a long string).
- Output per document: class ID, class name, fileID, stripped, first and last line, and the
  leaves in file order as `(path, value, line)`, where `path` is `m_Component[0].component`,
  `_waves[2].prefab`, and a value that is a flow mapping with `fileID` is an object reference
  `(fileID, guid?, type?)`.
- Anything the reader doesn't understand in a document (tabs, anchors other than the header,
  block scalars `|`/`>`) makes that document's leaves stop at that line, with one problem line
  per file; the file's other documents are still read.

## Node rules (content files)

`G` is the file's GUID; `F` a document's fileID. Keys `o:G:F`.

| Document | Node | `name` | `props` |
| --- | --- | --- | --- |
| GameObject (1), not stripped | `game_object` | `m_Name` | `path`: hierarchy path inside the file (`HubStation/LabStage/Turntable`, ≤ 200 chars); `active: false` when `m_IsActive: 0` |
| a component (has `m_GameObject`), not stripped, not Transform (4) or RectTransform (224) | `component` | class name; for MonoBehaviour (114) the type after `::` in `m_EditorClassIdentifier`, else `MonoBehaviour` | `class_id`; `class`: full type name from `m_EditorClassIdentifier` when present; `script`: script GUID; `enabled: false` when `m_Enabled: 0` |
| PrefabInstance (1001) | `prefab_instance` | the value of the one `m_Name` modification; the source's GUID prefix `prefab <8 hex>` when there are none or several | `source`: source prefab GUID |
| MonoBehaviour (114) without a GameObject, not the main object (see below) | `sub_asset` | `m_Name` | `class`, `script` as for components |

- Lines: the document's header line to its last line, so an answer can point at exactly the
  lines to read.
- Transforms are not nodes: a reference to a Transform or RectTransform means its GameObject,
  and `m_Father` gives `child_of`.
- Stripped documents (objects of a nested prefab that this file refers to) are not nodes: a
  reference to one means the source object `o:<source guid>:<source fileID>` from its
  `m_CorrespondingSourceObject`; a stripped Transform used as a parent means its prefab instance.
- Hierarchy path: GameObject names joined along `m_Father`; a parent inside a prefab instance
  contributes the instance's name.
- Main object of a non-scene, non-prefab file: the document with the `.meta`'s
  `mainObjectFileID`, else the first document. It is the asset itself: its edges come from
  `a:G` and it gets no node. Documents that are neither main nor MonoBehaviours (animator
  states, scene settings like `RenderSettings`) get no node; their edges come from `a:G` with
  the class name leading the field (`AnimatorState.m_Motion`).

## Edge rules

All owned by the content file and `exact` unless noted. One edge per (src, kind, dst, props)
per file, at the first line it occurs.

| Edge | Source → target | Props |
| --- | --- | --- |
| `contains` | `a:G` → each game object and sub-asset (prefab instances get `places`); game object → each of its components (a component added to a nested prefab's object: from that object's source key) | |
| `child_of` | game object → parent game object or prefab instance; prefab instance → the object at its `m_TransformParent` | |
| `instance_of` | MonoBehaviour component or sub-asset → `a:<script guid>`; main object MonoBehaviour: `a:G` → `a:<script guid>`; prefab instance → `a:<source guid>` | |
| `places` | `a:G` → prefab instance | |
| `variant_of` | `a:G` → `a:<source guid>`, for a `.prefab` whose root is a prefab instance (`m_TransformParent: {fileID: 0}`) | |
| `overrides_value` | prefab instance → `o:<source guid>:<target fileID>` | `path` (the modification's `propertyPath`), `value` (≤ 80 chars) or `ref` (the reference key) |
| `references` | the object whose leaf holds a non-null object reference → `a:<guid>` (another file) or the local object's key | `field` (leaf path, ≤ 200 chars); `object`: target fileID for a reference into another file |
| `event_calls` | component or sub-asset holding a UnityEvent → the method's key | `event` (field path before `.m_PersistentCalls`), `target` (the target object's key) |
| `addressable` | `a:<entry guid>` → `a:G` (the group asset), from `m_SerializeEntries` | `address`; `labels` when any |

Details:

- `instance_of` points at the script **asset**, not the type: code-index's `script_defines`
  links `a:<script guid>` to the type, so a class rename needs no asset reindex. (This refines
  the `instance_of` row in SPEC-graph-core; the key form is the same.)
- `references` skip: `fileID: 0`; built-in resources (GUID starting `0000000000000000`); and
  structural fields that other edges cover (`m_GameObject`, `m_Father`, `m_Children`,
  `m_Component`, `m_PrefabInstance`, `m_PrefabAsset`, `m_CorrespondingSourceObject`, `m_Script`,
  `m_SourcePrefab`, `m_Modification`, and `m_Target` inside persistent calls). A reference
  through a GUID equal to `G` is local.
- Prefab instance modifications on transform layout are not edges (`m_LocalPosition*`,
  `m_LocalRotation*`, `m_LocalScale*`, `m_LocalEulerAnglesHint*`, `m_RootOrder`,
  `m_AnchorMin*`, `m_AnchorMax*`, `m_AnchoredPosition*`, `m_SizeDelta*`, `m_Pivot*`): every
  placed instance has them and no question needs them. An `objectReference` in a modification
  gives `overrides_value` with `ref` and also a `references` edge from the instance.
- **UnityEvent** calls (`<field>.m_PersistentCalls.m_Calls[i]`): the type is
  `m_TargetAssemblyTypeName` up to the comma, `+` → `.`; the method key is
  `cs:M:<type>.<m_MethodName>` plus parameters by `m_Mode`: 1 (void) none, 2
  `(<m_ObjectArgumentAssemblyTypeName up to the comma>)`, 3 `(System.Int32)`, 4
  `(System.Single)`, 5 `(System.String)`, 6 `(System.Boolean)`: `exact`. Mode 0 (dynamic, the
  event's own arguments): the key without parameters, `heuristic` (query matches it against
  every overload). No type name (older Unity): the target object's `class`, else no edge and a
  count. A method inherited from a base class has the base's key, so its edge is unresolved in
  v1; query reports it as such.

## Incremental reindex

- `CheckFreshness(stats, Indexer.Asset)` over the tracked files (stats from the directory walk,
  no extra `stat` per file).
- A changed or added content file is reindexed; a changed, added or removed `.meta` also
  reindexes its content file (the GUID may have changed). Removed files: `RemoveFile`.
- No dependents: an edge into an object that a reindex removed stays and reads as unresolved,
  which is what happened (a broken override or reference).
- The dirty list (`TakeDirty`) is taken once per update and split between the indexers by
  extension; `CodeIndex.Update` gains a `dirty` parameter for that.

## Running with code-index

One coordinator in the CLI (`GraphUpdate`) runs both indexers over one store: code first, then
assets; `store.Created` or `--full` gives a full index of both through one bulk writer. A
module switched off is skipped. `ug index` prints the combined summary on the existing line
(files, own, vendored, nodes, edges); problems of both follow. No `.csproj`: code is skipped
with its existing message as a warning, assets are indexed. Queries keep their current
behaviour without `.csproj` files.

## Public surface (`UnityGraph.Assets`)

```csharp
public static class UnityYaml { public static IReadOnlyList<YamlDocument> Read(TextReader text, List<string> problems); }
public sealed record YamlDocument(int ClassId, string ClassName, long FileId, bool Stripped, int LineStart, int LineEnd, IReadOnlyList<YamlLeaf> Leaves);
public sealed record YamlLeaf(string Path, string Value, ObjectRef? Ref, int Line);
public readonly record struct ObjectRef(long FileId, string? Guid, int Type);

public sealed record MetaFile(string? Guid, bool Folder, long? MainObjectFileId) { public static MetaFile Read(string fullPath); }

public sealed class AssetWorkspace
{
    public static AssetWorkspace Scan(string projectRoot, VendoredFolders vendored);  // one directory walk
    public IReadOnlyList<FileStat> TrackedFiles { get; }                             // sorted by path
    public bool IsTracked(string path);
    public bool IsVendored(string path);
    public IReadOnlyList<FileContribution> IndexMany(IEnumerable<string>? paths = null); // parallel, path order
}

public static class AssetIndex
{
    public static IndexSummary Full(AssetWorkspace workspace, BulkWriter writer);
    public static IndexSummary Update(AssetWorkspace workspace, GraphStore store, IReadOnlyList<string> dirty, IEnumerable<string>? extra = null);
}
```

`FileContribution` and `IndexSummary` move from `UnityGraph.Code` to `UnityGraph.Core.Graph`
so both indexers share them.

## Failure handling

- A file that can't be read: status `error`, no rows, a problem line. The walk never stops.
- YAML the reader doesn't follow: see Reading Unity YAML; never an exception out of the indexer.
- Duplicate object keys in one file (a hand-merged scene): the first document keeps the key, a
  problem line names the file.

## Performance targets

| Operation | Target |
| --- | --- |
| Full asset index of purge-squad (~6,900 `.meta`, ~400 own content files) | ≤ 20 s |
| Update with nothing changed, both indexers, fresh process | ≤ 400 ms (code alone: 261 ms) |
| Update after one own prefab changed, fresh process | ≤ 600 ms |
| Reading a 650 KB scene | ≤ 100 ms |

## Testing

- **Fixture:** `fixtures/CodeProject` gains assets beside its scripts: `.meta` files for the
  scripts, a prefab using `Turntable`, a variant of it, a scene placing both with overrides and
  a UnityEvent calling `LabStage.Open(int)` and a dynamic call, a ScriptableObject asset with a
  sub-asset, a material referencing a texture, a texture `.meta`, a vendored prefab, an
  addressables group, a binary `.asset`, and a content file without `.meta`.
- **Reader table:** one row per construct above (headers, stripped, sequences at both indents,
  flow maps, quoted continuations, empty collections, an unsupported construct).
- **Golden:** the fixture's asset rows as sorted text, committed.
- **Incremental:** edit a prefab; change a `.meta` GUID; delete a scene; add a content file
  without `.meta`, then its `.meta`; a no-op leaves identical rows.
- **Determinism:** two full indexes, identical rows.
- **purge-squad acceptance** (`UG_PURGE_SQUAD`, graph in a temp folder, nothing written into
  purge-squad): no problems beyond recorded binary files, `Hub.unity` places the `TopBar`
  instance, counts and times recorded in Measured.
- Coverage ≥ 80% lines on Assets.

## Boundaries

- Always: read-only on the project; deterministic rows; exact unless marked heuristic.
- Ask first: new node or edge kinds; a YAML package; reading `Library/` or vendored YAML.
- Never: writing anything outside the graph folder; network; running Unity.

## Measured

Not yet (A6).

## Open points

1. Prefab instance names when the file has several or no `m_Name` modifications: v1 names them
   by source GUID; resolving the source prefab's root name needs a cross-file lookup.
2. Whether vendored YAML is ever worth reading (an own scene placing a vendored prefab already
   links to it through `instance_of`).
