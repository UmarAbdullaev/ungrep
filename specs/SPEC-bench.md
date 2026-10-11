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
- How each arm asks: codegraph's `codegraph_explore` takes the symbol names from the question
  (its MCP instructions: names, not prose; the verbatim question matched unrelated symbols);
  `graphify query` takes the question as asked (its skill: the question goes to the graph first).
  When the graph has no answer (no event subscriptions, no callers of a member, no field uses),
  the arm's next steps are grep's, marked `redo`.

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
  line 66`; `Enemy\.cs[^\n]{0,200}(:|\bline |\bL)66\b` the shorthand agents write, `Enemy.cs:188
  ... subscribes at :66`), type and member names, asset file names, object names. A question
  that reads two ways takes either answer (N3: the subscribing methods or the handlers). A `not`
  item matches the wrong claim, not the bare name: an answer that names it as excluded passes
  (A5: `C2_Central` beside a never, not, without or n't on its line).
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

MCP servers start on a leg's first call and stop after its `after` steps, so each leg starts them
fresh: a real session answers one question, and a server may keep state between calls
(codegraph's explore leaves out source it already sent in the session; one server for all legs
cost N3 its third subscriber).

Scores per question and arm: calls; tokens as request plus answer bytes / 4; wall seconds;
items found of all; redo calls. Totals per arm over the questions every arm ran; an arm that ran
no question (its index missing) is left out of the totals and named.

## Agent mode (`claude -p`, the user's yes first)

Script mode measures the calls an informed agent would make; agent mode measures what Claude does.
Each run: `claude -p "<prompt> <agentPrompt>" --output-format json --session-id <uuid>` in the
project folder, then the run's transcript is read from Claude Code's project folder.

- Isolation: `--setting-sources project,local` (the user's own plugins and hooks stay out: the
  caveman hook alone changes answer length), `--strict-mcp-config` with only the arm's MCP
  servers, `--plugin-dir <repo>` on the unitygraph arm only, a skill an arm lists outside that
  plugin (Graphify's, a personal skill in `~/.claude/skills`, which the `user` source would load)
  copied with its folder into a temp plugin named after the arm and passed with a second
  `--plugin-dir` (written for the run, removed after; `--dry-run` writes nothing), so the
  Graphify arm is listed `graphify:graphify`, and `--disallowedTools` naming the
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
| N3 | navigation | which methods subscribe to `Health.Died` | the `+=` at Enemy.cs:66 (`Enemy.Awake`), PlayerCharacter.cs:92 (`OnEnable`), RunDirector.cs:105 (`OnEnable`); the methods named as those or as the `OnDied` handlers |
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
  polls, captures; servers fresh per leg), `BenchHost` (shell, files, processes, MCP servers), `McpClient` (stdio
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
    stop at `max`; a server that does not start, and is not tried again; servers fresh per leg;
    captures filled; answers dumped; reads numbered like the Read tool; JSON round trip; the
    report (golden).
  - `McpClient` against an in-process server over pipes: handshake, tools, a call, an error.
  - Agent runner: command lines per arm, the plan, a fixture transcript scored like `ug stats`,
    a missing JSON answer, a chore's before and check steps around `claude`, a skill outside the
    plugin copied into the arm's plugin for the run only.
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

### Script mode, four arms, 2026-10-11

purge-squad 9ce9995, Unity 6000.3.14f1, bridge 0.15.0, `ug` at this commit, codegraph 1.6.0
(`codegraph serve --mcp`), Graphify's AST graph (`graphify update`, no LLM pass), Pipeline (`unity
mcp`) for the other arms' chores. Both indexes built on the user's yes and kept out of
purge-squad's git (`.git/info/exclude`). `Boot` open, left as found. C8 not run
(`scenes-saved`). `bench/baselines/bench-purge-squad-script.json` is this run (it replaced the
grep-and-unitygraph run of the same day: four arms, servers fresh per leg).

| Question | grep | codegraph | Graphify | unitygraph | Note |
|---|---|---|---|---|---|
| N1 | 2 / 110 | 1 / 6,266 | 1 / 1,665 | 1 / 178 | |
| N2 | 2 / 721 | 2 / 11,783, redo 1 | 3 / 1,204, redo 2 | 1 / 367 | Graphify has no member-call edges across files: grep fallback |
| N3 | 2 / 962 | 1 / 6,284 | 3 / 2,604, redo 2 | 1 / 145 | Graphify has no event nodes, only the `OnDied` methods |
| N4 | 2 / 221 | 2 / 10,630, redo 1 | 2 / 2,317, redo 1 | 4 / 688 | `graphify affected` finds implementers through derived interfaces; `ug` needs one `refs` per interface |
| N5 | 4 / 279 | 4 / 6,471 | 5 / 3,581, redo 1 | 2 / 190 | both graphs code-only: prefab data by grep |
| A1 | 2 / 98 | grep's | grep's | 1 / 413 | `ug impact` names objects too; `grep -l` lists files only |
| A2 | 2 / 97 | grep's | grep's | 1 / 643 | as A1 |
| A3 | 4 / 543 | grep's | grep's | 1 / 274 | |
| A4 | 5 / 468 | grep's | grep's | 5 / 624 | scene field values are not in `ug` offline: YAML fallback |
| A5 | 2 / 140 | grep's | grep's | 3 / 8,139, redo 2 | `ug refs` on a prefab: a row per field, cut twice, whole at `--budget 4800` |
| C1-C7, C9, C10 | 44 / 3,757 (Pipeline) | Pipeline's | Pipeline's | 19 / 1,188 | C5: 10 polls, 43.3 s against one `ug compile`, 14.7 s |

Calls / tokens per cell; codegraph and Graphify have no asset or editor side, so their asset
questions are grep's steps and their chores Pipeline's (Arms).

| Arm | Calls | Tokens | Seconds | Correct | Redo | Idle (always + on use) |
|---|---|---|---|---|---|---|
| grep | 71 | 7,394 | 134.1 | 19/19 | 0 | 1,510 + 2,698 (Pipeline's 151 tool names; schemas of the 16 used) |
| codegraph | 69 | 46,537 | 134.5 | 19/19 | 2 | 1,518 + 3,133 (Pipeline's, plus `codegraph_explore`: 8 + 435) |
| Graphify | 73 | 16,472 | 141.9 | 19/19 | 6 | 1,602 + 13,528 (Pipeline's, plus the skill: 92 + 10,830) |
| unitygraph | 39 | 12,846 | 72.9 | 19/19 | 2 | 27 + 599 (skill line, skill body) |

- Against grep: codegraph calls −3%, tokens +529%; Graphify calls +3%, tokens +123%; unitygraph
  calls −45%, tokens +74%. Without A5, unitygraph 36 calls, 4,707 tokens against grep's 69 and
  7,254 (−48% calls, −35% tokens).
- Tokens here are what the calls answered. In a session each call also re-reads the context
  (the agent pilot: about 52k a turn before any answer), so a call saved is worth far more than
  its answer's bytes; the calls column is the closer measure of cost.
- Navigation (N1-N5), where all four use their own tool: grep 12 / 2,293; codegraph 10 / 41,434;
  Graphify 14 / 11,371; unitygraph 9 / 1,568. codegraph's explore answers with the verbatim source
  of every related symbol and the call paths, about 6,300 tokens a call however small the
  question; Graphify's `query` is capped at 2,000 tokens of NODE and EDGE lines and falls back to
  grep on N2, N3 and N5.
- Asset: unitygraph 11 / 10,093 (1,954 without A5) against grep's 15 / 1,346. Chores: unitygraph
  19 / 1,188 against Pipeline's 44 / 3,757 (O10's chore result held).
- Budgets: skill description 27/300, body 599/600, compile gate 0 bytes clean and 20/20 error
  lines on the 30-error probe: all held, the gate checked live.
- Script mode flatters grep on navigation and assets: its steps are written by someone who knows
  where to look, one narrow grep and a Read with offset. Agent mode is the test of that; grep's
  numbers here are its floor.
- Checked by hand: every answer dump in `artifacts/bench/purge-squad-script-2026-10-11/`, each
  key item found for the right reason. Keys fixed while checking: N1 the class line is 24 (23 is
  its attribute; `ug explain` gives 23); N5 the third item is the code that uses the field, not a
  `Value` property; C1 Pipeline answers JSON; C5 no errors is the right answer; C9 marker names.
  For codegraph and Graphify: a line item also matches the line in numbered source (codegraph's
  and the Read tool's `66\t... Died +=`); N4's lines take the attribute's or the class's (Graphify
  gives L9, L70, L124, the attribute lines); N5's third item lost a bare `Value`, which matched an
  unrelated `Json.Value` in codegraph's answer. Unity-message items checked against the method's
  own source or NODE line (N3: `Enemy.cs:64 Awake`, not `Health.Awake`).
  Steps fixed: A3 and A4's grep legs read `m_Name` with `awk` (a GameObject's component list
  outruns `grep -A10`); A5's `ug` leg needs a third call; C4's Pipeline `eval` needs 30 s on a
  cold compile.
- `ug` gaps found, filed as follow-ups (B1 in tasks/todo.md): `ug refs <prefab>` grouped per
  asset; scene and prefab field values offline; implementers through derived interfaces in one
  call; `ug find <guid>` (found nothing); `ug explain`'s line at the attribute, not the class.

### Script mode after the B1 fixes, unitygraph, 2026-10-11

purge-squad 9ce9995, `ug` with B1a-B1e, navigation and asset questions only (the chores need the
editor and the fixes touch none of them: their 19 / 1,188 stands). `--baseline` the four-arm run
above: no regression. The baseline file is kept: it is the four-arm record, and its unitygraph
numbers are the floor these beat.

| Question | Before | After | Change |
|---|---|---|---|
| N4 | 4 / 688 | 1 / 201 | `ug refs IPartRule` lists the implementers through `IEmitter`, `IPayload`, `ITrigger` (B1c) |
| A4 | 5 / 624 | 2 / 352 | `ug explain 'Arena_Greybox.unity > Run > RunDirector'` gives the wired fields (B1b) |
| A5 | 3 / 8,139, redo 2 | 1 / 249 | 139 field rows folded into 11 lines, one per node asset (B1a) |
| N1-N3, N5, A1-A3 | 8 / 2,210 | 8 / 2,206 | N2 four tokens shorter: line numbers moved (B1e) |

- unitygraph navigation 6 / 1,077 against grep's 12 / 2,293; asset 6 / 1,931 against grep's
  15 / 1,346. With the chores, 31 calls / 4,195 tokens against grep's 71 / 7,394: calls −56%,
  tokens −43% (before: −45%, +74%).
- `ug explain RunDirector` gives 24, the class line; the key took 23 or 24 and still does.
- Idle went from 27 + 599 to 80 + 598: the skill description names the questions to load it for
  (B1f), 53 tokens on every session. Whether Claude now picks `ug` is an agent-mode question, not
  run here (paid runs, the user's yes first).
- The first `ug` call after the upgrade took 61 s: older index rules reindex every file once
  (SPEC-graph-core).

### Agent mode pilot, N3 on four arms, 2026-10-11

On the user's yes (4 runs, about $1-2 quoted): `claude-opus-5-5`, `--max-usd 0.5`, the desktop
build's `claude` 2.1.295. 4 runs, $0.68 in all, no subagents.

| Arm | Calls | Main context | $ | Its own tool used |
|---|---|---|---|---|
| grep | 2 | 180,117 | 0.34 (the first run: 36k of cache written) | (Grep is its tool) |
| codegraph | 2 | 182,304 | 0.12 | no: two Grep calls |
| Graphify | 2 | 180,305 | 0.12 | no: two Grep calls |
| unitygraph | 3 | 171,090 | 0.10 | no: three Grep calls |

- Every arm was offered its tool (`codegraph_explore` deferred, `graphify:graphify` and
  `unitygraph:unity` listed) and every run answered with the built-in Grep. On this question the
  tools were not chosen at all, so agent mode measured Grep four times.
- About 52k of context a turn before any answer (system prompt, built-in tools, the desktop
  build's own skills and deferred tools), the same on every arm.
- Isolation also drops what each tool's install adds outside its plugin or server: codegraph's
  CLAUDE.md block ("reach for it before grep"), Graphify's `graphify claude install` section and
  its PreToolUse hook. unitygraph ships no CLAUDE.md (SPEC.md Decisions #7); its skill and hooks
  came through `--plugin-dir`, and its PreToolUse hook does not match Grep.
- All four answers were right; the key was not. It asked for Awake and OnEnable, and every answer
  named the `OnDied` handlers, three of them with `:92` after another line of the same file. The
  key now takes either reading and the same-line form (Suites); all four pass it, checked by hand.
  Scored before the fix, the run's results file says 0/1 on each arm.
- The full set (76 runs, about $14 a variant) is not run: the user stopped at the pilot.

### Agent mode, B1f skill description, 2026-10-11

On the user's yes (9 runs, about $1-1.50 quoted): N3, N4 and A5 on the unitygraph arm, 3 repeats,
`claude-opus-5-5`, `--max-usd 0.5`, `claude` 2.1.295, the skill description of the B1 fixes.
9 runs, $1.55 in all (`total_cost_usd`; the first run of each question about $0.30, cache
written, the rest $0.09-0.11), no subagents.

| Question | Picked `ug` | Calls | Main context | Right (by hand) |
|---|---|---|---|---|
| N3 | 1 of 3 | 2.3 | 191,072 | 3/3, one redo |
| N4 | 0 of 3 | 2.3 | 190,129 | 3/3 |
| A5 | 2 of 3 | 2.3 | 191,495 | 3/3 |

- The skill was loaded and `ug` run in 3 of 9 runs, against 0 of 1 in the pilot; the other six
  answered with Grep. N4 ("which classes implement IPartRule") never picked it, though the
  description names "implements X".
- With `ug` or without, 2-3 calls and the same context a run: on these questions grep is two
  narrow calls too, so the saving is in the answer, not the run (A5 by `ug`: 11 files with their
  counts and line ranges, no YAML read).
- N3's redo: `ug refs Health.Died` lists the `+=`, the `-=` and the raise alike as `called from`
  and names no handler, so the run grepped `Died [+-]=` for `OnDied` (B1g).
- A5 scored 0/3 in the results file: every answer said "C2_Central never uses it" and the `not`
  item matched the bare name. The item now takes C2_Central named as excluded (Suites); all three
  pass it, checked by hand. The results file keeps the score of the run.
