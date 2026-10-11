# Spec: query

Part of unitygraph (see `../SPEC.md`). Status: **draft** (2026-09-29). Reads the store described
in `SPEC-graph-core.md`, kept fresh by `SPEC-code-index.md`.

## Objective

Answer "where is X", "who uses X" and "what is X" about a Unity project's C# and assets in one `ug` call,
within the answer budget, with `file:line` anchors and a confidence tag, so Claude's first
navigation call is the graph call instead of a grep-and-read streak (design rules 1, 2, 6).

Pulled forward (user, 2026-09-29): the first version covered the code graph only. Asset answers
(task A5) joined once `asset-index` landed; the command names and line format stayed.

Q2 (2026-10-01) adds `ug outline <file>` (the shape of a file without its bodies) and `ug impact
<symbol>` (what breaks if the symbol changes), see their sections below.

## Scope

In: `ug find`, `ug refs`, `ug explain` (with `--members`), `ug outline`, `ug impact`; symbol
resolution; answer budgets and `more` handles; bringing the graph up to date before answering;
the `unity` skill's command table; `--json` on `outline` and `impact` only (Q2).

Out (later modules): `batch` (editor-ops has `ug batch` for editor steps); `--json` on `find`,
`refs`, `explain`; reindex from hooks (step 3, `guards`/reindex-on-edit).

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

A GUID (32 hex characters, either case) finds its asset, under no `--kind` or `--kind asset`
(B1d, 2026-10-11: no asset is named by its GUID, so the name search found nothing):
`Assets/Prefabs/Turntable.prefab  prefab  guid b1000000000000000000000000000001`, `1 found`.

### `ug refs <symbol>`

Every edge arriving at the symbol, grouped by label in this order, each group sorted by source
name then line:

| Edge in | Label |
| --- | --- |
| `calls` | `called from`; by its `use`: `read by`, `written by`, `subscribed by`, `unsubscribed by`, `raised by`, `delegate from` |
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

**How a member is used** (B1g, 2026-10-11: `ug refs Health.Died` listed the `+=`, the `-=` and
the raise alike as `called from` and named no handler, so the agent grepped for them; a field
had no code lines at all). A `calls` line is labelled by the edge's `use` (SPEC-code-index,
Uses), the `called from` family sorted by label. A subscription line ends with the other end:
`handler <method>` (or `handler lambda`) on the event's lines, `to <event>` / `from <event>` on
the handler's.

```
$ ug refs Turntable.Rotated
Game.Lab.Turntable.Rotated  event  Assets/Scripts/Lab/Turntable.cs:15  [exact]
  raised by        Game.Lab.Turntable.Rotate  Assets/Scripts/Lab/Turntable.cs:20
  subscribed by    Game.Lab.Spectator.Watch   Assets/Scripts/Lab/Turntable.cs:37  handler Game.Lab.Spectator.OnRotated
  subscribed by    Game.Lab.Spectator.Watch   Assets/Scripts/Lab/Turntable.cs:38  handler lambda
  unsubscribed by  Game.Lab.Spectator.Leave   Assets/Scripts/Lab/Turntable.cs:44  handler Game.Lab.Spectator.OnRotated
4 references
```

