# P2: Q2 (outline, impact), G1 (guards), W1 (worker agent), B1 (bench), D1 (open points)

Repo: `D:\Projects\unitygraph`. Read first: `SPEC.md` (module table, Decisions 4-7 and 10,
Boundaries, Success criteria), `specs/SPEC-query.md`, `specs/SPEC-code-index.md`,
`specs/SPEC-asset-index.md`, `specs/SPEC-editor-ops.md`, `tasks/plan.md`, `tasks/todo.md`, and
your memory files. O1-O9 and I1-I4 are done. Not in this prompt: V1 (viewer, needs a design
discussion with the user), O10 and the publish work (tasks/P1-prompt.md). M1 (project-map) was
dropped by the user on 2026-10-01: remove it from `SPEC.md` (module table, build order,
config example, Decisions #7, source layout) as part of Q2's spec edit, and ask first if
removing it breaks something else.

## Rules that still hold
- One task at a time, in the order below. Write or update the spec first; the user reviews it
  only where this prompt says "ask". After each task: tests, `dotnet format`, tick `tasks/todo.md`,
  commit (O8 style, no attribution trailer, no Co-Authored-By), report in a few lines.
- Do not commit `tasks/*-prompt.md`.
- Ask before: a permanent delete path, any network call other than the docs.unity3d.com fallback,
  writes into a user's Unity project beyond what the spec lists, anything the spec's Safety rules
  or Boundaries do not allow. Questions from the user are not approvals. Anything the spec does
  not cover: ask, do not invent.
- Hooks never block the session: fail open, silent unless they have something to say (design
  rule 9). Hooks never change scenes or prefabs.
- Tests need no network and no Unity unless env-gated (`UG_UNITY`); LF files; follow surrounding
  style; TreatWarningsAsErrors. Scratch output in the session scratchpad.
- Unity launches via PowerShell `Start-Process` with absolute `-testResults` and `-logFile`;
  Unity 6000.3.14f1 at `D:\Program Files\Unity Editors\6000.3.14f1\Editor\Unity.exe`.
- Chat replies terse (caveman); every status message ends with x/y tasks done or %.

## Q2: `ug outline <file>` and `ug impact <symbol>`
SPEC-query.md lists both as later modules. Add them to the spec, then build.
- `ug outline <file>`: members with line numbers and no bodies, for .cs files from the code
  graph; answer budget applies; for scene/prefab files the object tree is already `ug tree`/
  `ug inspect`, so decide (and write down) whether outline covers them or points there.
- `ug impact <symbol>`: what breaks if the symbol changes: references grouped by kind (callers,
  overrides, implementations, type uses, serialized-data holders), counts of assets that hold
  serialized data of that type or field, `exact` vs `heuristic` confidence tags, `file:line`
  anchors, cut to the budget with "N more" lines. Same conventions as `find/refs/explain`.
- Tests: golden answers on the fixture project, budget cut, ambiguous-name handling, unknown
  symbol, JSON output. Env-gated measurement on purge-squad: record timings in the spec.
Ask: only if the answer shape needs a choice the spec does not cover.

## G1: guards (spec first: `specs/SPEC-guards.md`)
Spec section "guards" in SPEC.md names the cases: serialized field renamed or removed (with the
number of assets holding data), `[SerializeReference]` types moved, `.meta`-safe mv/cp/rm, edits
to vendored folders, and a configurable runtime-lookup lint. Write the spec with, for each case:
trigger (which tool call, which file), what is read from the graph, the exact message (at most
~5 lines), allow/deny/warn decision, and how the user turns it off (config). Decide with the
user (ask) whether each case blocks (PreToolUse deny with a reason) or only warns; the S3 spike
found deny+answer was retried in 9/10 runs, so warn-first is the safe default. Then build as
hook handlers in `UnityGraph.Hooks`, silent unless they fire, using the existing dispatcher.
- Success criterion 5: every fixture case caught; replayed purge-squad history (recorded tool
  calls, `research/`) shows no block that was not a real mistake. Build the replay test.
- Guards read the graph only; if the graph is stale or missing they stay silent.

## W1: worker agent (spec: `specs/SPEC-worker-agent.md`)
`agents/unity-worker.md` (plugin file) and delegation rules in `skills/unity/SKILL.md`.
- Delegate only chores of 3+ steps (compile and read errors, play-and-observe, scene setup with
  `ug batch`); a 1-step chore is cheaper inline. Verdict at most 800 tokens, containing: what was
  done, what failed with exact error text, the object addresses and file:line anchors needed to
  continue, and what was left undone, so the main thread does not repeat the chore.
- Default model Sonnet, configurable (Decision 6); tools limited to Bash (`ug`), Read, Grep.
- Verify with a scripted dry run on BridgeHost (UG_UNITY) and a replay of recorded chore
  sequences if `research/` has them. The measured redo rate belongs to B1.
- `claude plugin validate` must still pass.

## B1: bench (spec: `specs/SPEC-bench.md`; module "bench" in SPEC.md)
- Suites under `bench/suites/*.yaml`: question, expected answer (facts to match), allowed tools.
  Arms: grep baseline, codegraph, Graphify, unitygraph. Scores: tool calls, tokens (via
  `ug stats`), correctness (match against expected facts, no model judge unless the user agrees),
  redo rate for the worker.
- Projects (Decision 10): purge-squad plus 2-3 open Unity projects (Boss Room, URP 3D Sample,
  Chop Chop). Check each licence before copying any file into `bench/`; prefer pinned git
  references over copies. Ask before cloning anything (that is network use).
- `ug bench run <suite> --arm <arm>` and a compare report; a release gate that fails when a
  success criterion regresses. Running arms costs real model tokens: build the harness and a
  tiny suite, run once, then tell the user the estimated cost before any full run.
- The harness must work on Windows and macOS; scripted `claude -p` runs, transcripts read by the
  existing stats reader.

## D1: decide the open editor-ops points (a discussion, not code first)
Prepare a short comparison for each and ask the user to choose:
1. Addressing by the graph's `o:` keys (scene file + fileID) through `GlobalObjectId`, so a
   `ug refs` answer feeds `ug inspect` directly: options, failure cases (prefab instances,
   unsaved scenes), recommended option.
2. UI Toolkit in `ug shot`: an offscreen panel spike (render a UIDocument/PanelSettings to a
   RenderTexture) versus "game view only"; limits in batch mode and with no graphics device.
3. `ug play --for <s>`: play, wait, stop, digest in one command, here or in `ug log`.
Then implement only what the user picks, with EditMode and fake-bridge tests.

## Done when
Q2, G1, W1, B1 built and committed with tests green (dotnet, EditMode, e2e where gated), D1
decisions recorded in `specs/SPEC-editor-ops.md` and implemented, `tasks/todo.md` ticked, and a
short report with counts and any decisions you need from the user.
