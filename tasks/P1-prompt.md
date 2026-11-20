# P1: finish editor-ops, then make unitygraph ready to publish

Repo: `D:\Projects\unitygraph` (Claude Code plugin + `ug` CLI + Unity bridge package). Read first:
`SPEC.md`, `specs/SPEC-editor-ops.md`, `tasks/plan.md`, `tasks/todo.md`, your memory files.
O1-O9 are done and committed (editor-ops: scenes, objects, prefabs, assets, project, packages,
shots, API lookup, perf, batch). Nothing is published yet and
there is no git remote.

Status 2026-10-11: Part 1 is done (U1). Since this prompt was written L1-L5, S1, I1-I4, Q2, G1
and the MCP gap tasks R1 X1 O11 O13 U2 O12 are done (bridge 0.8.0). Next is Part 2 (O10); W1,
V1, B1, D1 stay open. The state list below is from 2026-09-30.

State of the tree when this prompt was written:
- Git history was rewritten to drop the `Co-Authored-By: Claude` trailers (44 commits, author
  and committer are Umar). Backup: `before-rewrite.bundle` in the previous session's scratchpad.
  Do NOT add a Co-Authored-By or "Generated with Claude" line to any commit from now on.
- Build output and caches were deleted: `artifacts/`, `dist/`, `TestResults/`,
  `fixtures/BridgeHost/{Library,Logs,UserSettings}`, and the installed copy of the plugin under
  `~/.claude/plugins/cache/unitygraph-dev`. Rebuild what you need (`dotnet build`; BridgeHost
  reimports on first Unity run, slow).
- `refs/original/refs/heads/main` (old commits with the trailers) still exists locally; the
  user decides whether to purge it (`git update-ref -d`, reflog expire, gc). Ask; do not do it
  unasked.

## Rules that still hold
- Do only what is asked, then stop and report. Commit only at the end of a task, message in
  O8 style, no attribution trailer.
- Do not commit `tasks/*-prompt.md`.
- Ask before: a permanent delete path, any network call other than the docs.unity3d.com
  fallback, anything the spec's Safety rules do not allow. Questions from the user are not
  approvals. Anything the spec does not cover: ask, do not invent.
- LF files, follow surrounding style, TreatWarningsAsErrors, `dotnet format` clean.
- Scratch output goes in the session scratchpad. Unity is launched via PowerShell
  `Start-Process` with absolute `-testResults` and `-logFile`; e2e needs
  `UG_UNITY="D:\Program Files\Unity Editors\6000.3.14f1\Editor\Unity.exe"`.
- Chat replies terse (caveman); every status message ends with x/y tasks done or %.

## Part 1: UI check (before O10) (done: U1)
Editor-ops has no UI-specific help. On a scratch scene (not purge-squad yet), build with `ug`
only: a uGUI Canvas with EventSystem, a Button with a Text/TMP label wired to an onClick
method, anchors and sizes through `ug set` on RectTransform; then a UI Toolkit screen (UXML +
USS written as files, a UIDocument on a GameObject, `ug shot` to check it). Record what needed
too many calls or failed. Add only the helpers the session shows are needed, each with EditMode
tests and a spec line first (ask if the spec does not cover it).

## Part 2: O10, three-way measurement on purge-squad
Updated 2026-10-11, after R1 X1 O11 O13 U2 O12 (bridge 0.8.0). The question changed on
2026-10-10: not "ug vs the MCP server" but which editor side to keep. Options from that day:
A. `ug` does everything (current); B. `ug` graph only + MCP for Unity; C. `ug` graph only +
Unity's CLI; D. hybrid, `ug` commands served through Unity's Pipeline (`[CliCommand]`), dropping
our bridge and transport. Rule agreed then: if `ug` is not clearly cheaper than the best other
leg (about 25% fewer tokens at equal correctness), report D with the numbers; do not start it.

The three legs and what is on this machine (checked 2026-10-11):
- `ug`: bridge 0.8.0 copied into purge-squad (`Assets/Plugins/UnityGraphBridge/`, uncommitted
  there; reinstall with `ug bridge install --copy` if main moved on, then `ug compile`).
- Unity CLI: `unity` 1.0.0-beta.8 at `C:\Users\umara\AppData\Local\Unity\bin\unity.exe`;
  `com.unity.pipeline` 0.7.0-exp.1 is already in purge-squad's manifest (the user's commit
  1a422d2, 2026-09-22). Two ways in: `unity` commands from Bash (read the `unity:unity-cli`
  skill for the syntax), and `unity mcp`, which is the user-level MCP server
  `unity-editor-mcp` (`~/.claude.json`, `unity mcp --project-path D:\Projects\Unity\purge-squad`),
  so its tools are already in every session (deferred: names only until ToolSearch loads them).
  Measure both ways. The purge-squad history's biggest cost, `eval`/`run_script` at 16.6% of
  context, came from this server.
- MCP for Unity (CoplayDev; tool list in
  `~/.claude/skills/unity-mcp-skill/references/tools-reference.md`): NOT installed. It needs its
  Unity package from a git URL and its Python server (run with `uv`; check its README): both
  network and a package add, so ASK first. If the user says no, measure its idle cost from the documented schemas only and say
  the per-chore leg was not run.

