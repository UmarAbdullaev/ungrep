# Spec: unity-loop

Part of unitygraph (see `../SPEC.md`, module table). Status: **built** (2026-10-01). Builds on
`editor-link` (`SPEC-editor-link.md`: bridge, `BridgeClient`, `ug compile`) and closes what the
Unity MCP server's `read_console`, `run_tests` and `get_test_job` do, in answers sized for Claude.

## Objective

The edit, compile, run, read-the-console loop costs about 4 calls per cycle and a lot of console
text. `unity-loop` makes it: one silent check after the edits (compile gate), one call for the
console (`ug console`), one call for a test run (`ug test`), one call for what a play session
did (`ug log`). Every answer is short, errors come first, repeats are folded, and a clean result
costs a line or nothing.

## Scope

In: `ug console`, `ug test`, `ug log`, `ug play --for <s>`; the compile gate (PostToolUse edit
tracking and a Stop hook); one new bridge endpoint (`/playlog`, bridge 0.3.0).

Out: reading `Editor.log` for the console or for tests (only `ug compile` falls back on it; with
no bridge these commands say `Unity not reachable (<reason>)`); clearing the console; test runs in
a player build; editing anything from a hook (hooks never touch scenes, prefabs or project files).

## Commands

All take `--project <dir>`; plain text; exit 0 for any answer, 1 only for no Unity project or an
invalid config. They need the bridge: `Unity not reachable (<reason>); ug <cmd> needs the bridge`
plus the install hint `ug status` gives. The config switches `editor-link` and `unity-loop` turn
them off (`unity-loop is off in .claude/unitygraph.json`).

### `ug console [--level error|warning|log] [--count N] [--after ID] [--budget T]`

The editor console, from the bridge's ring buffer (2,000 entries since the editor started).

- `--level` is the lowest level shown: `error` (errors only, asserts and exceptions count as
  errors), `warning` (default: errors and warnings), `log` (everything).
- `--count` is how many of the newest entries are looked at, 1 to 500, default 200. `--after` only
  looks at entries newer than a console id (the id `ug console` printed last time), so a second
  call shows only what is new.
- Entries with the same level, text and frame are folded into one line with `xN` (the bridge
  already folds consecutive repeats; this folds the rest). Order: errors, then warnings, then
  logs; within a level the most recently seen first. Text is the first line, cut at 160
  characters; the frame is the first stack frame in the project's own code (`Assets/...:line`).
- The answer fits the token budget (`--budget`, default `answerBudget` = 1,200; a token is
  4 bytes, as in `query`). A cut answer ends with `  more  ug console ... --budget <2 x>`.

```
$ ug console
console  2 errors, 1 warning  (last #4120)
  error x12  NullReferenceException: Object reference not set to an instance of an object  Assets/Scripts/Enemy.cs:42
  error  Missing key 'Hit' in Animator  Assets/Scripts/Enemy.cs:51
  warning  The referenced script on this Behaviour is missing!

$ ug console
console  nothing at warning level or worse  (last #4120)
```

### `ug test [--mode EditMode|PlayMode] [--filter <regex>] [--timeout S]`

Runs the project's tests through the bridge (`/tests`, `TestRunnerApi`) and waits (default
`--mode EditMode`, `--timeout` 300 s). `--filter` is a regex over the full test name.

