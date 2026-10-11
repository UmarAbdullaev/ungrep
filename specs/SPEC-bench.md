# Spec: bench

`ug bench`: question suites per Unity project, run on four arms (a grep baseline, codegraph,
Graphify, unitygraph), scored on calls, tokens, wall time, correctness against a checked answer
key and redo rate; and the overhead-budget checks that fail a release. SPEC.md's goal (−30%
main-thread context, −15% turns, equal or better correctness) is "measured by `stats` and a
benchmark": this is the benchmark. Depends on `stats` and every feature module. Written 2026-10-11
(B1), no review stop (standing approval, 2026-09-29).

Until now the numbers were O10's chores (`specs/SPEC-editor-ops.md`, Three-way measurement) and
the purge-squad transcript replays (`ug stats`). Neither scores questions across the code-graph
arms, and nothing failed a build that broke an overhead budget.

## Runner: `ug bench`, not a script in `bench/`

- It reuses `stats` in process: an agent run is scored by `TranscriptAnalyzer` on its own
  transcript, the same numbers `ug stats` prints.
- It reuses the hook dispatcher in process for the compile-gate budget, so the check runs the
  same handler the Stop hook runs.
- One toolchain: .NET 8, no new package (suites are JSON, read with System.Text.Json; YAML would
  need a library, see Suites). No node or Python step to keep working on two OSes.
- Golden-tested like every other command; SPEC.md Commands already names `ug bench`.
- Cost: about 60 KB more in `dist/ug`. A user can bench their own project with their own suite.