Steps:
1. Spec first, no review stop: a "Three-way measurement (O10)" section in
   `specs/SPEC-editor-ops.md` (replacing nothing; it is where the Objective's "measured reason"
   lands): the chores, the method, the legs, the decision rule above.
2. Chores: 8 to 10 real purge-squad tasks that each leg can do, for example: find an object and
   read live component values; set a serialized field and read it back; create an object with
   components under a parent at a position; instantiate a prefab and apply an override; enter
   play mode, read new console errors, stop; break a script, recompile, read the errors, fix;
   a game-view picture and a viewpoint of one object; run EditMode tests and read failures;
   1 s of play-mode profiling with the top markers; click a UI button in play mode and confirm
   the screen changed. Add one graph question with the editor closed (which prefabs and scenes
   use a script), where only `ug` answers without the editor.
3. Method, deterministic first: per leg and chore, the call sequence an informed agent would
   use, run for real against purge-squad's open editor; record calls, request + answer bytes / 4,
   wall time, and correctness (checked by a `ug` or `unity` read after). Where a leg has no
   command for a step, use its eval, as Claude would, and count it. Idle cost per leg: `ug`'s
   skill description and SKILL.md body; each MCP server's `tools/list` over stdio (bytes / 4,
   full schemas) and the deferred names list. Use `ug stats` on purge-squad's past sessions for
   the before numbers.
4. Optional, ASK first (it spends the user's usage): the same chores as `claude -p` runs, one
   leg at a time (`--strict-mcp-config`, the other legs' tools disallowed), reading tokens and
   tool calls from the JSON output. This shows whether a thin toolset pushes Claude to eval.
5. purge-squad is the testing ground (edits allowed, it is in git), but every edit goes through
   Undo and nothing is saved; `git -C D:\Projects\Unity\purge-squad status` before and after
   must match (the bridge copy is already uncommitted there: leave it). Do not restart or close
   the user's open editors. The Frame Debugger is refused there (the Hub starts it with
   `-automated`): leave that chore out or ask.
6. Results: a table per chore (calls, tokens, time, correct) and the idle costs, in the spec
   section; the verdict against the rule; tick O10 in `tasks/todo.md` with the numbers; commit
   (O8 style, no attribution trailer). Report the numbers and the verdict, and stop: A/B/C/D is
   the user's decision.

## Part 3: publish readiness (tell the user the plan and get a yes on the open decisions first)
1. Repo basics: `README.md` (what it is, install, the command table, safety rules, requirements:
   .NET 8+ runtime, Unity 2022.3+), `LICENSE` (plugin.json and package.json already say MIT; ask
   the user for the copyright name), `CHANGELOG.md`.
2. Distribution of `ug`: the plugin launcher `bin/ug` runs `dist/ug/ug.dll`, which is gitignored,
   so a plugin installed from git has no binary. DECISION for the user: (a) commit a prebuilt
   `dist/ug` on a release branch or tag, (b) release workflow attaches a zip and the launcher
   downloads it (a network call: needs approval and a spec rule), or (c) launcher builds from
   source (needs the SDK). Do not pick one silently.
3. Release workflow `.github/workflows/release.yml` on tag `v*`: `dotnet publish src/UnityGraph.Cli
   -c Release -o dist/ug` (framework-dependent, 29 MB with sqlite natives for the six desktop
   platforms), run tests, attach the result. CI today runs windows + macos; consider ubuntu.
4. Bridge as a UPM package: install from git URL `...?path=unity/com.unitygraph.bridge`; check
   `package.json` fields (`unity: 2022.3`, repository, keywords, `documentationUrl`,
   changelog and license files with `.meta`). `ug` needs an install/update command or clear
   messages for a missing or old bridge (MinimumOpsBridge is 0.2.0).
5. Plugin manifest: `.claude-plugin/plugin.json` version, `marketplace.json` (currently the
   local "unitygraph-dev" marketplace, source "./"): decide the published marketplace name and
   layout; run `claude plugin validate`.
6. Unity version matrix: only 6000.3.14f1 is tested (a 2022 log `bh2022.log` existed once; check
   it). Test 2022.3 LTS and 6000.0 or state the supported range in the README.
7. Safety pass: no permanent delete path, network use limited to docs.unity3d.com and Unity's
   own Package Manager, bridge listens on 127.0.0.1 only, hooks fail open.
8. Fresh-install test: clean clone, build, install the plugin from the marketplace path into a
   throwaway Claude config or scratch project, run `ug doctor`, a query, and an editor-op
   against BridgeHost.
9. Open spec points (SPEC-editor-ops "Open points"): addressing by `o:` keys through
   `GlobalObjectId`; whether `ug play --for <s>` belongs in editor-ops. Ask before building.

## Done when
O10 measured, Part 1 gaps closed or listed, Part 3 checklist done or with the user's decisions
recorded in `tasks/todo.md`, all tests green (dotnet 971+, EditMode 309+, e2e), then report.
