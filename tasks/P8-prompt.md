# P8: the benchmark (B1)

Repo: `D:\Projects\unitygraph`. Read first: `SPEC.md` (Goal, Evidence, Overhead budget, Decisions
#10, the capability map rows `stats` and `bench`), `specs/SPEC-editor-ops.md` (Three-way
measurement, Results, Rooms: the O10 method to reuse), `src/UnityGraph.Stats/`,
`bench/baselines/purge-squad-2026-09.json`, `tasks/todo.md`, your memory files.

## Where things stand (2026-10-11)
- Everything before B1 is merged on `main` (b1f1af5): F3, F4, O14, X1, R1 and the F3 undo/redo
  names landed together, bridge 0.15.0. No other branches or worktrees. purge-squad runs bridge
  0.15.0 (`ug status`), 0 compile errors.
- Commit history was rewritten to drop `Co-Authored-By: Claude` trailers: do not add them.
- Open in `tasks/todo.md`: B1 (this task) and D1 (`o:` keys through `GlobalObjectId`), which is
  the user's decision: do not build D1.

## Why
SPEC.md's goal is -30% main-thread context (stretch -40%) and -15% turns with equal or better
correctness, "measured by `stats` and a benchmark". Today the numbers are O10's chore runs and
purge-squad replays; nothing scores questions across the four arms or fails a release that
breaks an overhead budget.

## What to build
1. Spec first (no review stop): `specs/SPEC-bench.md`. Question suites per project (navigation:
   where/what calls/what breaks; asset: who references this prefab/material/scene object;
   editor chores reusing O10's), each question with a checked answer key. Arms: grep baseline,
   codegraph, Graphify, unitygraph. Scores: calls, main-thread tokens (from `ug stats` over the
   run's transcript), wall time, correctness against the key, redo rate. Overhead budget checks
   (skill description, skill body, compile gate) that fail the run.
2. `ug bench` (or a `bench/` runner, whichever the spec argues for): runs a suite, writes JSON
   results beside `bench/baselines/`, compares to a baseline, exits non-zero on a broken budget.
3. purge-squad suite first, end to end, all four arms.

## Ask the user before
- Downloading Boss Room, the URP 3D Sample or Chop Chop (network, licence check per
  Decisions #10), or installing Graphify or anything new.
- Any headless `claude -p` runs: give the run count and a dollar estimate first (the user
  watches spend; report main + subagent context and $ as `ug stats` gives them).

## Done when
`specs/SPEC-bench.md` written; purge-squad suite scored on all four arms with answer keys
checked by hand; dotnet tests and format clean; `tasks/todo.md` B1 entry with the numbers.
End each status message with x/y tasks done.