`bench/` keeps the data: `bench/suites/<project>.json`, `bench/baselines/` (committed reference
results), `bench/results/` (each run's JSON). The answers a run saw (`--answers`) go to
`artifacts/bench/`, which git ignores: they hold the project's code.

## Commands

```
ug bench run <suite.json> [--mode script|agent] [--arm <a>[,<b>]] [--question <id>[,<id>]]
             [--project <dir>] [--out <file>] [--baseline <file>] [--answers <dir>]
             [--repeat <n>] [--claude <exe>] [--model <m>] [--max-usd <x>] [--dry-run]
ug bench budgets [--project <dir>] [--gate]
```

- `run` runs every question on every arm the suite defines (or the ones named), writes the
  results JSON (`--out`, default `bench/results/<suite>-<mode>-<yyyy-MM-dd>.json` beside
  `bench/baselines/`), prints a table, checks the overhead budgets, and compares with
  `--baseline` when given.
- `--mode script` (default) runs each arm's recorded call sequence: free and repeatable. `--mode
  agent` runs `claude -p` once per question, arm and repeat: it spends the user's usage.
- `--dry-run` runs nothing in either mode: script mode lists each question's legs and calls,
  agent mode the run count and every command line (the count the user is asked about).
- `budgets` runs only the overhead checks; `--gate` adds the compile-gate check, which needs the
  open editor and writes a probe script into the project (see Overhead budgets).
- Exit codes: 0 every check held; 1 a budget broken or a regression against the baseline; 2 a
  usage or suite error (unknown arm, bad regex, missing file). A question an arm could not run is
  reported, not an error.

## Arms

| Arm | Tools | Needs | Editor chores |
|---|---|---|---|
| `grep` | shell `grep`/`find`, Read | nothing | `unity-editor-mcp` |
| `codegraph` | codegraph 1.6 `codegraph_explore` (MCP), its CLI | `.codegraph/` in the project | `unity-editor-mcp` |
| `graphify` | `graphify query/explain/affected/path` over `graphify-out/graph.json` | the graph, built with `graphify update` (AST only, no LLM) | `unity-editor-mcp` |
| `unitygraph` | `ug` and the `unity` skill | `ug index` done | `ug` |

- Graphify's full pipeline adds an LLM pass over docs and comments. It costs money per build and
  is not repeatable, so the arm uses the AST graph, the part Graphify builds for free. Its graph
  holds code only; asset questions fall back to grep on that arm, as they do on codegraph.
- Editor chores: grep, codegraph and Graphify have no editor side. A session using them works the
  editor through an MCP server, so those arms share one leg: `unity-editor-mcp` (Unity's
  `unity mcp`, Pipeline), the cheapest other leg O10 measured (3,524 tokens against `ug`'s 1,939
  on chores 1-7 and 9-11). Shared steps run once and their score counts for each of the three.
- Indexing a project for codegraph or Graphify is the user's decision (their global
  instructions); a missing index makes that arm "not run: needs codegraph-index".

## Suites

One JSON file per project (comments and trailing commas allowed). JSON, not the `.yaml` SPEC.md
Commands first named (now corrected there): System.Text.Json is already in, and Unity-flavoured YAML parsing stays in
`asset-index`.

```jsonc
{
  "suite": "purge-squad",
  "project": "D:/Projects/Unity/purge-squad",   // default for --project
  "commit": "9ce9995",                           // the keys hold for this commit; another HEAD is a warning
  "needs": {                                     // named preconditions: a shell command that exits 0
    "editor": "ug status | grep -q '  idle  '",
    "scenes-saved": "! ug scene | grep -q '  modified  '",
    "codegraph-index": "test -d .codegraph"
  },
  "servers": {                                   // MCP servers `mcp` steps start over stdio
    "unity-editor-mcp": { "command": "unity", "args": ["mcp", "--project-path", "."] }
  },
  "arms": {
    "grep": {},
    "codegraph": { "needs": ["codegraph-index"], "own": "codegraph|mcp__codegraph__" },
    "unitygraph": { "own": "\\bug\\b", "agent": { "pluginDir": true } }
  },
  "agentPrompt": "Answer in at most 12 lines and give file:line for each place you name.",
  "questions": [
    {
      "id": "N1",
      "kind": "navigation",                      // navigation | asset | chore
      "prompt": "Where is the class RunDirector defined, and which Unity messages does it implement?",
      "key": {
        "items": ["RunDirector\\.cs\\D{1,12}2[34]\\b", "\\bAwake\\b", "\\bStart\\b"],
        "not": [],                               // agent mode: must not appear in the final answer
        "checked": "2026-10-11, RunDirector.cs read by hand: attribute at 23, class at 24, Awake 90, OnEnable 101, OnDisable 108, Start 117"
      },
      "steps": {
        "grep": [
          { "run": "grep -rn \"class RunDirector\" Assets --include=*.cs" },
          { "read": "Assets/Scripts/Run/RunDirector.cs", "offset": 88, "limit": 40 }
        ],
        "codegraph": [ { "mcp": "codegraph", "tool": "codegraph_explore", "args": { "query": "RunDirector" } } ],
        "unitygraph": [ { "run": "ug explain RunDirector" } ]
      }
    },
    {
      "id": "C2",
      "kind": "chore",
      "needs": ["editor"],
      "prompt": "Set Boot/AdOverlay/Bar's ProgressBar._value to 0.5 and read it back.",
      "before": [ { "run": "ug eval 'return UnityEditor.Undo.GetCurrentGroup();'", "capture": { "group": "result: (\\d+)" } } ],
      "steps": {
        "unitygraph": [ { "run": "ug set Boot/AdOverlay/Bar _value 0.5 --component ProgressBar" } ],
        "unity-editor-mcp": [ { "mcp": "unity-editor-mcp", "tool": "set_serialized_field", "args": { } } ],
        "grep": "unity-editor-mcp", "codegraph": "unity-editor-mcp", "graphify": "unity-editor-mcp"
      },
      "check": [ { "run": "ug inspect Boot/AdOverlay/Bar --component ProgressBar" } ],
      "after": [ { "run": "ug eval 'UnityEditor.Undo.RevertAllDownToGroup(${group}); return \"reverted\";'" } ],
      "key": { "check": ["_value\\s+float\\s+0\\.5\\b"], "checked": "O10 chore 2: ug inspect after the set" }
    }
  ]
}
```

- `steps` maps an arm or a named leg to its calls; a string value means "the same steps as that
  key" and the steps run once for all who share them. An arm with no entry is "not run: no steps".
- Step kinds, each one counted call:
  - `run`: a shell command in the project folder (`bash -c`; Git Bash on Windows, as hooks
    use), stdout and stderr together as the Bash tool shows them. The request is the command
    text. `timeout` in seconds (default 120, the Bash tool's).
  - `read`: a file, `offset`/`limit` optional, answered like the Read tool (`<line>\t<text>`, at
    most 2,000 lines). The request is the Read tool's input JSON.
  - `mcp`: a tool call on a suite server: the request is the tool name and the arguments' JSON,
    the answer the result's text blocks. Images count by their text only (O10's rule: the PNG
    costs the same on every arm at the same size).
- `redo: "<why>"` on a step marks a call made because an earlier answer of the same arm was wrong
  or cut short (a `more` line followed, a fallback after a miss). It still counts as a call.
- Step controls, on any step: `until` (a regex) repeats the call until its answer matches, each
  poll a counted call, `every` seconds apart (default 1), at most `max` (default 90): the status
  polls an MCP server needs where `ug` blocks. `count: false` runs a step without counting it (a
  file edit the chore itself makes). `capture: { "name": "regex" }` keeps group 1 of the answer as
  `${name}` for the later steps of the same leg.
- Around each leg, none counted: `before` steps (capture the undo group), the leg, `check` steps,
  then `after` steps (revert to the captured group, put a file back). Chores undo by
  `Undo.RevertAllDownToGroup`, never by reopening or saving a scene: the user's unsaved changes in
  the open scene survive the run.
- A key has `items`, matched against what the leg's counted calls answered (agent mode: the final
  answer), `check` items, matched against the check steps' output, and `not` items, which must be
  absent from an agent's final answer. At least one item or check item; check items need check
  steps.
- Key items are .NET regular expressions (`(?i)` for case). Items name what the answer must say
  in a tool-neutral form: file names
  with line numbers (`Enemy\.cs\D{1,12}66\b` matches `Enemy.cs:66`, `Enemy.cs L66` and `Enemy.cs,
  line 66`), type and member names, asset file names, object names.
- `checked` records how the key was verified by hand. A key without it is refused.
- The suite runs from its own folder's view of PATH plus the `bin/` beside the running `ug`, so
  `ug` in a step is this build.

## Script mode (default)

O10's method, kept: per question and arm, the calls an informed agent would make with that arm's
tools, run for real, every call counted.

Rules the steps follow, so no arm is written to lose:

1. Answer-blind. A step uses only names in the question or in an earlier answer of the same arm.
   The key is never a source for a step.
2. Each arm at its best. The arm's own command when it has one; grep and Read narrowly (`-n`,
   `--include`, a Read with `offset`/`limit` around a hit, not whole files). A cheap grep baseline
   makes every saving claimed against it conservative.
3. Fallbacks count. When an arm's tool cannot answer (codegraph and Graphify on asset questions,
   `ug` on a cut answer), the next steps are the fallback an agent would take, marked `redo` when
   they repeat work.
4. Failures count: a call that errors or times out is a call, with its bytes.
5. The answers must hold the evidence: correctness is the share of key items found in what the
   arm's calls returned. Whether an item was found for the right reason (a caller line, not a
   comment) is checked by hand once per suite change and written into the Measured section.

Scores per question and arm: calls; tokens as request plus answer bytes / 4; wall seconds;
items found of all; redo calls. Totals per arm over the questions every arm ran; an arm that ran
no question (its index missing) is left out of the totals and named.

## Agent mode (`claude -p`, the user's yes first)

Script mode measures the calls an informed agent would make; agent mode measures what Claude does.
Each run: `claude -p "<prompt> <agentPrompt>" --output-format json --session-id <uuid>` in the
project folder, then the run's transcript is read from Claude Code's project folder.

- Isolation: `--setting-sources project,local` (the user's own plugins and hooks stay out: the
  caveman hook alone changes answer length), `--strict-mcp-config` with only the arm's MCP
  servers, `--plugin-dir <repo>` on the unitygraph arm only, and `--disallowedTools` naming the
  other arms' CLIs (`Bash(ug:*)`, `Bash(codegraph:*)`, `Bash(graphify:*)`, `Skill(graphify)`).
  Deny rules are not a fence (spike S3: a denied search came back through PowerShell); a run that
  used another arm's tool is marked `leaked`.
- `--model` (default: the CLI's own), `--max-usd` passed through as `--max-budget-usd` per run.
- Scores, from `TranscriptAnalyzer` on the run's transcript: main-thread turns and context tokens,
  tool calls, what subagents read in their own transcripts, and dollars (`total_cost_usd`).
  Correct: every key item in the final answer and no `not` item. Redo: the run made a text search
  (Grep, Glob, `grep`/`rg`/`find`/`Select-String` in a shell) after the arm's first own call
  (`own`), the retry pattern spike S3 measured. Redo rate = such runs / runs, per arm (none for
  `grep`, whose own call is the search).
- Run count = questions × arms × `--repeat`; `--dry-run` prints it with each command line. The
  estimate in dollars is given to the user before any run (their rule).
- Chores run their `before`, `check` and `after` steps around the `claude -p` run, as in script
  mode, and are scored by their check items.
- The final answer is scored, then written to `--answers` (`<question>-<arm>-<repeat>.txt`, with
  the session id), not to the results JSON: it can quote project code.

## Overhead budgets

Checked on every `run` and by `ug bench budgets`; a broken one exits 1.

| Check | Budget | How |
|---|---|---|
| skill description | ≤ 300 tokens | the line Claude Code lists every session, `- unitygraph:unity: <description>`, bytes / 4 |
| skill body | ≤ 600 tokens | `skills/unity/SKILL.md` after the front matter, bytes / 4 |
| compile gate | 0 bytes when clean; ≤ 20 error lines | live, `--gate` or a suite `gate`: PostToolUse marks an untouched script edited, Stop must print nothing; then a probe script with 30 errors is written, PostToolUse and Stop must block with at most 20 error lines (the head and the `+N more` line are not errors); the probe and its `.meta` are deleted and the check must be clean again, retried every 5 s for a minute while the editor reloads |

- The gate check writes into the project (SPEC.md Boundaries: ask first), so it runs only with
  `--gate` or a suite that names a probe path (`"gate": "Assets/UnityGraphBenchProbe.cs"`), and
  only when the editor answers. Otherwise it is "not checked", which does not fail the run; a
  release run must show all three checked.
- Index builds cost 0 tokens by construction (no LLM); nothing to check.

## Baseline

`--baseline <results.json>` compares per question and arm. Fails (exit 1), unitygraph arm only:

- a question correct in the baseline and not now;
- more calls than the baseline;
- more than 10% and more than 50 tokens over the baseline.

Other arms' changes are printed, never fail (their tools change outside this repo). Questions in
only one file are listed. `bench/baselines/bench-<suite>-<mode>.json` is the committed reference;
it is replaced only with a commit that says why.

## Results

```
bench purge-squad  script  commit 9ce9995 (HEAD 9ce9995)
question       grep                  codegraph  graphify  unitygraph
N1 navigation  2 / 110 / 0.5 ok      -          -         1 / 178 / 0.5 ok
...
A5 asset       2 / 140 / 1.3 ok      -          -         3 / 8,139 / 1.2 ok redo 2
C1 chore       2 / 411 / 1.5 ok *    -          -         2 / 79 / 0.4 ok
...
- codegraph N1,N2,N3,N4,N5,A1,A2,A3,A4,A5: no steps
- unitygraph C8: needs scenes-saved
* shared steps, run once: unity-editor-mcp
totals over 19 questions every arm ran (codegraph, graphify ran none)
            calls  tokens  seconds  correct  redo
grep           71   7,393    111.0    19/19     0
unitygraph     39  12,847     65.3    19/19     2
unitygraph vs grep: calls -45%, tokens +74%
idle tokens (always + on use)  grep 1,510 + 2,698  codegraph 0 + 0  graphify 92 + 10,831  unitygraph 27 + 599
budgets  skill description 27/300 ok  skill body 599/600 ok  compile gate 0 bytes clean, 20/20 error lines ok
baseline  bench/baselines/bench-purge-squad-script.json: no regression
results  bench/results/purge-squad-script-2026-10-11.json
```

The idle row is what an arm costs in a session before and when it is used: always, the lines
Claude Code lists every turn (skill listings, MCP tool names of a server the arm used); on use,
the skill body or the used tools' schemas.

A cell is calls / tokens / seconds and `ok`, `3/5` items found, or `-` with the reason below the
table. The JSON holds the same per step (kind, request and answer bytes, seconds, exit), the
suite's commit and the project's HEAD, and in agent mode the session id, turns, context, subagent
context, dollars and `leaked`. Answers go to `--answers` (default
`artifacts/bench/<suite>-<mode>-<date>/`), one file per leg or run. No timestamps but the run date.

## purge-squad suite

`bench/suites/purge-squad.json`, keys for commit 9ce9995 (branch `rebuild`). Each key was checked
by hand against the code and assets; the `checked` field says how.

| id | kind | question | key |
|---|---|---|---|
| N1 | navigation | where is `RunDirector` defined; its Unity messages | `RunDirector.cs:24` (23 with its attribute); Awake, OnEnable, OnDisable, Start |
| N2 | navigation | what calls `Wallet.TrySpend` (both overloads) | `Fabrication.TryFabricate` (Fabrication.cs:32); 6 tests in WalletTests.cs |
| N3 | navigation | which methods subscribe to `Health.Died` | `Enemy.Awake` (Enemy.cs:66), `PlayerCharacter.OnEnable` (:92), `RunDirector.OnEnable` (:105) |
| N4 | navigation | which concrete classes implement `IPartRule`, directly or through interfaces | `StraightPayload` (WeaponRules.cs:10), `SimpleEmitter` (:71), `OnKillVolleyTrigger` (:125) |
| N5 | navigation | what breaks if the serialized field `ProgressBar._value` is renamed | `ProgressBar.prefab` (the field's data), `RevivePanel.prefab` (override on `Countdown`), the code uses at ProgressBar.cs 18, 21, 40; `HealthBar._value` is another field |
| A1 | asset | which prefabs and scenes use the `ProgressBar` script (O10 chore 11) | 4 prefabs, 2 scenes |
| A2 | asset | which scenes and prefabs use `ButtonPrimary.prefab` | ResultsPanel, LabPanel, FabricatePanel, LocatePanel prefabs; GalaxyMap scene |
| A3 | asset | which prefabs use `Gun_Metal.mat`, on which objects | 7 part prefabs; Body, Tip, ProngL, ProngR, Tube |
| A4 | asset | what Arena_Greybox wires into RunDirector's `_nodes`, `_rooms`, `_spawner`, `_player`, `_fade` | NodeCatalog.asset, SolReach_Rooms.asset, Waves, Player, RoomFade |
| A5 | asset | which data assets spawn `Enemy_Shooter.prefab` | 11 node assets; not C2_Central |
| C1-C10 | chore | O10 chores 1-10 | O10's checks |

The chores are O10's, on `Boot` with nothing saved, every edit undone by `after` steps; chore 11
is A1. C8 (the EditMode tests) also needs `scenes-saved`: with a modified scene open `ug test`
refuses ("unsaved changes ... ug save or ug undo first") and Pipeline's `run_tests` hangs on the
runner's save prompt, so C8 is "not run" until the user saves or undoes.

## Code

- `src/UnityGraph.Bench`: `Suite` (model, loader, validation), `StepExecutor` (run, read, mcp;
  polls, captures), `BenchHost` (shell, files, processes, MCP servers), `McpClient` (stdio
  JSON-RPC: initialize, tools/list, tools/call), `ScriptRunner`, `AgentRunner`, `Scoring` (key
  items, redo, leaks), `BenchResults` (JSON), `Baseline`, `BenchText`, `Budgets`, `SkillFile`.
- `src/UnityGraph.Cli/BenchCommand.cs`: arguments, paths, dry runs, exit codes.
- Shell, files, processes and MCP servers behind `IBenchHost`, so the runner is tested without
  shells, editors or `claude`.

## Testing

- `tests/UnityGraph.Bench.Tests`, on a fake `IBenchHost`:
  - Suite loading: aliases, unknown arms and step kinds, bad regexes, missing `checked`; the
    committed purge-squad suite loads with a unitygraph leg on every question.
  - Scoring: items found, `not` items, tool uses, redo, leaks, text searches.
  - Script runner: before, leg, check, after in order; a shared leg once; `needs` unmet; polls
    stop at `max`; a server that does not start; captures filled; answers dumped; reads numbered
    like the Read tool; JSON round trip; the report (golden).
  - `McpClient` against an in-process server over pipes: handshake, tools, a call, an error.
  - Agent runner: command lines per arm, the plan, a fixture transcript scored like `ug stats`,
    a missing JSON answer, a chore's before and check steps around `claude`.
  - Baseline compare and its report (golden).
  - Budgets: skill files over and under; the gate through the dispatcher with a fake check, the
    after-check retried while the editor reloads.
- `tests/UnityGraph.Cli.Tests/BenchCommandTests`: dry runs in both modes run nothing, a run writes
  results and a baseline regression exits 1, usage errors exit 2.
- End to end on purge-squad is the suite itself (Measured).

## Boundaries

- Always: every call counts; keys checked by hand before a number is reported; results name the
  commit the keys hold for.
- Ask first: `claude -p` runs (count and dollars first); indexing a project for codegraph or
  Graphify; downloading Boss Room, the URP 3D Sample or Chop Chop (SPEC.md Decisions #10, licence
  checked first); installing anything; the compile-gate probe on a project other than purge-squad.
- Never: project code in a committed results file (`--answers` goes to `artifacts/`); a step
  written from the key.

## Measured

### Script mode, grep and unitygraph, 2026-10-11

purge-squad 9ce9995, Unity 6000.3.14f1, bridge 0.15.0, `ug` at this commit, Pipeline (`unity
mcp`) for the grep leg's chores. `Boot` open with the user's unsaved changes, left as found (still
`modified`, edit mode, probe gone, `git status` unchanged). codegraph and Graphify not run: the
project is not indexed for them (the user's decision, asked). C8 not run (`scenes-saved`).
`bench/baselines/bench-purge-squad-script.json` is this run.

| Question | grep calls / tokens | unitygraph calls / tokens | Note |
|---|---|---|---|
| N1 | 2 / 110 | 1 / 178 | |
| N2 | 2 / 721 | 1 / 367 | |
| N3 | 2 / 962 | 1 / 145 | |
| N4 | 2 / 221 | 4 / 688 | one `ug refs` per interface: no transitive implementers in one call |
| N5 | 4 / 279 | 2 / 190 | |
| A1 | 2 / 98 | 1 / 413 | `ug impact` names objects too; `grep -l` lists files only |
| A2 | 2 / 97 | 1 / 643 | as A1 |
| A3 | 4 / 543 | 1 / 274 | |
| A4 | 5 / 468 | 5 / 624 | scene field values are not in `ug` offline: YAML fallback |
| A5 | 2 / 140 | 3 / 8,139, redo 2 | `ug refs` on a prefab: a row per field, cut twice, whole at `--budget 4800` |
| C1-C7, C9, C10 | 44 / 3,757 (Pipeline) | 19 / 1,189 | C5: 10 polls, 36.8 s against one `ug compile`, 13.4 s |

| Arm | Calls | Tokens | Seconds | Correct | Redo | Idle (always + on use) |
|---|---|---|---|---|---|---|
| grep | 71 | 7,393 | 111.0 | 19/19 | 0 | 1,510 + 2,698 (Pipeline's 151 tool names; schemas of the 16 used) |
| unitygraph | 39 | 12,847 | 65.3 | 19/19 | 2 | 27 + 599 (skill line, skill body) |

- unitygraph against grep: calls −45%, tokens +74%. Without A5, unitygraph 36 calls, 4,708 tokens
  against grep's 69 and 7,253 (−48% calls, −35% tokens).
- By kind: navigation 9 / 1,568 against 12 / 2,293; asset 11 / 10,093 (1,954 without A5) against
  15 / 1,346; chores 19 / 1,189 against Pipeline's 44 / 3,757 (O10's chore result held).
- Graphify's skill would add 92 tokens a turn and 10,831 on use (its idle cost was read; the arm
  did not run).
- Budgets: skill description 27/300, body 599/600, compile gate 0 bytes clean and 20/20 error
  lines on the 30-error probe: all held, the gate checked live.
- Script mode flatters grep on navigation and assets: its steps are written by someone who knows
  where to look, one narrow grep and a Read with offset. Agent mode is the test of that; grep's
  numbers here are its floor.
- Checked by hand: every answer dump in `artifacts/bench/purge-squad-script-2026-10-11/`, each
  key item found for the right reason. Keys fixed while checking: N1 the class line is 24 (23 is
  its attribute; `ug explain` gives 23); N5 the third item is the code that uses the field, not a
  `Value` property; C1 Pipeline answers JSON; C5 no errors is the right answer; C9 marker names.
  Steps fixed: A3 and A4's grep legs read `m_Name` with `awk` (a GameObject's component list
  outruns `grep -A10`); A5's `ug` leg needs a third call; C4's Pipeline `eval` needs 30 s on a
  cold compile.
- `ug` gaps found, filed as follow-ups (B1 in tasks/todo.md): `ug refs <prefab>` grouped per
  asset; scene and prefab field values offline; implementers through derived interfaces in one
  call; `ug find <guid>` (found nothing); `ug explain`'s line at the attribute, not the class.
