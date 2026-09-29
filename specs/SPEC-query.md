# Spec: query

Part of unitygraph (see `../SPEC.md`). Status: **draft** (2026-09-29). Reads the store described
in `SPEC-graph-core.md`, kept fresh by `SPEC-code-index.md`.

## Objective

Answer "where is X", "who uses X" and "what is X" about a Unity project's C# in one `ug` call,
within the answer budget, with `file:line` anchors and a confidence tag, so Claude's first
navigation call is the graph call instead of a grep-and-read streak (design rules 1, 2, 6).

Pulled forward (user, 2026-09-29): this first version covers the code graph only. Asset facts
("lives on", serialized values, prefab wiring) join `explain` when `asset-index` lands; the
command names and line format stay.

## Scope

In: `ug find`, `ug refs`, `ug explain` (with `--members`); symbol resolution; answer budgets and
`more` handles; bringing the graph up to date before answering; the `unity` skill's command
table.

Out (later modules): `impact`, `outline`, `batch` (SPEC module table, after asset-index);
`--json`; asset and scene facts; reindex from hooks (step 3, `guards`/reindex-on-edit).

## Commands

All take `--project <dir>` (default: current folder) and `--budget <tokens>` (200 to 8000;
default `answerBudget` from `.claude/unitygraph.json`, 1,200). Plain text, one fact per line,
paths project-relative with `/`. Exit 0 for any answer including "nothing found"; 1 only when
there is no Unity project, the config is invalid, or no graph can be built.

### `ug find <text> [--kind type|method|field|property|event]`

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

## Resolving `<symbol>`

1. `cs:<doc id>`: that key.
2. Otherwise the text is a dotted name, optionally with a parameter list (`Rotate(float)` is
   matched against `sig`). Candidates are nodes named like its last segment; a candidate matches
   when its qualified name (the doc ID without prefix, parameters and generic arity marks) ends
   with the text on a `.` boundary. Case-sensitive matches win; if none, case-insensitive.
3. One match, or only overloads of one method: answer. Several: list them like `find` (at most
   20) under `ambiguous: <text>` with a `more` line repeating the command for the first by its
   qualified name, exit 0. None: `not found: <text>` followed by up to 5 `find` results for the
   last segment.

## Freshness

Before answering, the query brings the code graph up to date with `CodeIndex.Update` (design
rule 5: a stale answer is worse than grep). A new graph is built there, and the answer starts
with one line `indexed N files in S s`. If another writer holds the lock (1 s timeout), the
query answers from the graph as it is and ends with `note: graph busy, edits from the last
seconds may be missing`. `code-index` off in config: `code-index is off; nothing to query`.

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
  interface members, dispatch, unresolved sources, ambiguous and not-found names).
- Resolver table: key, dotted names, nested and generic types, `sig`, case fallback.
- Budget: a small budget cuts on a line boundary and names the larger budget; determinism.
- Freshness: edit a file, query sees it; locked graph answers with the note.
- Performance: env-gated purge-squad run records p95 in Measured.
- Coverage ≥ 80% lines on Query.

## Boundaries

- Always: read-only on the project; the only write is `CodeIndex.Update`'s graph write.
- Ask first: a new command; `--json`; changing the line format once the skill documents it.
- Never: network; answers without a location; more than the budget.

## Measured

purge-squad, 2026-09-29, a copy of its index inputs, Release build, fresh process per query,
graph up to date, 21 runs per query (`find Gun`, `refs LabStage`, `explain LabStage`,
`explain LabStage --members`, `refs MonoBehaviour`, `find Update --kind method`): medians
268–304 ms, p95 324–363 ms, overall p95 345 ms (target ≤ 500 ms: met). About 260 ms of that is
process start plus the freshness check. Answer sizes: 6 to 1,165 tokens (`find Update` hits the
50-result cap within the 1,200 budget).
