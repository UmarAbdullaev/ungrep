# Spec: query

Part of unitygraph (see `../SPEC.md`). Status: **draft** (2026-09-29). Reads the store described
in `SPEC-graph-core.md`, kept fresh by `SPEC-code-index.md`.

## Objective

Answer "where is X", "who uses X" and "what is X" about a Unity project's C# and assets in one `ug` call,
within the answer budget, with `file:line` anchors and a confidence tag, so Claude's first
navigation call is the graph call instead of a grep-and-read streak (design rules 1, 2, 6).

Pulled forward (user, 2026-09-29): the first version covered the code graph only. Asset answers
(task A5) joined once `asset-index` landed; the command names and line format stayed.

## Scope

In: `ug find`, `ug refs`, `ug explain` (with `--members`); symbol resolution; answer budgets and
`more` handles; bringing the graph up to date before answering; the `unity` skill's command
table.

Out (later modules): `impact`, `outline`, `batch` (SPEC module table, after asset-index);
`--json`; reindex from hooks (step 3, `guards`/reindex-on-edit).

## Commands

All take `--project <dir>` (default: current folder) and `--budget <tokens>` (200 to 8000;
default `answerBudget` from `.claude/unitygraph.json`, 1,200). Plain text, one fact per line,
paths project-relative with `/`. Exit 0 for any answer including "nothing found"; 1 only when
there is no Unity project, the config is invalid, or no graph can be built.

### `ug find <text> [--kind type|method|field|property|event|asset|game_object|prefab_instance]`