1. A compile check first (`ug compile`'s flow): when the scripts do not compile the run does not
   start, the answer is the compile errors (same lines as `ug compile`).
2. Start the run, poll `/tests/result` every 250 ms (also not run while the editor is in play mode). A PlayMode run reloads the domain; the
   client rides it out the way the compile wait does (refused connections while the editor
   lives, new port from `bridge.json`).
3. Answer. Green: one line. Red: the summary line, then one line per failure, at most 20
   (`(+N more failures)` after): `file:line  Full.Test.Name  first line of the message` (message cut
   at 160 characters; `(no file)` when the failure has no frame in the project's code).
   Nothing is printed for passing tests.

```
$ ug test
EditMode: 156 passed, 4 skipped in 8.2 s

$ ug test --filter Turret
EditMode: 1 failed, 11 passed in 1.4 s
  Assets/Tests/Editor/TurretTests.cs:34  Game.Tests.TurretTests.Fires_at_range  Expected: 3  But was: 2
```

Other answers: `EditMode: no tests ran` (nothing matched); `not run: the scripts do not compile (1 error)`
and the lines; `Unity was still running tests after 300 s` (the run goes on in the editor; a new
`ug test` is refused with `a test run is in progress`); `com.unity.test-framework is not in this
project`. `inconclusive` is counted when there are some. From bridge 0.11.0 (Modified scenes and
stopped runs, below): `not run: unsaved changes in Assets/Scenes/Main.unity (the test runner would
ask to save them): ug save or ug undo first`; `EditMode: canceled in Unity before it finished`;
`EditMode: Unity stopped the run: <its message> (ug console shows the error)`.

#### Modified scenes and stopped runs (F2, bridge 0.11.0)

Measured 2026-10-11 (Unity 6000.3.14f1, com.unity.test-framework 1.6.0): before every run
`TestRunnerApi.Execute` runs `SaveModifiedSceneTask`, which calls
`EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()`. In batch mode Editor.log shows
`DisplayDialog: Scene(s) Have Been Modified ... This should not be called in batch mode.`, the run
ends without `RunFinished`, and `ug test` waited out its 300 s. In a GUI editor it is a modal Save /
Don't Save / Cancel dialog in front of the user: the run blocks until someone answers, Save writes
the scene (against SPEC-editor-ops safety rule 2, no saving unasked), and Cancel ends the run with
`TestRunCanceledException` and no `RunFinished`. The bridge's running id then stayed set, so every
later `ug test` answered `a test run is in progress` until the editor restarted. The Test Runner
window's Cancel button ends a run the same way. Decision:

- **Refuse, never save.** `/tests` refuses (409) while any loaded scene is modified, untitled ones
  included, and starts nothing: `unsaved changes in Assets/Scenes/Main.unity (the test runner would
  ask to save them): ug save or ug undo first`. Scenes are listed by path, `untitled scene` for
  one with no path (`ug save` then says what to do with it). `ug test` prints it after `not run: `
  like every refusal. `a test run is in progress` is checked first: a run's own scenes are often
  modified while it runs. Saving for the user, as MCP for Unity's `run_tests` does (SPEC-editor-ops,
  O10 results), was rejected by rule 2; discarding the changes by rule 4.
- **A run that ends without a result ends the wait.** The bridge answers `/tests/result` with
  `{"state": "stopped", "reason": "..."}` when the test runner reports an error for its run
  (`IErrorCallbacks.OnError`, e.g. a failing `IPrebuildSetup`: `Unity stopped the run: <its message>
  (ug console shows the error)`), or when its run is gone from the test runner without
  `RunFinished` (`canceled in Unity before it finished`). Gone is read from the test framework's
  internal `TestRunnerApi.IsRunning(guid)` by reflection, on every `/tests/result` and before a new
  run starts. A run is registered inside `Execute` and registered again by an
  `[InitializeOnLoadMethod]` after each domain reload, before any editor update where the bridge
  answers, so a live run never reads as gone. The stopped run is the last run (`ug test --failures`
  and `--failure` answer `last run: <reason>`), and a new run can start at once.
- `ug test` prints `<mode>: <reason>`, exit 0 as for every answer.
- **Older test frameworks.** `IsRunning(guid)` is in 1.6.0, not in 1.4.6 or 1.1.33. Without it a
  canceled run is not noticed: `ug test` waits out `--timeout` and the bridge says `a test run is in
  progress` until the editor restarts, as before. The refusal works on every version (1.1.33 has
  the same save step, `SaveModiedSceneTask`), so a run `ug` starts no longer meets the dialog.
- Bridge 0.11.0 (package and `Bridge.Version`); `ug` gates nothing on it. An older bridge neither
  refuses nor stops; an older `ug` prints a stopped run as `no tests ran`.

### `ug log`

A digest of the last play session, from the bridge's play log (`/playlog`) and console buffer.

- A session starts at the editor leaving edit mode (from `ug play` or the Play button) and ends
  when play mode ends; it lists the pause and resume times (seconds since the start). Only the last
  session is kept, across domain reloads, lost when the editor quits.
- Console entries of the session are folded as in `ug console`, errors and warnings only, logs
  as a count. Same budget flag and `more` line.

```
$ ug log
play 12.4 s, stopped 14:02:23  (pause 4.1 s, resume 6.0 s)
  3 errors, 1 warning, 212 logs  (last #4120)
  error x2  NullReferenceException: Object reference not set to an instance of an object  Assets/Scripts/Enemy.cs:42
  warning  The referenced script on this Behaviour is missing!
```

While playing the first line is `playing 5.3 s so far`. With no session since the editor opened:
`no play session since the editor opened`. A bridge older than 0.3.0: `bridge 0.2.0 is too old for ug log: run ug bridge install again`.

### `ug play --for <s>`

Decision on the open point (editor-link and SPEC): **yes**. `ug play` enters play mode as before;
with `--for <s>` (1 to 600) it then waits `s` seconds, runs `ug stop`, and prints the `ug log`
digest: one call instead of play, sleep, stop, log. A refusal from `play` (compile errors, already
playing) ends it there; if the editor was stopped meanwhile, the stop answer says so and the digest
is still printed. Under 30 lines of code: no new bridge request.

## Bridge change

One request, `GET /playlog`, in bridge 0.3.0 (package version too; `ug` knows it as
`MinimumLoopBridge`). `ug console` and `ug test` use requests that exist since 0.1.0 and work on any
bridge. The bridge records the session from `EditorApplication.playModeStateChanged` (leaving
edit mode: time and console id) and `pauseStateChanged`, in `SessionState`, so it survives the
reload that entering play mode causes.

```json
{"session": null}
{"session": {"start": "2026-10-01T14:02:11.000Z", "end": null, "after": 4001, "until": null,
             "events": [{"at": 4.1, "what": "pause"}, {"at": 6.0, "what": "resume"}]}}
```

`after` and `until` are console ids at the start and end; the client reads `/console?after=<after>`
and drops entries with an id above `until`. A repeat of an older entry gets a new id, so one
logged before the session and repeated in it counts as in it.

## Nothing out of reach (C1, bridge 0.9.0)

User rule (2026-10-10): answers stay compact, and whatever they leave out has a command that
shows it. Defaults do not change; a cut answer names the command that shows the rest.

- **Console entries are kept whole.** The bridge keeps each entry's message up to 8,000 characters
  (was 2,000) and Unity's whole stack trace up to 8,000 characters (was: only the first own frame),
  for every entry of the 2,000-entry ring. The ring and the two per-entry bounds are memory bounds
  (the ring is saved in `SessionState` across domain reloads) and stay; a text longer than its
  bound ends with `... (cut at 8000 characters)`. The saved format gains the stack as a sixth
  field; a buffer saved by an older bridge restores with no stacks.
- **`ug console --id <ID>`** prints one entry whole: `#4101 error x12`, the full message (every
  line), then `stack:` and the stack lines. An id the ring no longer holds: `no console entry #ID
  (the bridge keeps the newest 2000)`. It takes no other flag but `--budget`.
- **`ug console --whole`**: the same folded rows, each with the id of its newest occurrence
  (`#4101`), the full message and the whole stack under it, indented. `--level`, `--count`,
  `--after`, `--before` and `--budget` apply as usual.
- **`ug console --before <ID>`** looks at the newest `--count` entries older than the id, so
  older entries page back. With `--after` too, the window is between the two.
- **The default answer says what it left out.** One last line, only when something applies:
  `  more  whole: ug console --whole; older: ug console --before 3920` (the `--level`, `--count`
  and `--after` given are repeated). `whole` appears when a shown row was cut (a message past its
  first line or 160 characters) or has a stack beyond its frame; `older` when the ring holds
  entries before the oldest one looked at (and after `--after`). The budget's own `more` line
  stays as it is.
- **`ug log --whole`** and **`ug play --for <s> --whole`** print the session's rows that way. The
  session is read 500 entries at a time; past that the digest's last line names
  `ug console --after <after> --before <first id read> --level log` for the rest, and the counts
  line says `500+ logs` as before.
- **`ug test --failures`** lists every failure of the last run (no new run): the same row format,
  no 20-line cap, within `--budget` (default `answerBudget`), with the budget's `more` line.
  **`ug test --failure <name>`** prints one failure of the last run whole: the full test name, the
  whole message (NUnit's `Expected:` and `But was:` lines included) and the whole stack. `<name>` is
  the full name or a unique part of it; several matches list them; none: `no failure matching
  <name> in the last run`. No run since the editor opened: `no test run since the editor opened`.
  The bridge keeps every failure (was: the first 50), each with its message and stack up to 8,000
  characters (memory bounds, stated in the answer when hit). The red answer's cap of 20 rows
  stays; past it the answer says `(+N more failures: ug test --failures)`, and a row whose message
  was cut keeps its 160-character first line; when a shown row cut its message or stack the
  answer ends `  more  whole: ug test --failure <name>`. `ug test --failure` reads the last run with
  `--budget` too.
- **Compile gate.** The Stop hook's reason stays at 20 error lines; its last line names
  `ug compile --all` (SPEC-editor-link, `ug compile`).
- `--id`, `--whole`, `--before`, `--failures` and `--failure` need bridge 0.9.0; on an older bridge
  they are refused by name: `bridge 0.8.0 is too old for ug console --whole: run ug bridge install
  again`. The plain commands work on any bridge as before.

Requests (all `GET`, JSON as before): `/console?id=<ID>` answers `{"last": n, "oldest": n,
"entries": [one entry]}` or no entries; `/console?...&before=<ID>`; every entry gains `"stack"`
(null when Unity gave none) and the page gains `"oldest"`, the oldest id the ring holds.
`/tests/result?id=` (empty id) answers the last run; each failure gains `"stack"`.

## Compile gate

Two handlers of the module `unity-loop` on the hooks already wired in `hooks/hooks.json`; silent
unless it fires (design rules 3 and 9).

- **Edit tracking (PostToolUse on Edit, MultiEdit, Write, NotebookEdit).** A file with the extension `.cs`,
  `.asmdef` or `.asmref` under `Assets/` or `Packages/` of the Unity project marks the session
  dirty: it creates `Library/unitygraph/gate-<session_id>` (our own folder, where `bridge.json`
  and `hook-errors.log` live). Nothing else is written, nothing is printed.
- **Stop.** No marker for the session: exit at once, no output, no Unity call (0 tokens, about 100 ms).
  With a marker: connect to the bridge; if the editor is playing or paused the check is skipped and
  the marker stays; otherwise the marker is deleted and a compile runs as in `ug compile` with a
  30 s deadline. No errors: silent. Errors: the answer is the `ug compile` text (count line and at
  most 20 error lines) as `{"decision":"block","reason":"..."}`, so Claude fixes them before it
  stops. The marker is gone, so a second Stop without a new script edit is silent: no loop.
- **Fail open.** Unity closed, bridge missing or old, refused, timed out, malformed answer: no
  output, marker deleted, nothing logged (Unity being closed is not a plugin failure). A handler
  exception is logged to `hook-errors.log` by the dispatcher as for every hook.
- **Time.** The Stop hook may take up to 40 s (hooks.json 45 s); the dispatcher's 2 s limit stays for
  the tool hooks. The tracker is a path check and one small file write.
- Edits made through Bash (`sed`, `mv`) are not seen; `ug compile` covers them.

## Testing

- **Client and formatting** (`tests/UnityGraph.EditorLink.Tests`, `UnityGraph.Cli.Tests`, fake
  bridge): console folding, ordering, level filter, `--after`, budget cut and `more`, empty
  answers; test run green, red with 20-line cap, no tests, compile errors first, a refused
  connection mid-run with a new port, timeout, 501 and 409; log with and without a session, old
  bridge; `play --for`; unreachable answers for each command; the gate's check (clean, errors,
  playing, unreachable, timeout) against the fake bridge; the modified-scene refusal and a stopped
  run for `ug test` and `ug test --failures`.
- **Hooks** (`tests/UnityGraph.Hooks.Tests`): the recorded PostToolUse and Stop payloads: a `.cs`
  edit marks, a `.txt` or outside-project edit does not, Stop with no marker is silent and calls
  nothing, errors give the block JSON, a clean check and an unreachable bridge are silent, the
  marker is deleted, a second Stop is silent, module off is silent.
- **Bridge** (EditMode in `fixtures/BridgeHost`): `PlayLog` recording from simulated state changes,
  JSON shape, survival through `Save` and `Restore`. A modified untitled scene refuses a run and
  starts none; a running id the test runner does not know answers `stopped` and frees the bridge
  for the next run.
- **End to end** (`UG_UNITY`): `ug console` shows a logged fixture error, `ug test` runs the
  fixture's EditMode tests green and one red, `ug log` after a `ug play --for 2`; with Main.unity
  modified `ug test` answers the refusal at once, and after `ug save` the run goes. Times and token
  sizes go in Measured.

## Boundaries

- Always: the gate stays silent when clean and when Unity is out of reach; answers within budget;
  a hook never edits project files (only its marker in `Library/unitygraph/`).
- Ask first: any further bridge request that changes the project; a new hook event; a network call
  (none here).
- Never: block a Stop for anything but compile errors; loop a Stop; wait for Unity past the deadline.

## Measured

End to end on a copy of `fixtures/BridgeHost`, Unity 6000.3.14f1, batch mode (`UG_UNITY`, 2026-10-01).
Tokens are bytes / 4 of the printed answer.

| What | Time | Answer |
|---|---|---|
| `ug console`, 2 errors | 0.03 s | 49 tokens |
| `ug test --filter E2E`, 1 red, 1 green | 0.35 s | 25 tokens |
| `ug test --filter PlayLogTests`, 9 green | 0.29 s | 7 tokens |
| compile gate check, 1 error | 0.6 s | block reason, the `ug compile` text |
| compile gate check after the fix (domain reload) | 1.6 s | silent |
| `ug play --for 2` | 3.4 s | `ug log` digest, 2 lines when clean |
| `ug test --filter E2E`, Main.unity modified (refused, F2, 2026-10-11) | 0.04 s | 31 tokens |
| `ug test`, run canceled 6 s into a 20 s test (F2, 2026-10-11) | 7.3 s | `EditMode: canceled in Unity before it finished` |

Not measured: a PlayMode `ug test` run, and a Stop hook against a project with many errors (cut at 20 lines by design).

Not yet understood: in the end-to-end run a `Debug.LogError` made by `ug run` right after `ug stop` (play mode left, domain reload) did not reach the console buffer, while the same call before any play session did. Editor.log had it. The e2e checks `ug console` before the play steps for that reason.

## Open points

None.
