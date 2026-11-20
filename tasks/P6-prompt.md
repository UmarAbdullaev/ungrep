# P6: no hard caps (C1), then the three-way measurement (O10)

Repo: `D:\Projects\unitygraph`. Read first: `SPEC.md` (Overhead budget, Decisions, Boundaries),
`specs/SPEC-editor-ops.md`, `specs/SPEC-editor-link.md`, `specs/SPEC-query.md`,
`specs/SPEC-play-input.md`, `tasks/todo.md`, your memory files, and `tasks/P1-prompt.md` Part 2.

## Why
User rule (2026-10-10): full capability, nothing left out to save tokens; answers stay compact
with `more` handles. An audit on 2026-10-11 found caps that cut content no `ug` command can
reach (only `ug eval` could). C1 gives each one an escape. It runs before O10 because O10
measures answer sizes: measure the final shape once.

## Rules
- Defaults stay as they are: the short answer is what keeps `ug` cheap. C1 adds escapes (a flag,
  a `more` line naming the command, paging, a file the answer names), and the cut answer always
  says how to get the rest. A cap that only bounds memory or transport (console ring size,
  request body, profiler frames, picture pixels) may stay; say so in the spec.
- Spec first (no review stop), in the spec that owns each command; tests for every escape
  (EditMode for the bridge, client/CLI tests, the `UG_UNITY` end to end where it already covers
  the command); `dotnet format`; tick `tasks/todo.md` (add C1 under "Unity MCP gaps" first,
  numbers when done); one commit, O8 style, no attribution trailer, no Co-Authored-By.
- Bridge: one version bump for C1 (0.9.0), `ug` refuses the new flags on an older bridge by name.
  Reinstall in `fixtures/BridgeHost` and purge-squad (`ug bridge install --copy`, `ug compile`).
- Skill: new flags go in `--help`; a new command gets one line in `skills/unity/SKILL.md`, body
  under 600 tokens (the budget test checks it).
- Ask before: a graph schema version bump (storing longer index values may or may not need one:
  check, then ask), any new package, any network call other than the docs.unity3d.com fallback,
  a permanent delete path. Anything the spec does not cover: ask, do not invent. Questions from
  the user are not approvals.
- Do not commit `tasks/*-prompt.md`, `specs/SPEC-viewer.md` or the user's `skills/unity/ui.md`
  change. LF files; TreatWarningsAsErrors; follow the surrounding style.
- EditMode runs in `fixtures/BridgeHost`, batch mode (also once without `-nographics` so the
  shot tests run), Unity via PowerShell `Start-Process` with absolute `-testResults`/`-logFile`;
  e2e needs `UG_UNITY="D:\Program Files\Unity Editors\6000.3.14f1\Editor\Unity.exe"`. Test
  executables run directly (`artifacts/bin/<Proj>/release/<Proj>.exe`; `dotnet test` runs zero).
- Do not restart or close the user's open editors (purge-squad, captain_of_space).
- Replies terse; every status message ends with x/y tasks done or %.

## Part 1: C1, an escape for every hard cap
B/ is `unity/com.unitygraph.bridge/Editor/`, S/ is `src/`. Most likely to bite first.

1. Console: the CLI prints the first line, 157 chars (S/UnityGraph.Cli/EditorCommands.Console.cs:15,
   125-129); the bridge keeps 2000 chars and one own stack frame, never the trace
   (B/ConsoleBuffer.cs:17,58; B/Bridge.cs:156; B/StackFrames.cs:32); only the newest 500 entries
   after `--after` are reachable (B/Bridge.cs:234). Needed: the full message and stack kept per
   entry (a size bound per entry is fine), one entry shown whole by its id, older entries paged.
   Same for `ug log` and `play --for`.
2. Test failures: first line, 160 chars, one frame; past 20 only a count
   (B/TestRuns/TestRuns.cs:17; S/UnityGraph.Cli/EditorCommands.Test.cs:15,89). Needed: every
   failure listed on request, one failure's full message (NUnit's "But was:" line) and stack.