Nodes whose name has words starting with the words of `<text>` (the store's name search), exact
name matches first.

```
$ ug find Open
Game.Core.IOpenable.Open           method  Assets/Scripts/Core/Stage.cs:9        Open(int)
Game.Core.Stage.Open               method  Assets/Scripts/Core/Stage.cs:14       Open(int)
Game.Lab.LabStage.Open             method  Assets/Scripts/Lab/LabStage.cs:24-36  Open(int)
Game.Lab.LabStage._opened          field   Assets/Scripts/Lab/LabStage.cs:16     int
Game.EditorTools.LabTools.OpenLab  method  Assets/Editor/LabTools.cs:9-13        OpenLab()
Game.Lab.LabStage.OpenFirst        method  Assets/Scripts/Lab/LabStage.cs:55-56  OpenFirst()
6 found
```

Asset, game object and prefab instance rows (`--kind` narrows to them; a plain `find` ranks
them together with code, exact names first):

```
$ ug find Turntable --kind asset
Assets/Scripts/Lab/Turntable.cs          script  guid 0f9e8d7c6b5a49382716a5b4c3d2e1f0
Assets/Prefabs/Turntable.prefab          prefab  guid b1000000000000000000000000000001
Assets/Prefabs/Turntable Variant.prefab  prefab  guid b2000000000000000000000000000002
3 found

$ ug find Platter --kind game_object
Turntable/Platter  game_object  Assets/Prefabs/Turntable.prefab:65-77  inactive

$ ug find Lab --kind prefab_instance
Lab Turntable  prefab_instance  Assets/Scenes/Lab.unity:117-145  of Assets/Prefabs/Turntable.prefab
```

Columns are padded to the widest entry in the answer. At most 50 results; beyond that the last
line is `50+ found, narrow the text or add --kind`. Nothing: `nothing found: <text>`.

### `ug refs <symbol>`

Every edge arriving at the symbol, grouped by label in this order, each group sorted by source
name then line:

| Edge in | Label |
| --- | --- |
| `calls` | `called from` |
| `dispatches` | `dispatched from` (always `[heuristic]` unless exact) |
| `overrides` | `overridden by` / `implemented by` (target is an interface member) |
| `inherits` | `subclass` / `extended by` (target is an interface) |
| `implements` | `implemented by` |
| `uses_type` | `used by` |
| `script_defines` | `script asset` (the `.cs.meta` GUID) |
| `instance_of` from a component whose script defines the type | `on` (`GameObject > Component`) |
| `event_calls` | `unity event` (`Owner: field`; param-less mode-0 calls matched by name are `[heuristic]`) |
| `instance_of` from a prefab instance | `prefab instance` |
| `variant_of` | `variant` |
| `references` | `referenced by` (`Owner: field path`) |
| `overrides_prop` | `overridden in` (`Instance: path = value` or `path -> Target`) |
| `addressable` | `addressable entry` (`Group "address"`) |

For an asset (`refs` on a path, file name, GUID or `a:` key) the edges into the asset and into
any of its objects count, except those from its own file and `.meta`. Labels name assets by path,
game objects by hierarchy path, components as `GameObject > Type`, and a key with no node raw.
`contains` is not a reference. Overloads named without a signature (`Turntable.Rotate`) are one
symbol for `refs`: all their references, and each line says which overload when there are
several.

```
$ ug refs Turntable.Stop
Game.Lab.Turntable.Stop()  method  Assets/Scripts/Lab/Turntable.cs:23  [exact]
  called from  Game.Broken.Run             Assets/Scripts/Broken.cs:10
  called from  Game.Lab.LabStage.OnClosed  Assets/Scripts/Lab/LabStage.cs:51
2 references
```

A line whose source has no node (a file that failed to read) shows the raw key.

### `ug explain <symbol> [--members]`

Everything a first look needs, in one answer. For a type:

```
$ ug explain LabStage
Game.Lab.LabStage  class : Game.Core.Stage, Game.Lab.ILabDebug  Assets/Scripts/Lab/LabStage.cs:7-57  [exact]
  also in       Assets/Scripts/Lab/LabStage.Debug.cs
  unity         MonoBehaviour, script guid 5a1b2c3d4e5f60718293a4b5c6d7e8f9
  engine calls  Awake
  entry points  OpenFirst [ContextMenu]
  serialized    _turntable: Turntable, _data: StageData, speed: float
  members       7 methods, 6 fields
  used by       Game.EditorTools.LabStageEditor, Game.EditorTools.LabTools.OpenLab
  more  ug explain LabStage --members | ug refs LabStage
```

A type with components in scenes or prefabs gets `on  <file> > <GameObject> > <Component>`; a
method named by UnityEvents gets `unity events  <file> > <owner>: <field>`.

Lines with nothing to say are left out, except `used by` / `called from`, which say `(none)`
(an unused symbol is an answer). Header: display name, kind (the subkind for types), base list,
location, confidence. `unity` comes from the `unity` prop, the guid from `script_defines`.
`engine calls` are members with `message`; `entry points` members with `entry`. Lists in a line
are cut at 8 items with `(+N)`.

`--members` adds one line per member, by file then line: `    <name>  <kind>  <line>  <sig or
type>  <flags>` (flags: access unless private, static, abstract, virtual, override, const,
readonly, serialized, message, `[entry]`), and the `more` line becomes `ug refs <symbol>`.
All golden answers: `tests/UnityGraph.Query.Tests/golden/`.

For a member:

```
$ ug explain Turntable.Rotate
Game.Lab.Turntable.Rotate(float)  method  Assets/Scripts/Lab/Turntable.cs:17-21  public  [exact]
  in           Game.Lab.Turntable
  called from  Game.Lab.LabStage.Open, Game.Lab.LabStage.Spin
  calls        Game.Lab.Turntable.Rotated
  more  ug refs Turntable.Rotate
```

Member lines: `in`; `overrides` (what it overrides or implements); `called from` and
`dispatched from`, callers counted when one calls it more than once in a file (edges are one per
file, so the count is files); `calls` (outgoing `calls` and `dispatches`, deduplicated);
`serialized` (with former names) for fields and properties; `type` for fields, properties and
events; `flags` for what the header doesn't say. Fields have no `called from` (no read/write
edges yet).

For an asset:

```
$ ug explain Turntable.prefab
Assets/Prefabs/Turntable.prefab  prefab  guid b1000000000000000000000000000001  [exact]
  objects      2 game objects, 3 components
  scripts      Game.Lab.LabStage, Game.Lab.Turntable
  uses         Assets/Materials/Lab.mat
  used by      Assets/Data/LabSettings.asset, Assets/Prefabs/Turntable Variant.prefab, Assets/Scenes/Lab.unity
  addressable  "Prefabs/Turntable" in Assets/AddressableAssetsData/AssetGroups/Default Local Group.asset, labels lab, turntables
  more  ug refs Turntable.prefab
```

