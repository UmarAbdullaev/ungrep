# Spec: guards

Part of ungrep (see `../SPEC.md`, module table). Status: **built 2026-10-10** (G1; cases and
modes decided with the user 2026-10-01; mechanism check still pending, see Mechanism). Builds on
`graph-core`, `code-index`, `asset-index`, `query` (holder counts) and the hook dispatcher of
`plugin-shell`.

## Objective

Some edits cost a rework round because Unity's serialized data is invisible to the text that
Claude edits: renaming a serialized field drops the values saved in scenes and prefabs, moving a
`.cs` file without its `.meta` orphans every reference to it, an edit inside `Assets/Plugins`
disappears with the next package update. The graph already knows what holds the data, so the
hook can say so before the edit lands, in at most five lines, and only when it matters.

Five cases: a serialized field renamed or removed, a `[SerializeReference]` type renamed or
moved, `.meta`-unsafe `mv`/`cp`/`rm`, edits to vendored folders, and a runtime-lookup lint the
user configures. All of them **warn** by default (user decision, 2026-10-01: spike S3 measured
deny plus an answer being retried in 9 of 10 runs, so a deny costs a turn even when the edit was
meant); each can be switched to `deny` or `off` in the config.

## Scope

In: one PreToolUse handler (`GuardsHandler`, module `guards`) with five cases; the config section
`guards`; a once-per-session repeat filter; holder counts from `query`; unit, golden and replay
tests.

