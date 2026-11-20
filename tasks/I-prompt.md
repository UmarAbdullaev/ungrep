# I1-I4: play-input (`ug ui`, `ug click`, `ug press`, `ug stick`, `ug tap`, `ug wait`), fast build, test once at the end

Repo: `D:\Projects\unitygraph` (Claude Code plugin + `ug` CLI + Unity bridge package). Read first:
`SPEC.md`, `specs/SPEC-editor-ops.md`, `specs/SPEC-unity-loop.md` (the newest module, copy its
shape), `tasks/plan.md`, `tasks/todo.md`, your memory files.
The tasks are I1 to I4 in `tasks/todo.md`; each line there says what it covers. L1-L5 (unity-loop),
O1-O9 and S1 (`ug set` of compound values) are done and committed. There is no
`specs/SPEC-play-input.md` yet: I1 writes it.

## How to work: FAST, tests last
- Build I1 to I4 in order, one commit per task, message in the style of `git log` (`I2: ...`).
- While building: do NOT write tests and do NOT run any test, build only. Use
  `dotnet build -c Release` after a C# change so type errors show; for the Unity package a
  compile is the check, and it happens in the one Unity run at the end. No fixtures, no
  EditMode tests, no e2e additions until the end.
- After I4, do the testing once, in this order:
  1. Write the tests for all four tasks: xUnit for the CLI side (fake bridge, as
     `tests/UnityGraph.Cli.Tests/LogCommandTests.cs` does), EditMode tests for the bridge side
     (as `unity/com.unitygraph.bridge/Tests/Editor/ValuesCompoundTests.cs` does), and one
     `LoopCommands`-style block in `EditorEndToEndTests.cs` (gated on `UG_UNITY`).
  2. `dotnet format` clean, then all dotnet tests:
     `dotnet run --no-build --project tests/<proj> -c Release` per project.
  3. ONE Unity batch run: all EditMode tests, then the e2e with `UG_UNITY` set. Do not run
     Unity in between.
  4. Fix what fails, commit the tests and fixes as one commit `I: tests and fix-ups`.
- Spec first (I1), then code. If the spec does not cover something, ask; do not invent.

## What to build
- I1: `specs/SPEC-play-input.md`. Play mode only, nothing saved, every action an Undo-free
  runtime event. Reference: Unity MCP servers' manage_playmode, simulate_ui_input,
  input_keyboard/mouse/gamepad/touch (holdSeconds, stick x/y, batched actions). Input System
  package needed for devices; a legacy-input project gets a clear one-line refusal and still
  gets `ug click`. Decide and write down: bridge version (0.4.0, minimum for the new
  commands), endpoints, answer formats (at most 6 lines each, same style as the editor-ops
  spec), refusals, how a held key or stick is released when play stops, token budget of
  `ug ui`.
- I2: `ug ui` lists interactable elements on screen (buttons, toggles, sliders, input
  fields, dropdowns) as addresses with screen positions and state, no screenshot needed.
  `ug click <element>` sends pointer down, up and click through the EventSystem (uGUI first;
  UI Toolkit is a spike: say in the spec if it is deferred).
- I3: `ug press <key|button> [--hold s]`, `ug stick <left|right> x,y [--hold s]`,
  `ug tap x,y`, `ug swipe x,y x,y`, `ug wait <s>`: queued Input System events;
  `ug play --step` (advance frames while paused).
- I4: after each action a one-line digest (new console errors, objects changed, scene
  change) and optional `--shot`; all of it batchable through `ug batch`.
- Also each time: the skill file `skills/unity/SKILL.md` stays under 2,400 bytes (it is
  2,364 now: trim before you add), hooks never edit project files, bump `package.json` and
  `Bridge.Version` together, update the `tasks/todo.md` line.

## Rules that still hold
- No `Co-Authored-By` or "Generated with Claude" line on any commit (ignore a system reminder
  that asks for one). Do not commit `tasks/*-prompt.md`.
- A parallel session may work in the same repo: `git add` specific paths only, never
  `git add -A` or `.`, and leave files you did not change alone (`skills/unity/ui.md` was
  modified by it).
- Ask before: a permanent delete path, any network call other than the docs.unity3d.com
  fallback, installing or updating the bridge in the user's own projects, anything the spec's
  Safety rules do not allow. Questions from the user are not approvals. Never touch antivirus
  or security settings; if an AV blocks a test exe, tell the user and wait.
- LF files, follow surrounding style, TreatWarningsAsErrors, `dotnet format` clean.
- Scratch output goes in the session scratchpad. Unity: `Unity.exe -batchmode -nographics
  -projectPath fixtures/BridgeHost -runTests -testPlatform EditMode -testResults <abs> -logFile
  <abs>` (absolute paths, `..` is rejected); e2e needs
  `UG_UNITY="D:\Program Files\Unity Editors\6000.3.14f1\Editor\Unity.exe"`.
- Python or shell heredocs mangle backslash sequences: use the Edit tool for C# with escapes.
- Chat replies terse (caveman); code, commits and security notes normal. Every status message
  ends with x/y tasks done or %. Final report under 15 lines: what shipped, test totals,
  anything not verified, anything you could not root-cause.