3. `ug run`/`ug eval` exceptions: first line, 300 chars, one frame, no inner exception
   (B/Ops/CodeRun.cs:27-28,175,266,320). Needed: the full exception chain and stack (a file the
   answer names, as `result.txt` already does for values, is fine).
4. Compile errors past 20: `(+N more errors)` with no way to see them in `ug`
   (S/UnityGraph.EditorLink/CompileText.cs:12,110; full list only in
   `Library/unitygraph/compile.json`). Needed: `ug compile` shows all on request; the Stop hook
   stays at 20 and names the command.
5. `ug inspect` values: arrays, lists and curve keys past 10, structs nested past one level
   (`{...}`), more than 150 lines with no `more` line for materials (B/Ops/Values.cs:18,167,
   178-183,246; B/Ops/Values.Compound.cs:26; B/Ops/ReadOps.cs:18,294,324). Needed: one field by
   property path, whole, with paging for long arrays; a `more` line wherever lines are cut.
6. Graph: `ug explain` outgoing lists past 8 (calls, asset uses, scripts, prefabs, object
   components, overrides, references: S/UnityGraph.Query/Names.cs:107; QueryEngine.cs:557-563;
   `ug refs` lists incoming only); `ug impact` rows past 10 for "held via", "overridden in",
   "field data" (QueryEngine.Impact.cs:8,457-460; HolderLimit 500, ClosureLimit 200, ViaDepth 3
   cut silently); answers past the 8000-token budget and refs past EdgeLimit 2000 have no paging
   (UnityGraphConfig.cs:15-17; Answer.cs:64-78; QueryEngine.cs:13). Needed: outgoing lists and
   every impact group in full on request, paging past the budget, silent cuts said.
7. UI Toolkit: element text, value and tooltip cut at 30 chars; choices past 5
   (B/Ops/PlayInput.Ui.cs:46-55; B/Ops/UiToolkit.Read.cs:226,248; B/Ops/UiToolkit.Input.cs:18,318,
   345,372). Needed: whole in `ug inspect <element>`.
8. Prefab instance overrides: only the apply/revert report shows them, 40 lines
   (B/Ops/PrefabOps.cs:18,318). Needed: a read-only listing (check MCP `manage_prefabs` for the
   matching action and name the parity in the spec).
9. Index losses: SendMessage receivers past 20 are not stored (S/UnityGraph.Code/FileIndexer.cs:
   521-524); asset paths past 200, override values past 80, labels past 10 cut silently
   (S/UnityGraph.Assets/AssetFileIndexer.cs:22-24,145). Needed: stored whole or marked as cut;
   ask before a schema bump.
10. Rare: `ug ui --all` stops at 150 (B/Ops/PlayInput.Ui.cs:64-65); 20 selected objects
    (B/Ops/EditOps.cs:22); play digest names 3 changed objects and none past 20000 objects
    (B/Ops/Digest.cs:18-21); 25 overloads, 4 constructors and name budgets in `ug api`
    (B/Ops/ApiOps.cs:26-28,329-360); 10 index warnings (S/UnityGraph.Cli/IndexCommand.cs:15);
    20 sub-assets in `perf memory --object` (B/Ops/PerfOps.Memory.cs:87); `ug docs` has no
    `--budget` (S/UnityGraph.Cli/EditorCommands.cs:549-551). Each gets `--all`/`--top` or a
    `more` line.

Re-check this list against the code first (line numbers drift); add any hard cap it missed.
Report per item: the escape, its default answer size before and after (should be unchanged),
and the full answer's size on a purge-squad example.

## Part 2: O10
Run `tasks/P1-prompt.md` Part 2 as written (three legs, ask before installing MCP for Unity and
before any `claude -p` runs). Stop after its report: A/B/C/D is the user's decision.

## Report at the end
For C1 and O10: what was built or measured; test counts (dotnet, EditMode, e2e); purge-squad
numbers; decisions the spec now records; open asks. End with x/y tasks done (the "Not built
yet" list plus O10).