Out: any case that writes or repairs something (guards never change a file, scene or prefab);
shell writes into code (`sed -i`, `>` redirects, Python heredocs: the edit tools are guarded, a
shell program is not parsed); a deny for anything the user did not choose; interception of Grep and
Read (SPEC Decisions #4); deleting a referenced asset (that is what `rm` was asked to do; `ug
refs` answers it on request); field *type* changes and `[SerializeField]` removal (data may stay
but is ignored; ask first to add).

## Mechanism

Wired in `hooks/hooks.json` already: PreToolUse on `Bash|PowerShell|Edit|MultiEdit|Write|NotebookEdit`
calls `ug hook PreToolUse`. `GuardsHandler.Handles` is true for those tools; the dispatcher gives
it 2 s and fails open (design rule 9). The handler prints one JSON object or nothing.

| Decision | Output | Effect |
| --- | --- | --- |
| silent | nothing, exit 0 | the call proceeds as if no hook existed |
| warn | `{"hookSpecificOutput":{"hookEventName":"PreToolUse","additionalContext":"<note>"}}` | the call proceeds, the model reads the note before it sees the result; no `permissionDecision`, so the user's permission prompts are untouched |
| deny | `{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"<note>"}}` | the call is blocked and the model reads the note |

Never `permissionDecision: "allow"`: it would skip the user's own prompts.

**Unverified:** that Claude Code 2.1.x passes PreToolUse `additionalContext` to the model is taken
from its hooks reference, not yet exercised live. A scripted `claude -p` check (stub hook, scratch
folder, 2 calls) would settle it; it waits for the user's go (asked 2026-10-01, no answer to the
approval itself). If the field is ignored, a warn does nothing visible and `deny` stays the only
channel; the fix is then to emit the note as `systemMessage` for the user or to let the user pick
`deny`. Nothing else in the design depends on it.

Shared rules:

1. **Graph only, read only.** Cases read the graph through `GraphStore` opened read-only, the
   project config, and the call's own input; never the project's assets or scenes. The only files
   a guard reads besides the graph are the `.cs` file being edited (to find where `old_string`
   sits; the lint's `Write` compares with it), a moved script (for `[MovedFrom(`), and the
   `.asmdef`/`.asmref` files above a moved script and its destination. The only file it writes is
   the repeat marker below.
2. **Silent when it can't know.** No graph (never indexed), the edited script changed since it
   was indexed (size differs from the graph's file record, or mtime differs and the hash too), a
   payload without the fields a case needs, a path outside the project, a tool call that would
   fail anyway (`old_string` empty, not found, or not unique without `replace_all`), a shell path
   that needs the shell to resolve it (a variable, a glob, a `cd` that can't be followed): no
   output. A full rebuild writes `graph.db.new` and readers keep the old graph (graph-core), so
   during a rebuild the guards answer from the last complete graph, still behind the freshness
   check. The next index (`ug index`, any query) makes the graph current again.
3. **Silent when nothing is at stake.** A field renamed that no asset holds data for, a moved file
   nothing references, a copy outside the project: no output.
4. **At most five lines**, plain ASCII, first line `ungrep: ...`, project-relative paths with
   `/`, counts exact where the graph is exact and "up to N" / "may hold" where it is not (a
   component saved before a field existed holds no value for it). The five-line bound stays under
   C1 (no hard caps) as a bound on the hook's output: a note's `(+N more)` counts lines of the
   edit or paths of the copy the agent itself just made, and case 2 names `ug impact <Type>`.
5. **Once per session and subject.** A note is printed the first time and not again for the same
   case and subject (the file and members of case 1, the type of case 2, the paths of case 3, the
   folder of case 4, the file and lines of case 5) in the same session; the session's fired keys
   are in `Library/ungrep/guards-<session id>` (`SessionMarker`, shared with the compile
   gate's `gate-<session id>`). A deny is never filtered: the retried call must meet it again.
   A deny's reason is the note plus one line: `  Blocked by guards.<case> = "deny" in
   .claude/ungrep.json: ask the user before doing this anyway.` (`guards.lint.mode` for the lint).
6. **Switches.** `modules.guards: false` turns the whole handler off; each case has a mode.
   Dispatcher timeouts, parse errors and exceptions are logged to `hook-errors.log` and print
   nothing.
7. **Holders** (who holds serialized data of a type or field): the components and sub-assets that
   are `instance_of` the script asset that `script_defines` the owner type or one of its subclasses
   (transitive `inherits`), excluding prefab instances; for a `[Serializable]` type, the holders
   of the types that hold it in a serialized field (to `ug impact`'s depth); and the holders of
   `[SerializeReference]` fields typed with one of its bases. `QueryEngine.DataHolders` (public,
   built on `ug impact`'s holder walk) returns the file list; guards print counts by kind: `N
   assets (A scenes, B prefabs, C other)`. Field values are not in the graph, so a field's holders
   are those of its owner type, tagged "up to".

## Config

In `.claude/ungrep.json` (a new top-level `guards` object; unknown keys are an error, as
elsewhere):

```jsonc
"guards": {
  "field-removed": "warn",          // "warn" (default) | "deny" | "off"
  "serialize-reference": "warn",
  "meta": "warn",
  "vendored": "warn",
  "lint": {
    "mode": "warn",                 // "warn" | "deny" | "off"
    "patterns": [                   // default: none, so the lint is silent until you add some
      { "pattern": "GameObject\\.Find\\(", "message": "look it up once and cache it, or serialize a reference" }
    ]
  }
}
```

`UngrepConfig.Guards` exposes the parsed section. A bad value names the setting
(`guards.meta must be "warn", "deny" or "off"`; `guards.lint.patterns[0].pattern is not a valid
regex: ...`).

## Cases

Each case: the trigger, what it reads, the exact note, the decision and how to turn it off.
`<root>` is the project; paths in notes are relative to it. Counts are examples from
`fixtures/CodeProject`.

### Case 1: serialized field renamed or removed (`field-removed`)

**Trigger.** PreToolUse `Edit`, `MultiEdit` or `Write` whose `file_path` is a `.cs` file under
`Assets/` or `Packages/` that is not vendored (vendored paths are case 4's) and is in the graph.

**Reads.** The graph's nodes of that file (`KeysInFile`): fields and properties with `serialized`
(and the `former` names they already carry), with their line ranges; the file's current text.
Holders of the owner types (shared rule 7). Freshness of the file (rule 2).

**Fires** when an edit's `old_string` (located in the file text; for `MultiEdit` each edit in
order) overlaps a serialized member's lines and the matching `new_string` no longer contains the
member's name as an identifier (`\bName\b`), or when a `Write` leaves out a serialized member's
name altogether, **and** the owner type has holders. A new text that still contains the old name
anywhere, including as `[FormerlySerializedAs("name")]`, does not fire: renaming with the
attribute is the safe form, and it is the cheap, honest test of it. Known miss: the old name still
used in the replacement text (a read of the field) hides a removal.

**Note** (three lines; `ug impact` names the exact holders):

```
ungrep: Turntable._speed is serialized and this edit renames or removes it.
  Saved values would be lost: up to 1 asset (1 prefab) holds data of Game.Lab.Turntable (ug impact Turntable._speed).
  To keep them, put [FormerlySerializedAs("_speed")] on the new name; to drop them on purpose, ignore this note.
```

Several members: the first by name, then ` (+2 more: a, b) are serialized and this edit renames
or removes them.` on line 1, `and N more types` after the owner when they belong to several, and
`on each new name, with its old name` on line 3. A property says `<Name> property`, and line 3
names its YAML key: `[field: FormerlySerializedAs("<Tint>k__BackingField")]` (a plain
`[FormerlySerializedAs("Tint")]` on an auto-property does not carry the data over).

**Decision.** warn (default), deny or off with `guards.field-removed`. A deny's reason is the same
text.

### Case 2: `[SerializeReference]` type renamed or moved (`serialize-reference`)

**Trigger.** (a) Edit/MultiEdit/Write of a `.cs` file as in case 1; (b) `mv`, `git mv`, `Move-Item`
of a `.cs` file in a shell call (parsed as in case 3).

**Reads.** The top-level types declared in the file that can have instances (classes, structs and
records that are neither abstract nor static: an interface or abstract class is never the stored
type, so renaming one orphans nothing); for each, the graph's `serialize_reference` fields whose
declared type (`uses_type`) is the type itself or one of its bases or interfaces (transitive
`inherits`/`implements`), their owners' holders (rule 7), and the assembly of the source and
destination folders. The graph has no `in_assembly` edges, so the assembly is read as Unity
assigns it: the nearest `.asmdef` (its `name`) or `.asmref` (its `reference`, by name or
`GUID:` through the graph) at or above the folder, up to `Assets` or the package root; else
`Assembly-CSharp`, `-firstpass` under `Assets/Plugins`, `Assets/Standard Assets` and `Assets/Pro
Standard Assets`, `-Editor` under an `Editor` folder; a package folder without one is not compiled
and stays silent.

**Fires** for (a) when an edit's old text declares the type (`class|struct|interface|record Name`)
and the new text no longer names it (rename), or the old text has `namespace X` and the new text
does not (a `Write` counts as replacing the whole file), and for (b) when the destination folder
belongs to another assembly; both only when the new text (the moved file for (b)) lacks
`[MovedFrom(`, and when the owner fields' holders are not empty. Unity stores a
`[SerializeReference]` value by assembly, namespace and class name, so a rename or move orphans
every saved instance; nothing else breaks loudly.

**Note** (from the fixture):

```
ungrep: Game.Core.SpinBehaviour is stored by name in [SerializeReference] fields (Game.Lab.Spinner._behaviour).
  Renaming it or changing its namespace or assembly orphans saved instances: up to 1 asset (1 prefab) may hold one (ug impact SpinBehaviour).
  Add [MovedFrom(true, sourceNamespace: "Game.Core", sourceAssembly: "Game.Core", sourceClassName: "SpinBehaviour")] to carry them over.
```

For (b) line 2 is `Moving the file to <assembly> changes its assembly and orphans saved
instances: ...` and line 3 names only the source assembly: `Add [MovedFrom(true, sourceAssembly:
"Game.Core")] to carry them over.` Several fields: the first, then `(+N more)`.

**Decision.** warn, deny or off with `guards.serialize-reference`.

### Case 3: `.meta`-unsafe `mv`, `cp`, `rm` (`meta`)

**Trigger.** PreToolUse `Bash` or `PowerShell` whose command, split into simple commands
(`&&`, `||`, `;`, `|`, newlines; here-document bodies skipped; quotes honoured), contains a move,
copy or remove: `mv`, `git mv`, `cp`, `rm`, `git rm`, `Move-Item` (`mi`, `move`), `Rename-Item`
(`rni`, `ren`), `Copy-Item` (`cpi`, `copy`), `Remove-Item` (`ri`, `del`, `rd`, `rmdir`, `erase`).
Only commands whose paths are literal count: an argument with `$`, a backtick, `*`, `?`, `[`, `~`,
or `{` makes that command silent (a shell variable or glob can't be resolved without running it).
Relative paths resolve against the payload's `cwd`, followed through `cd`, `pushd`, `Set-Location`
and their aliases; after a `cd` that can't be followed (no argument, `-`, a variable, `popd`),
later relative paths are skipped. Git Bash paths (`/d/Projects/x`) are read as `D:/Projects/x` on
Windows. Only paths inside `Assets/` or `Packages/` count.

**Reads.** `<path>.meta` file records and the asset's GUID (`KeysInFile("<path>.meta")` gives the
`a:<guid>` node), references into it (`EdgesIntoAsset`, distinct files), files tracked under a
folder (`FilesUnder`, new).

**Fires**, three subcases, each only with the graph knowing the asset:

- **a. Move without the meta.** The sources of a move include `X` (a file or folder) and not
  `X.meta`, `X.meta` is tracked, and references into `X`'s GUID are not empty. Unity then gives `X`
  a new GUID at the destination.
- **b. Meta removed alone.** A remove of `X.meta` without `X` in the same command, `X` tracked
  and referenced: the next import gives `X` a new GUID.
- **c. Copy that duplicates GUIDs.** A copy whose source is a `.meta` file, or a folder (recursive
  flag or a directory source) with at least one tracked `.meta` under it, and whose destination
  is inside `Assets/` or `Packages/`: the copies keep the GUIDs of the originals.

Not firing: `rm X` without `X.meta` (Unity deletes the orphan meta itself), a copy of a single
asset file without its meta (Unity assigns a new GUID, which is what the user wants), a copy to a
destination outside the project (a backup), a move of both files.

**Notes:**

```
ungrep: this mv leaves Assets/Prefabs/Turntable.prefab.meta behind.
  Assets/Prefabs/Turntable.prefab is referenced by 3 assets (ug refs Turntable.prefab); at the new path Unity gives it a new GUID and they lose it.
  Move the .meta with it: mv Assets/Prefabs/Turntable.prefab.meta Assets/Prefabs/Old.prefab.meta
```

The fix names the `.meta`'s own target (inside the destination folder when the move goes into one;
`Rename-Item X.meta <new name>.meta` for a rename). Several assets left behind: the first, then
` (+N more .meta files)` on line 1. The verb is the one typed (`git mv`, `Move-Item`).

```
ungrep: this rm deletes Assets/Prefabs/A.prefab.meta but keeps the asset.
  Assets/Prefabs/A.prefab is referenced by 2 assets (ug refs A.prefab); Unity would give it a new GUID and they lose it.
  Remove both files, or neither.
```

```
ungrep: this cp copies 14 .meta files under Assets/Art/Icons (a folder copy copies metas).
  The copies share their GUIDs with the originals, so references resolve to either at random.
  Copy the files without the .meta files and let Unity import them, or duplicate the folder in the Project window.
```

A copy of `.meta` files named on the command line says `this cp copies Assets/X.png.meta (+N
more).` on line 1. Folder `.meta` files are not in the graph (asset-index), so a folder moved or
removed without its `.meta` is not covered.

**Decision.** warn, deny or off with `guards.meta`. Subcase c is the one that is always a mistake
(a duplicate GUID has no good reading), so a user who wants "deny only where it is surely wrong"
sets `meta` to `deny` and relies on the filters above; nothing else is split.

### Case 4: edits to vendored folders (`vendored`)

**Trigger.** PreToolUse `Edit`, `MultiEdit`, `Write` or `NotebookEdit` whose `file_path`, resolved
against `cwd`, is inside the project and inside a vendored folder (`vendored` in the config, or
`VendoredFolders.Default`: `Assets/Plugins/`, `Assets/3rd Party/`, `Assets/3rdParty/`,
`Assets/Third Party/`, `Assets/ThirdParty/`, `Assets/Vendor/`, `Assets/AssetStore/`, `Packages/`).

**Reads.** The config only; no graph, so this case also works before the first index.

**Note:**

```
ungrep: Assets/Plugins/Foo/Bar.cs is in a vendored folder (Assets/Plugins/); local edits are lost when the package is updated.
  Prefer a wrapper, subclass or partial class outside it. If this edit is intended, set guards.vendored to "off"
  in .claude/ungrep.json, or take the folder out of `vendored`.
```

**Decision.** warn (user decision, 2026-10-01; SPEC Boundaries say Never edit vendored code, so
the note says how to allow it), deny or off with `guards.vendored`. `.meta` files and shell
writes are not covered. The subject is the folder, so a session hears it once per vendored folder
(a deny every time).

### Case 5: runtime-lookup lint (`lint`)

**Trigger.** Edit/MultiEdit/Write of a `.cs` file under `Assets/` that is not vendored, when
`guards.lint.patterns` is not empty.

**Reads.** The config patterns (regular expressions, .NET syntax, each with an optional
`message`) and the call's text. No graph.

**Fires** for the added lines: lines of `new_string` (or of the `Write` content) that are not
already a whole line of `old_string` (or of the file on disk), trimmed, that match a pattern.
Lines that are comments (`//`) are skipped. Up to 3 hits are printed; with more, 2 and `(+N
more)`, so a deny's extra line keeps the note within five lines. A line is clipped to 80
characters; a pattern without `message` prints the line alone. A pattern that runs past its 50 ms
match timeout counts as no match.

**Note:**

```
ungrep lint: Assets/Scripts/Lab/LabStage.cs, added lines match your guards.lint.patterns:
  GameObject.Find("A");   look it up once and cache it, or serialize a reference
  SendMessage("C");
  (+2 more)
```

**Decision.** warn (default), deny or off with `guards.lint.mode`. Patterns default to none: the
lint is silent until the user adds some (user decision, 2026-10-01).

## Parsing a shell command (case 3)

`ShellCommand.Operations(text, powerShell)` returns the moves, copies, removes and directory
changes in order. `Split` makes simple commands into word lists: single and double quotes group a
word, `\` escapes in Bash (a `` ` `` in PowerShell) and before a newline continues the line, `;`
`&&` `||` `|` `(` `)` and newline separate commands, `<<WORD` / `<<'WORD'` / `<<-WORD` skip their
body (Bash), `#` starts a comment at the start of a word. Leading `sudo`, `command`, `env` and
`NAME=value` words are skipped; a verb counts as the first word of a simple command. Flags (words
starting with `-`) are dropped except the recursive flags (`-r`, `-R`, `-a` in Bash, `-Recurse`);
`--` ends the flags; `-t`/`--target-directory` makes the command unreported. Redirects (`> log`,
`2>/dev/null`) are not paths. PowerShell's `-Path`, `-LiteralPath` (comma lists too),
`-Destination`, `-NewName` are read, and the values of `-Filter`, `-Include`, `-Exclude`,
`-ErrorAction` and the other common value parameters are skipped; positional paths otherwise.

## Code

`src/Ungrep.Hooks`: `GuardsHandler` (registered in `HookHandlers.All`; `Check` runs the cases
without the repeat filter, for the replay), `GuardContext` (what one call's guards share: paths,
shell operations, the graph opened read-only on first use), `GuardNote` and `GuardOutput` (the
text and its warn/deny rendering), `EditCall` (an edit call's replacements, applied and located
as the tools match them), `FieldGuard`, `SerializeReferenceGuard`, `ScriptAssembly`,
`MetaGuard`, `VendoredGuard`, `LintGuard`, `ShellCommand`, `GuardSession` and `SessionMarker`
(the repeat marker). `src/Ungrep.Core`: `GuardSettings`, `UngrepConfig.Guards`,
`GraphStore.GetFile` and `FilesUnder`. `src/Ungrep.Query`: `QueryEngine.DataHolders`,
`SerializeReferenceUsers`, `SerializedMembers`, `TypesIn`, `AssetGuid`, `AssetReferrers`.

## Testing

- **Fixture cases** (`fixtures/CodeProject` copied and indexed, payloads built like recorded
  ones): each case has a firing payload with its exact note (golden), a silent twin (the safe form),
  and the "nothing at stake" twin. Case 1: rename without and with `[FormerlySerializedAs]`, a
  removal, a `Write` that drops a field, a property, a field no asset holds. Case 2: a rename, a
  namespace change, with `[MovedFrom]`, an `mv` into another asmdef. Case 3: every subcase and every
  "not firing" line above, bash and PowerShell, quoting, `&&` chains, a heredoc that mentions `rm`.
  Case 4: each default folder, a custom one, `Packages/`, a path outside the project. Case 5:
  patterns, comments, added versus unchanged lines.
- **Silence:** no graph, a stale file, an outside path, a payload without `tool_input`, an
  `old_string` that is not in the file: empty output, exit 0. (A rebuilding graph is the last
  complete graph to readers, rule 2, so it has no silent case of its own.)
- **Modes:** `deny` renders the deny JSON, `off` is silent, `modules.guards: false` is silent, a
  bad config value fails open and logs.
- **Repeat filter:** the second identical warn in a session is silent, a deny is not, another
  session id prints again.
- **Replay** (env-gated: `UG_PURGE_SQUAD` names the project; the recorded calls are read from
  `~/.claude/projects/D--Projects-Unity-purge-squad/*.jsonl`, or `UG_PURGE_SQUAD_TRANSCRIPTS`):
  every recorded `Edit`, `MultiEdit`, `Write`, `Bash` and `PowerShell` call of those sessions
  (subagents' transcripts too) becomes a PreToolUse payload and goes through the cases
  (`GuardsHandler.Check`) against the project's graph, kept in a temp folder, with the
  once-per-session filter kept in memory, so nothing is written into the project. The test fails
  on any `deny` and prints every note it gets and every shell operation under `Assets/` or
  `Packages/`; success criterion 5 is that each note printed is a real mistake (judged by reading
  them, recorded in Measured). Calls whose `old_string` no longer sits in today's file are counted
  as `not replayable`, not as silent passes; so are case-1 notes on a `Write` whose missing fields
  were all added after the call (none of them is in the file at the project's last git commit
  before the call's time).
- Coverage: at least 80% of lines in the guard classes.

## Boundaries

- Always: read only; silent unless it fires; fail open; at most five lines; the warn default.
- Ask first: a new case; a `deny` default; any write other than the repeat marker; a new hook
  event.
- Never: change a file, scene, prefab or `.meta`; call Unity; touch the network; block because the
  graph is missing or stale; print a note for something nothing depends on.

## Measured

2026-10-10, G1 build (Windows 11, .NET 8, Debug).

- **Tests.** 807 in the solution pass (6 env-gated skips); the hooks project has 104: every case's
  firing note, its safe and nothing-at-stake twins (golden `GuardsHandlerTests.Notes_for_each_case`,
  `Lint_notes`, `Warn_and_deny_outputs`), 42 shell-parsing cases, assembly resolution, silence,
  modes, the module switch and a bad config through the dispatcher, the repeat filter, Git Bash
  paths. Line coverage of the guard classes (`*Guard*`, `EditCall`, `ShellCommand`,
  `ScriptAssembly`, `SessionMarker`): 794 of 835, 95.1%.
- **Replay** on purge-squad (44 transcripts, 2026-09-20 to 10-09): 1949 guarded
  calls, 22 of them shell moves, copies or removes under `Assets/`. Not replayable: 268 edits whose
  `old_string` is gone from today's file, and 41 case-1 notes on `Write` calls whose missing
  fields were all added later (checked against git). **0 notes, 0 denies.** So criterion 5 holds
  trivially: nothing printed was a false alarm; the replay does not show a catch either.
  - All 22 shell operations were the safe forms (`rm`/`git rm` of asset and `.meta` together,
    folder removes whose folder `.meta` is not indexed). One near-miss the replay cannot judge:
    `git mv Assets/Scripts/Weapons/EnemyDef.cs Assets/Scripts/Combat/` and its `.meta` were moved
    in two separate calls a few seconds apart; live, the first call would have warned (the script
    is referenced), which is correct but redundant since the next call fixed it. Today's tree has
    no `Weapons/EnemyDef.cs`, so the replay is silent there.
  - None of the 79 `Edit` calls still replayable (scripts and other files) renamed or removed a
    serialized member that assets hold, and none touched a `[SerializeReference]` type.
- **Time** in-process per call, graph open included: median 0.0 ms (most calls never open the
  graph), p99 6.1 ms, max 16.4 ms; the hook budget is 2 s. Process start of `ug hook` comes on top.
- **Known limits.** After an edit lands, the script is stale until the next index (no
  reindex-on-edit hook yet), so a second edit of the same script in a row is silent (rule 2).
  PreToolUse `additionalContext` reaching the model is still unverified (Mechanism); a deny is the
  channel known to work.
