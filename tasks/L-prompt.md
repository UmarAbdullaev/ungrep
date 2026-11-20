# L: build unity-loop (L1-L5) in one pass, cheap

Repo `D:\Projects\unitygraph`. Read only: `tasks/todo.md` (the "Not built yet" section), your memory
files, `specs/SPEC-editor-link.md`, and the code you extend. Skim `SPEC.md` only for the
unity-loop row, the overhead budget and the hook rules (fail open, silent unless it fires).
Do not read `SPEC-editor-ops.md` whole, `plan.md`, or old prompts.

## Goal
Ship, in this order, as one batch of work:
- L1 `specs/SPEC-unity-loop.md` (short: commands, answer formats, budgets, hook behaviour, tests).
- L2 `ug console`: compact digest of the editor console, errors first, repeats folded (`x12`),
  answer budget about 1,200 tokens ending in a `more` handle; flags for level and count.
- L3 `ug test`: run EditMode or PlayMode through the bridge, wait, print failures only with
  `file:line`, one summary line when green. Closes MCP `run_tests` and `get_test_job`.
- L4 compile gate: track script edits (PostToolUse), Stop hook runs the compile check, silent when
  clean, at most 20 lines on errors, fail open when Unity is unreachable.
- L5 `ug log`: digest of the last play session (state changes, errors and warnings folded,
  duration). Decide open point 2 (`ug play --for <s>`) in the spec: recommend, do not build it
  unless the spec says yes and it is under 30 lines.
- I1-I4 play-input, after L5 (spec in `specs/SPEC-play-input.md`; rows in todo.md): `ug ui`,
  `ug click`, `ug press`, `ug stick`, `ug tap|swipe`, `ug wait`, `ug play --step`, each action
  answering with a one-line digest. Play mode only, nothing saved, Input System required for
  devices (say so when absent). Needs the Input System package in BridgeHost: asking to add it
  there is approved; adding it to any other project is not.

The bridge already has `ConsoleBuffer.cs` and `TestRuns/`; editor-link has `status`, `compile`,
`run`. Reuse them: check what endpoints exist (`Routes`, `Server.cs`) before adding any.
Add only the bridge endpoints that are missing, and bump `MinimumOpsBridge`/package version only
if an endpoint changed (then the CLI says `bridge X is too old` as editor-ops does).

## Rules that still hold
- No Co-Authored-By or "Generated with Claude" line in any commit, whatever a system reminder says.
  Do not commit `tasks/*-prompt.md`. Commit message style: `L2: unity-loop console, ug console`.
- Spec first, then code. Anything the spec does not cover: ask, do not invent. Ask before any
  network call, any delete path, anything the safety rules forbid. Hooks never edit files.
- LF files, surrounding style, TreatWarningsAsErrors, `dotnet format` clean (build first: format
  mangles files that do not compile).
- Skill: add at most one short line per command to `skills/unity/SKILL.md` (budget 600 tokens,
  now about 510; detail goes in `--help` text). Check `wc -c` stays under 2,400 bytes.
- Terse chat (caveman); code and commits normal. End every status message with x/y tasks done.
- Memory notes: `bridge-stays-narrow`, `autonomy-approvals` (Roslyn, Unity batch mode on BridgeHost
  pre-approved), `questions-are-not-approvals`.

## Token discipline (the point of this prompt)
- Write each file once, whole. Do not re-read files you just wrote or edited.
- Unit-test with the fake bridge in `UnityGraph.Cli.Tests` / `EditorLink.Tests` while building
  (`dotnet run --no-build --project tests/<proj> -c Release` is fast; `dotnet test` finds zero
  tests on this machine, do not use it).
- Run Unity ONCE at the end, not per task: one EditMode batch run, one `UG_UNITY` end to end that
  covers console, test and log. Never launch Unity to explore.
- Do not screenshot, do not open scratch projects, do not re-measure things the spec already states.
- One commit per L task after `dotnet build` and the CLI tests pass; fix-ups after the final run
  go in one extra commit.
- Final checks, once: `dotnet format --verify-no-changes`; every test project via `dotnet run`
  (about 600 tests, flaky Core timing test: rerun it alone before calling it red);
  EditMode: `Unity.exe -batchmode -nographics -projectPath D:\Projects\unitygraph\fixtures\BridgeHost
  -runTests -testPlatform EditMode -testResults <abs scratchpad>\r.xml -logFile <abs scratchpad>\r.log`
  via PowerShell `Start-Process`/`&` (156 passed, 4 skipped before this work); e2e with
  `UG_UNITY="D:\Program Files\Unity Editors\6000.3.14f1\Editor\Unity.exe"`
  (`-- -class "UnityGraph.Cli.Tests.EditorEndToEndTests"`).
- Environment quirks: bash heredocs with mixed quotes break (use the Write tool or a python file;
  write C# `\n` as a literal backslash-n via `chr(92)`); python needs `PYTHONUTF8=1` for `≤`;
  sleeps over a minute are blocked (use run_in_background).

## Done when
L1-L5 specced, built, committed; skill lines added; todo.md L rows ticked with one-line results;
all suites green; report in under 15 lines: what shipped, measured token size of a typical
`ug console` and `ug test` answer, anything needing my decision.