Asset lines: `defines` (a script's types); `objects` (counts by kind, not for scripts);
`scripts` (types behind its components, or `script guid X` when no code defines it); `variant of`;
`prefabs` (instanced, besides the variant base); `uses` (assets it references); `unity events`
(methods its UnityEvents call); `used by` (always); `addressable`; `on` (a script's components
elsewhere).

For a game object, component, sub-asset or prefab instance (an `o:` key from an answer):

```
$ ug explain o:c3000000000000000000000000000003:600
Lab Turntable  prefab_instance  Assets/Scenes/Lab.unity:117-145  [exact]
  in           Assets/Scenes/Lab.unity
  children     Lab Turntable/Marker
  instance of  Assets/Prefabs/Turntable.prefab
  overrides    m_Name = Lab Turntable, _speed = 3, _turntable -> Turntable > Turntable
  more  ug refs o:c3000000000000000000000000000003:600
```

Object lines: `in`, `parent`, `on`, `components`, `children`, `script` or `instance of`,
`overrides`, `references` (serialized object fields, not for prefab instances), `unity events`
(`field -> Method`). Separators are ASCII (` > `, `->`) so every console shows them.

## Resolving `<symbol>`

1. `cs:<doc id>`, `a:<guid>`, `o:<guid>:<fileID>`: that key.
2. 32 lowercase hex characters: that GUID's asset.
3. Text with a `/`: the asset at that project path (`Assets/Prefabs/Turntable.prefab`).
4. Text with a `.` and no `(` is first tried as an asset file name (`Turntable.prefab`),
   case-sensitive first; several assets with that name are ambiguous. No asset: step 5.
5. Otherwise the text is a dotted name, optionally with a parameter list (`Rotate(float)` is
   matched against `sig`). Candidates are nodes named like its last segment; a candidate matches
   when its qualified name (the doc ID without prefix, parameters and generic arity marks) ends
   with the text on a `.` boundary. Case-sensitive matches win; if none, case-insensitive.
6. One match, or only overloads of one method: answer. Several: list them like `find` (at most
   20) under `ambiguous: <text>` with a `more` line repeating the command for the first by its
   qualified name, exit 0. None: `not found: <text>` followed by up to 5 `find` results for the
   last segment.

## Freshness

Before answering, the query brings the graph up to date through the CLI's `GraphUpdate` (code,
then assets; design rule 5: a stale answer is worse than grep). A new graph is built there, and the answer starts
with one line `indexed N files in S s`. If another writer holds the lock (1 s timeout), the
query answers from the graph as it is and ends with `note: graph busy, edits from the last
seconds may be missing`. `code-index` or `query` off in config: `code-index or query is off in .claude/unitygraph.json;
nothing to query`. No `.csproj` files: the "Regenerate project files" hint; asset answers still
need code-index on.

## Budget

A token is estimated as 4 UTF-8 bytes. Lines are added whole until the next one would pass the
budget; then the answer ends with `  more  <the same command> --budget <2 × budget, max 8000>`.
The header line always fits. Deterministic: same graph and command, same bytes.

## Performance targets

| Operation | Target |
| --- | --- |
| Any query on purge-squad, graph up to date, fresh process | p95 ≤ 500 ms |
| Symbol resolution + answer, in process | ≤ 50 ms |

## Code

`src/UnityGraph.Query`: `SymbolResolver` (text → keys), `Answer` (budgeted line builder),
`QueryEngine` (`Find`, `Refs`, `Explain` over an open `GraphStore`, returning text). The CLI
(`FindCommand`, `RefsCommand`, `ExplainCommand`) opens the project, freshens the graph and
prints. The engine never writes.

## Testing

- Golden answers for each command on `fixtures/CodeProject` (types, overloads, partial types,
  interface members, dispatch, unresolved sources, ambiguous and not-found names; assets by path,
  name and GUID, game objects, components, sub-assets, prefab instances and variants).
- Resolver table: key, dotted names, nested and generic types, `sig`, case fallback.
- Budget: a small budget cuts on a line boundary and names the larger budget; determinism.
- Freshness: edit a file, query sees it; locked graph answers with the note.
- Performance: env-gated purge-squad run records p95 in Measured.
- Coverage ≥ 80% lines on Query.

## Boundaries

- Always: read-only on the project; the only write is the graph write through `GraphUpdate`.
- Ask first: a new command; `--json`; changing the line format once the skill documents it.
- Never: network; answers without a location; more than the budget.

## Measured

purge-squad, 2026-09-29, a copy of its index inputs, Release build, fresh process per query,
graph up to date, 21 runs per query (`find Gun`, `refs LabStage`, `explain LabStage`,
`explain LabStage --members`, `refs MonoBehaviour`, `find Update --kind method`): medians
268–304 ms, p95 324–363 ms, overall p95 345 ms (target ≤ 500 ms: met). About 260 ms of that is
process start plus the freshness check. Answer sizes: 6 to 1,165 tokens (`find Update` hits the
50-result cap within the 1,200 budget).
