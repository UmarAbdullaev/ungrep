# P7: the Unity worker subagent (W1)

Repo: `D:\Projects\unitygraph`. Read first: `SPEC.md` (Evidence, Design rules 1, 3 and 7,
Overhead budget, the capability map row `worker-agent`, Project structure), `skills/unity/SKILL.md`,
`specs/SPEC-editor-ops.md` (Three-way measurement, Results, Rooms), `specs/SPEC-unity-loop.md`,
`tasks/todo.md`, your memory files.

## Where things stand (2026-10-11)
- Done and committed: everything up to F1 (83fb145, bridge 0.10.0). O10 measured `ug` 45% fewer
  tokens than the best other leg (52% on the rooms run), so by the rule agreed on 2026-10-10
  option A stands (`ug` does the editor side too); D is not started.
- Open in `tasks/todo.md`: W1 (this task), then V1 viewer (`specs/SPEC-viewer.md`, untracked,
  user decisions of 2026-10-01), B1 bench, D1 editor-ops points.
- No `specs/SPEC-worker-agent.md` and no `agents/` folder yet. `ug stats` already sorts
  subagent traffic into its own category (`src/UnityGraph.Stats/Categorizer.cs`).

## Why
SPEC.md Evidence: on purge-squad's real sessions a worker subagent for Unity and navigation
streaks was the biggest lever left (-36% to -41% context with graph + compile gate, against -21%
to -33% without). Multi-step Unity chores (play to a state, edit and verify, set up a scene) fill
the main thread with intermediate answers that are paid on every later turn.

## What to build
1. Spec first (no review stop): `specs/SPEC-worker-agent.md`, written from SPEC.md and what
   the code does. It covers:
   - the subagent file (`agents/unity-worker.md`): name, description, tools, model;
   - the delegation rule (only chores of 3 or more steps; a 1 or 2 step chore stays inline);
   - the verdict contract: at most 800 tokens; what was done; what was checked and how; evidence
     anchors (`file:line`, object paths, asset paths); anything left undone or unverified; the
     `ug` commands to re-check;
   - failure behaviour (Unity gone, compile errors, a chore that cannot finish);
   - what the worker may not do (save scenes unasked, commit, install packages, the Ask list);
   - how it is measured.
   Check the plugin agent format (frontmatter fields, how `tools` and `model` are given, how a
   plugin's agents are found) against the Claude Code docs with the claude-code-guide agent; do
   not guess it.
2. `agents/unity-worker.md`: the worker's own prompt, `ug`-first, the editor-ops rules (Undo,
   nothing saved, play mode only for input), compact answers, the verdict format.
3. Delegation rules in `skills/unity/SKILL.md`: the body stays under 600 tokens (the budget test
   checks it). Longer guidance goes in a file beside it, read on demand.
4. Overhead: skill and agent descriptions together at most 300 tokens loaded every session. Add
   a test like the skill budget test for the agent description.
5. `ug stats`: count delegations and short ones (fewer than 3 tool calls inside the worker), as
   SPEC.md Overhead budget says ("`stats` counts short delegations"). Tests on recorded
   transcripts.
6. A dry run on purge-squad: 2 or 3 chores of 3+ steps done once inline and once delegated (for
   example: play until the first room is cleared and report the HUD; add a component, set two
   fields, check them in play mode, undo). Main-thread tokens for each. Real agent runs spend the
   user's usage: ask before any `claude -p` or subagent run, say how many and roughly what they
   cost.

## Rules
- Spec first; tests for everything testable; `dotnet format`; tick `tasks/todo.md` with numbers
  when done; one commit, O8 style, no attribution trailer, no Co-Authored-By.
- Ask before: a graph schema version bump, any new package, any network call other than the
  docs.unity3d.com fallback, a permanent delete path, registering MCP servers or changing
  persistent Claude config (installing the plugin into a project counts). Anything the spec does
  not cover: ask, do not invent. Questions from the user are not approvals.
- Do not commit `tasks/*-prompt.md`, `specs/SPEC-viewer.md` or the user's `skills/unity/ui.md`
  change. LF files; TreatWarningsAsErrors; follow the surrounding style.
- purge-squad: every edit through Undo, nothing saved, `git status` the same as at the start
  (the bridge copy in `Assets/Plugins/UnityGraphBridge/` already shows as modified there; leave
  it). Do not restart or close the user's open editors (purge-squad, captain_of_space,
  tashkent/game).
- EditMode runs in `fixtures/BridgeHost` in batch mode with absolute `-testResults`/`-logFile`;
  e2e needs `UG_UNITY="D:\Program Files\Unity Editors\6000.3.14f1\Editor\Unity.exe"`.
- Replies terse; every status message ends with x/y tasks done or %.

## Report at the end
What was built, test counts, the dry-run numbers (inline against delegated, main-thread tokens
and verdict sizes), decisions the spec now records, open asks. End with x/y tasks done (W1, V1,
B1, D1).