**Implementers through extending interfaces** (B1c, 2026-10-11: "which classes implement
`IPartRule`" took one `refs` per extending interface). For an interface, a type that implements
an interface extending it, at any depth, is an `implemented by` line too, ending with the
interface it goes through; a type that also implements it directly is listed once, as the direct
line. `explain` (`Game.Core.Door (via Game.Core.ILockable)`) and `impact` list them the same way.
Subclasses of an implementer are not walked: they are one `refs` away.

```
$ ug refs IOpenable
Game.Core.IOpenable  interface  Assets/Scripts/Core/Stage.cs:7-10  [exact]
  extended by     Game.Core.ILockable  Assets/Scripts/Core/Stage.cs:27
  implemented by  Game.Core.Door       Assets/Scripts/Core/Stage.cs:32  via Game.Core.ILockable
  implemented by  Game.Core.Stage      Assets/Scripts/Core/Stage.cs:12
3 references
```

**Folded asset lines** (B1a, 2026-10-11: a prefab placed 135 times in 11 wave assets took 135
lines and two cuts, 8,139 tokens, where grep's GUID search listed the 11 files). Four or more
lines with one asset label (`on`, `unity event`, `prefab instance`, `variant`, `referenced by`,
`overridden in`, `addressable entry`) from one file fold into one line where the first of them
was: the count (`19 fields`, `5 instances`, `4 overrides`, ...; after the owner and `: ` when
they all have one owner other than the file) and the file with the first and last line. It ends
`[heuristic]` when every folded edge is, `[N heuristic]` when some are. Code lines never fold.
The count line says what was folded and `--all` lists every line:

```
  referenced by    4 fields  Assets/Data/Waves.asset:10-16
14 references, 4 folded into 1 line: ug refs 'Assets/Prefabs/Turntable.prefab' --all
```

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
method named by UnityEvents gets `unity events  <file> > <owner>: <field>`. When an asset `on`
names is placed elsewhere, as a prefab instance or a variant, nested to any depth, `instances in`
lists those scenes and prefabs by path (`LabStage` on `Turntable.prefab`: `instances in
Assets/Prefabs/Turntable Variant.prefab, Assets/Scenes/Lab.unity`), so the answer names every
asset that holds the component, as `ug impact` does (O10 fix, 2026-10-11: `ProgressBar` named
only `ProgressBar.prefab`; it now also names the 5 assets `impact` lists and `GalaxyMap.unity`,
which places `MapNode.prefab`).

Lines with nothing to say are left out, except `used by` and a member's main use line (below),
which say `(none)` (an unused symbol is an answer). Header: display name, kind (the subkind for
types), base list, location, confidence. `unity` comes from the `unity` prop, the guid from `script_defines`.
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

Member lines: `in`; `overrides` (what it overrides or implements); the incoming `calls` lines by
`use`, in this order: `called from`, `read by`, `written by`, `subscribed by`, `unsubscribed by`,
`raised by`, `delegate from`; then `dispatched from`. Each source is counted when it uses the
member that way more than once in a file (edges are one per file, so the count is files). A
subscription names its other end: on an event, `Game.Lab.Spectator.Watch (Game.Lab.Spectator.OnRotated)`
(or `(lambda)`); on a handler method the lines read `subscribed to` / `unsubscribed from`,
`Game.Lab.Turntable.Rotated (Game.Lab.Spectator.Watch)`. The main line says `(none)` when empty
(nothing calls it, nothing reads it, nothing listens: an answer): a method's `called from`, an
event's `subscribed by`, a field's or property's `read by`, or `subscribed by` when it is
subscribed to or raised (a delegate or UnityEvent); other lines only when they have sources.
`calls` (outgoing `calls` and `dispatches` to methods, properties and events, deduplicated); `serialized` (with former
names) for fields and properties; `type` for fields, properties and events; `flags` for what the
header doesn't say.

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

For a game object, component, sub-asset or prefab instance (an `o:` key from an answer; a
component also as answers show it, `Run > RunDirector` or `Arena_Greybox.unity > Run > RunDirector`):

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
`overrides`, `references` (serialized object fields in the order they are saved, which is the
inspector's; not for prefab instances), `unity events`
(`field -> Method`). Separators are ASCII (` > `, `->`) so every console shows them.

### `ug outline <file> [--json]`

The shape of one file, no bodies: what to read before reading, with the lines to read.
`<file>` is a project path (relative, or absolute inside the project; `\` is accepted), or a
file name (`LabStage.cs`) resolved like `explain` resolves asset names; a `.cs` name with no
`.meta` yet (`LabStage.Debug.cs`) is found in the code graph when one indexed file has it. A
path outside the project (`../x.cs`, or absolute outside) answers `outside the project: <text>`. A file name that
several assets share is `ambiguous:` and lists them. A name that is not a file (a type, a
member) answers `not a file: <text>` with `more  ug explain <text> --members`.

For a `.cs` file (from the code graph, so partial types and nested types come out right):

```
$ ug outline Assets/Scripts/Lab/LabStage.cs
Assets/Scripts/Lab/LabStage.cs  script  1 type, 12 members  [exact]
  Game.Lab.LabStage  class   7-57   : Game.Core.Stage, Game.Lab.ILabDebug  public MonoBehaviour  partial, also in Assets/Scripts/Lab/LabStage.Debug.cs
    ResetMethod      field   10     string  const
    _turntable       field   12     Turntable  serialized
    ...
    Open             method  24-36  Open(int)  public  override
    OpenFirst        method  55-56  OpenFirst()  [ContextMenu]
```

Rows are in source order; a type's members are indented under it; nested types are rows too,
with their members one level deeper. Columns: `name`, `kind`, `lines` (`24-36`, or one line),
then the detail (a type's bases, a member's signature or type) and the same flags as
`explain --members`. When the file declares
only a part of a partial type (`LabStage.Debug.cs`), the type row says `part of
<file>:<lines>` and lists the members this file declares.

For a scene, prefab or other Unity YAML asset the answer is the object tree **from the graph**,
without Unity running; `ug tree` and `ug inspect` stay the way to see live and unsaved state
(decided 2026-10-01: an offline tree with YAML line anchors replaces a streak of `Read`
offsets on a big scene, and the graph has it already). Each row is a game object or prefab
instance, indented by hierarchy, with the file lines to read and its components:

```
$ ug outline Assets/Prefabs/Turntable.prefab
Assets/Prefabs/Turntable.prefab  prefab  2 game objects, 3 components  [exact]
  Turntable  game_object  3-16   Turntable, LabStage
    Platter  game_object  65-77  MeshRenderer  inactive
```

Prefab instances read `Lab Turntable  prefab_instance  117-145  of Assets/Prefabs/Turntable.prefab`.
A component list is cut at 6 (`(+N)`). Assets without objects (a material, a ScriptableObject)
answer one line, `no objects in <path>`, with `more  ug explain <path>`; so do assets whose
YAML the graph does not hold (binary, vendored). A file that is not in the graph answers the
standard `not found: <path>`.

Budget as everywhere: rows go in whole, then the `more` line. `--json`: `{"file", "kind",
"rows": [{"depth", "kind", "name", "lines": [start, end], "detail", "flags"}], "cut": bool}`;
rows are dropped from the end until the document fits the budget.

### `ug impact <symbol> [--json]`

What breaks if the symbol changes (renamed, removed, its signature or type changed): the code
and data that depend on it, grouped by how they depend, with counts, plus the assets that hold
serialized data for it. Direct dependents only; a caller's own callers are one more `ug impact`
away (a transitive walk would spend the budget on its first rows).

`<symbol>` resolves like `refs` (types, members, a script asset by path or name, which means the
type it defines). Overloads named without a signature are one symbol, as in `refs`. Other assets
and scene objects answer `impact is for code symbols; use ug refs <symbol>`.

```
$ ug impact Turntable._speed
Game.Lab.Turntable._speed  field  Assets/Scripts/Lab/Turntable.cs:11  serialized  [exact]
  affects        3 places in 1 file: 1 read, 2 writes; 1 asset may hold data (1 prefab); 2 overrides
  readers        1
    Game.Lab.Turntable.Rotate  Assets/Scripts/Lab/Turntable.cs:20
  writers        2
    Game.Lab.Turntable.Rotate  Assets/Scripts/Lab/Turntable.cs:19
    Game.Lab.Turntable.Stop    Assets/Scripts/Lab/Turntable.cs:23
  serialized     1 component of Game.Lab.Turntable in 1 asset  [heuristic]
    1 Turntable  Assets/Prefabs/Turntable.prefab:33
  overridden in  2
    Lab Turntable: _speed = 3      Assets/Scenes/Lab.unity:133
    Turntable Variant: _speed = 5  Assets/Prefabs/Turntable Variant.prefab:19
```

**Groups** (this order; a group with no rows is left out; a row is `<what>  <file>:<line>` and
ends `[heuristic]` when the edge is; a holder row is `<count> <type>  <file>:<line>`):

| Group | For | Rows |
| --- | --- | --- |
| `callers` | method, property, event | `calls` edges in with no `use`, or `delegate` (the method named as a delegate) |
| `readers` / `writers` | field, property | `calls` edges in with `use` `read` / `write` |
| `subscribers` | event, delegate or UnityEvent field or property, a handler method | `calls` edges in with `use` `subscribe` or `unsubscribe`: `<member>, handler <method>` on the event, `<member>, to <event>` (`from` for an unsubscribe) on the handler |
| `raised by` | event, delegate or UnityEvent field or property | `calls` edges in with `use` `raise` |
| `dispatched from` | method | `dispatches` edges in (string names; confidence of the edge) |
| `overridden by` / `implemented by` | virtual, abstract and interface members | `overrides` edges in |
| `overrides` | a member that overrides or implements one | the base members: changing the signature breaks that contract |
| `subclasses` / `extended by` | type | `inherits` edges in |
| `implemented by` | interface | `implements` edges in, and into the interfaces extending it (`X (via ILockable)`) |
| `type uses` | type | `uses_type` edges in: members and types naming it |
| `unity events` | method | `event_calls` in, with the owning object path; param-less dynamic calls are `[heuristic]` |
| `serialized` | Unity types, serialized fields | assets holding components, below |
| `held via` | `[Serializable]` types, `[SerializeReference]` subtypes | serialized fields of this type and their owners' holders, below |
| `overridden in` | serialized field | prefab-instance overrides of the field: `Instance: path = value` |
| `field data` | serialized field | `references` edges whose field path starts with the field's name |

**Serialized-data holders.** Field values are not indexed (asset-index keeps references,
overrides and UnityEvent calls only), so holders come from the component, which carries every
serialized field of its type:

- A Unity type (it has `script_defines`): the components and sub-assets `instance_of` its script
  asset, and those of every subclass, are its holders (`exact`). One row per file: the first
  component's line, the count and the type name. `affects` counts files by kind (scene, prefab,
  other asset).
- A serialized field or `[field: SerializeField]` property: the holders of its owner type are
  assets that *may* hold a value (a component saved before the field existed does not), so
  `serialized` is `[heuristic]`. `overridden in` and `field data` rows are `exact` when the
  field path matches the current name and `[heuristic]` when it matches a `former` name; a
  property's YAML name is `<Name>k__BackingField`.
- A `[Serializable]` class or struct without a script of its own: the owners reached through
  serialized fields of that type (`uses_type` from a `serialized` field), up to 3 levels, then
  their holders, as `held via Game.Lab.LabStage._data`. `exact`.
- A type that inherits or implements the declared type of a `[SerializeReference]` field: the
  field owner's holders, `held via ... (SerializeReference)`, `[heuristic]` (the instance may
  be of another subtype).

**Budget and cuts.** The header and the `affects` line always fit; `affects` is the fixed
summary (`N places in M files: 2 callers, 1 unity event (1 heuristic)` for code, the holder
counts above for data), so the shape of the impact is known from two lines. Each group shows at
most 10 rows, then `    +N more  ug refs <symbol>`. The whole answer is cut on a line like every
answer; a cut answer's `more` line says how many lines were left and names the larger budget.

`--json`: `{"symbol": [{"key", "kind", "location"}], "affects": "<the affects line>", "groups":
[{"label", "count", "rows": [{"name", "file", "line", "confidence"}], "more": n}], "cut": bool}`;
rows are dropped from the end until it fits the budget.

Not covered (ask first): transitive walks, impact of assets and scene objects, a "what to change"
plan. Field reads and writes are `readers` and `writers` since B1g (before, a note said to grep).

## Resolving `<symbol>`

1. `cs:<doc id>`, `a:<guid>`, `o:<guid>:<fileID>`: that key.
2. 32 lowercase hex characters: that GUID's asset.
3. Text with ` > `: a component as answers show it, `<object> > <component>` (`Run >
   RunDirector`, `Canvas/RoomFade > ScreenFade`), or after its scene or prefab file name as
   `explain` lists where a script is on (`Arena_Greybox.unity > Run > RunDirector`). The object
   resolves as in step 7; its components named like the last part match, case-sensitive first
   (B1b, 2026-10-11: the `on` row named the component but no command took that name). No match:
   step 4.
4. Text with a `/`: the asset at that project path (`Assets/Prefabs/Turntable.prefab`); no
   such asset: step 7.
5. Text with a `.` and no `(` is first tried as an asset file name (`Turntable.prefab`),
   case-sensitive first; several assets with that name are ambiguous. No asset: step 6.
6. Otherwise the text is a dotted name, optionally with a parameter list (`Rotate(float)` is
   matched against `sig`). Candidates are nodes named like its last segment; a candidate matches
   when its qualified name (the doc ID without prefix, parameters and generic arity marks) ends
   with the text on a `.` boundary. Case-sensitive matches win; if none, case-insensitive. No
   match (and no parameter list): step 7.
7. Scene objects: game objects and prefab instances named like the text's last `/` segment
   whose hierarchy path is the text or ends with it on a `/` (`Spawn`, `Level/Spawn`, `Lab
   Turntable/Marker`; a leading `/` is ignored). A prefab instance's path is its parent's plus its
   name. Case-sensitive matches win; if none, case-insensitive. Code symbols come first, so
   `Turntable` stays the class when a game object shares the name.
8. One match, or only overloads of one method: answer. Several: list them like `find` (at most
   20) under `ambiguous: <text>` with a `more` line repeating the command for the first by its
   qualified name (a scene object by its `o:` key), exit 0. None: `not found: <text>` followed by up to 5 `find` results for the
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

## Session hint (B1f): tried, removed

Built 2026-10-11 (c3e2fa9): in a Unity project a SessionStart hook added one line of context,
78 tokens, saying `ug refs|explain|impact <X>` answers who calls, uses, subscribes to or
implements X, what a scene or prefab wires and what breaks, in one call with file:line. It was
meant to arrive before the first tool call, where a PreToolUse note comes after Grep is chosen.
The agent check (SPEC-bench, Agent mode with the session hint) found it in every run's context
and no change in the pick: Claude takes `ug` where grep is hard and Grep where one pattern
answers. Removed before 0.9.0 (Claude's call on the user's "do whatever is best"): 78 tokens in
every session with no measured gain fails the overhead rule (SPEC Overhead budget). The skill's
listing line stays the only always-on cost. `ug` works bare in Bash because Claude Code puts a
plugin's `bin/` on the Bash tool's PATH.

## Nothing out of reach (C1)

User rule (2026-10-10): full capability; answers stay compact and say how to get the rest.
Defaults keep their size. Every query command gains two flags:

- **`--from <n>`** pages a text answer: lines 1 to `n` are skipped (the answer's own numbering,
  header included), the rest is cut to the budget as usual. A cut answer at the largest budget,
  which used to end `more  (cut at the largest budget; ask a narrower question)`, ends
  `  more  <the same command> --budget 8000 --from <next line>`. Below the largest budget the
  `more` line still names the doubled budget. Lines are deterministic, so pages join up.
  `--json` answers are cut at the budget and say `"cut": true`; the text answer pages, and
  `--from` with `--json` is refused (`--from pages text answers; --json answers are cut at the
  budget`), as is `--from` on `ug find --live` (SPEC-editor-ops: it answers whole with `--all`).
  `--from` past the end answers `(nothing past line 900: the answer has 9 lines)`.
- **`--all`** lifts the list caps:
  - `ug find`: every match, not 50 (the line is now `50+ found, narrow the text or add --kind;
    all: ug find <text> --all`);
  - `ug refs`: every edge, past the 2,000 read per query (`1999+ references: ug refs <symbol>
    --all`; the count is the rows the 2,000 edges gave). A default `refs` cut at the largest
    budget pages to that line; folded asset lines are listed one by one;
  - `ug explain`: every list item, not 8 (`(+N)`); a cut list's answer ends
    `  more  ug explain <symbol> --all | ug refs <symbol>`. `ug refs` lists incoming edges only;
    the outgoing lists (`calls`, `overrides`, `uses`, `scripts`, `components`, `references`, ...)
    are whole only here;
  - `ug impact`: every row of every group (was 10, then `+N more  ug refs`, which did not list
    `held via`, `overridden in` or `field data`; the line is now `+N more  ug impact <symbol>
    --all`); the holders of a field past 500, type closures past 200 types, and `held via` past
    3 levels (cycles are still stopped by the seen set);
  - an ambiguous symbol: every candidate, not 20 (past 20: `  +2 more candidates: ug refs Clash
    --all`), and the resolver reads every node of the name, not the first 500;
  - `ug outline`: every component of a game object, not 6, and every other file of a partial
    type, not 3 (found in the C1 re-check).
- **Silent cuts are said.** When a default answer hits the 500 holders, the 200-type closure, the
  3-level `held via` walk, the 2,000 edges read per symbol in `explain` or `impact`, the 50,000
  edges read per file (`explain` of an asset, `outline`), or an outline's 6 components or 3
  partial files, a `note` line says which (`note  cut: holders past 500, held via past 3 levels:
  ug impact <symbol> --all`). `explain` with a list cut to 8 items ends `  more  ug explain
  <symbol> --all | ug refs <symbol>`. `--json` answers carry the note as `"capped"`.
- The 2,000 and 50,000 edge reads stay as the default's time bound; with `--all` only the graph's
  size bounds a query. The outline's 100-level hierarchy walk stays: a cycle guard, as deep as no
  real scene goes.

## Performance targets

| Operation | Target |
| --- | --- |
| Any query on purge-squad, graph up to date, fresh process | p95 ≤ 500 ms |
| Symbol resolution + answer, in process | ≤ 50 ms |

## Code

`src/UnityGraph.Query`: `SymbolResolver` (text → keys), `Answer` (budgeted line builder),
`QueryEngine` (`Find`, `Refs`, `Explain`, `Outline`, `Impact` over an open `GraphStore`, returning
text or, for `Outline` and `Impact`, JSON). The CLI (`QueryCommands`) opens the project, freshens
the graph and prints. The engine never writes.

## Testing

- Golden answers for each command on `fixtures/CodeProject` (types, overloads, partial types,
  interface members, dispatch, unresolved sources, ambiguous and not-found names; assets by path,
  name and GUID, game objects, components, sub-assets, prefab instances and variants).
- Resolver table: key, dotted names, nested and generic types, `sig`, case fallback.
- Budget: a small budget cuts on a line boundary and names the larger budget; determinism.
- Freshness: edit a file, query sees it; locked graph answers with the note.
- Q2: golden answers for `outline` (a plain file, a partial type's two files, nested types, a
  prefab, a scene with instances, a material, a bare name, an ambiguous name, a type name, a
  missing file) and `impact` (method with callers, dispatch and a UnityEvent; interface member;
  type with subclasses and holders; serialized field with a former name and an override;
  `[Serializable]` type held via a field; unknown symbol; ambiguous name; an asset). Budget cuts
  and the 10-row group cap, JSON for both (parsed back, within budget), determinism.
- Performance: env-gated purge-squad run (`UG_PURGE_SQUAD`) records p95 in Measured, Q2 commands
  included (`PurgeSquadQueryTests` for `outline` and `impact`, in process, warm).
- B1 fixes (2026-10-11): golden answers for `find` by GUID (either case, and under another `--kind`),
  a component by `object > component` and with its file (and one in no such file), `refs`,
  `explain` and `impact` of an interface with an implementer through an extending interface;
  `refs` folding four asset lines from one file and `--all` listing them.
- Coverage ≥ 80% lines on Query.

## Boundaries

- Always: read-only on the project; the only write is the graph write through `GraphUpdate`.
- Ask first: a new command; `--json` on `find`, `refs`, `explain` (allowed on `outline` and
  `impact` by the Q2 task); changing the line format once the skill documents it.
- Never: network; answers without a location; more than the budget.

## Measured

purge-squad, 2026-09-29, a copy of its index inputs, Release build, fresh process per query,
graph up to date, 21 runs per query (`find Gun`, `refs LabStage`, `explain LabStage`,
`explain LabStage --members`, `refs MonoBehaviour`, `find Update --kind method`): medians
268–304 ms, p95 324–363 ms, overall p95 345 ms (target ≤ 500 ms: met). About 260 ms of that is
process start plus the freshness check. Answer sizes: 6 to 1,165 tokens (`find Update` hits the
50-result cap within the 1,200 budget).

Q2, purge-squad, 2026-10-01, dev machine (Windows 11, Debug build, in process, graph in a temp
folder, second of two runs, 2 runs of the env-gated `PurgeSquadQueryTests`): `outline` of the
largest own script (`MapShapes.cs`, 22 KiB, 49 lines of answer) 2-4 ms, of the largest own scene
(`GalaxyMap.unity`, 633 KiB, 53 lines) 2-3 ms, of the largest own prefab (`HubStation.prefab`,
722 KiB, 42 lines) 2-5 ms; `impact` of `MapShapes` and two of its nested types 0-3 ms (6-16
lines). Far inside the 50 ms in-process target; a fresh `ug` process stays at the ~260 ms start
and freshness cost above. Vendored assets (`Assets/3rd Party`) are not in the asset graph:
`outline` answers `not found:` for them.
