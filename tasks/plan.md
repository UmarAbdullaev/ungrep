# Plan: step 1 (spikes, stats, plugin-shell, graph-core)

Source: `../SPEC.md` (build order step 1), `../specs/SPEC-graph-core.md`. Written 2026-09-29.

Step 1 ends when: the three spikes have answers recorded in `SPEC.md`; `ug stats` reproduces the
Evidence numbers on purge-squad; the plugin installs locally and `ug` runs from a hook; the
graph store passes its tests and performance targets.

## Order and dependencies

```
S1 runtime ─┐
S2 install ─┼─> T1 solution skeleton ─┬─> T2 stats ──> T3 stats report
S3 hooks ───┘                         ├─> T4 plugin-shell ──> T5 hook dispatcher
                                      └─> T6 graph-core store ──> T7 freshness + concurrency ──> T8 perf
```

S1–S3 can run in parallel. T2, T4 and T6 can run in parallel once T1 is in.

## Spikes (timeboxed; result is a written answer, code is thrown away)

### S1. .NET runtime inside Unity (≤ 0.5 day)
Question: can a framework-dependent `net8.0` console app run on the runtime shipped with Unity
2022.3 and Unity 6 (`<Editor>/Data/NetCoreRuntime`)? Which version is it, on Windows and macOS?
Does Roslyn workspace loading of Unity's generated `.csproj` work on it?
Done when: a table (Unity version × OS → works / doesn't, runtime version) is in `SPEC.md`
Decisions #2, and the `bin/` launcher strategy is chosen: bundled Unity runtime, installed
runtime, or self-contained publish.

### S2. Local plugin install (≤ 0.5 day)
Question: the exact `marketplace.json` / `plugin.json` layout and commands to install a plugin
from a local folder in the CLI and in the desktop app; how hooks find `bin/` (plugin root env var).
Done when: a hello-world plugin with one skill and one hook installs from `D:\Projects\unitygraph`,
the hook fires, and `SPEC.md` Commands is updated with the confirmed commands.

### S3. Hook interception (≤ 1 day)
Question: when a PreToolUse hook denies a symbol-shaped Grep and returns an answer as the reason,
does Claude use the answer or retry? Also: is the reason shown in full, and what's its size limit?
Method: a stub hook that returns a hand-written answer for 10 navigation questions taken from
purge-squad transcripts; run each with and without the hook; count calls, tokens and retries.
Done when: retry rate and token delta are recorded in `SPEC.md` Decisions #4, with the go/no-go
for interception on by default (threshold ~10% retries).

## Tasks

### T1. Solution skeleton
- `unitygraph.sln`; projects `UnityGraph.Cli`, `UnityGraph.Core`, `UnityGraph.Stats`,
  `UnityGraph.Hooks` and their `tests/*.Tests`; `Directory.Build.props` with .NET 8, C# 12,
  nullable, warnings as errors, invariant globalization; `.editorconfig`.
- `ug --version` prints the version.
Acceptance: `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` all pass on a
clean clone. CI workflow (GitHub Actions, Windows + macOS) runs the same three commands.

### T2. stats: transcript reader and categorizer
- Port `research/transcript-analysis/usage.js` (`categorize`) and the per-turn context sum from
  `turns.js` to `UnityGraph.Stats`: read `~/.claude/projects/<project-dir>/*.jsonl`, group by
  session, compute context tokens per turn (input + cache read + cache write), detect compaction
  (> 40% drop), attribute context to categories.
- Project directory derived from `--project <path>` the way Claude Code names it.
Acceptance: unit tests on small recorded `.jsonl` fixtures (one per category, one compaction).
On purge-squad (`--since` all), totals match the Evidence table within ±2 percentage points per
category and ±1% on total context tokens.

### T3. stats: report and before/after
- `ug stats --project <path> [--since <date>] [--until <date>] [--json]`: sessions, turns,
  context tokens, first-turn baseline, category table.
- `--baseline <file>` compares against a saved `--json` report; prints deltas.
- Output within ~40 lines; deterministic.
Acceptance: golden-file test for text and JSON output; baseline file for purge-squad saved at
`bench/baselines/purge-squad-2026-09.json` (not committed if it contains paths the user doesn't
want public; ask).

### T4. plugin-shell: plugin files and launcher
- `.claude-plugin/plugin.json`, `.claude-plugin/marketplace.json` (per S2), `skills/unity/SKILL.md`
  (stub, description ≤ 60 tokens), `bin/ug` and `bin/ug.cmd` launchers (per S1), config file
  `unitygraph.json` (per-module on/off switches, answer budget) with defaults in code.
- `ug doctor`: runtime found, Unity project detected, config valid, graph present.
Acceptance: local install works per S2; `ug doctor` passes on purge-squad; idle cost measured with
`ug stats` on a fresh session ≤ 300 tokens.

### T5. plugin-shell: hook dispatcher
- `hooks/hooks.json` routes every event to `ug hook <event>`; `ug hook` reads the JSON payload
  from stdin, dispatches to registered handlers, and fails open: any exception or > 2 s runtime
  → exit 0, no output, one line to `Library/unitygraph/hook-errors.log`.
- No handlers yet besides a no-op; each handler has its config switch.
Acceptance: golden tests feeding recorded hook payloads (PreToolUse Grep/Read/Edit, PostToolUse,
Stop); a throwing handler still exits 0 silently; cold `ug hook` start ≤ 150 ms on Windows
(measured; if over, note it for the step-3 hooks and consider a ReadyToRun build).

### T6. graph-core: store
- Implement `SPEC-graph-core.md`: schema v1, keys, kind validation, `ReplaceFile`, `RemoveFile`,
  `BulkWriter`, `GetNode`, `FindByName` (FTS5), `EdgesFrom`/`EdgesTo`, `unresolved` edges,
  version check and rebuild.
Acceptance: the Testing section's unit tests pass; ≥ 80% line coverage on `UnityGraph.Core`.

### T7. graph-core: freshness and concurrency
- `CheckFreshness` (size/mtime then SHA-256), write lock, `dirty.txt`, `graph.db.new` swap.
Acceptance: freshness and concurrency tests from the spec pass on Windows and macOS CI.

### T8. graph-core: performance
- Synthetic graph generator at purge-squad scale; benchmark test for every target in the spec.
Acceptance: all targets met in Release on the dev machine; CI runs them with a 2× margin.

## Checkpoint at the end of step 1

- ~~Review spike results~~ Done 2026-09-29: installed .NET 8+ required (S1); interception
  dropped from v1 and from `query`'s scope (S3).
- Write `SPEC-code-index.md`, `SPEC-asset-index.md`, `SPEC-editor-link.md` for step 2.

## Step 2 (and query, pulled forward)

Order changed 2026-09-29 (user): `query` comes right after `code-index`, before `asset-index`
and `editor-link`, so the graph is usable on C# as early as possible. Each module's spec is
written, committed and built without a review stop (user, 2026-09-29).

```
C1 spec ──> C2 loader ──> C3 nodes + Unity rules ──> C4 edges ──> C5 incremental + ug index ──> C6 purge-squad
                                                                                    └─> Q query (spec, ug find/refs/explain)
then asset-index, editor-link, unity-loop
```

### C1. code-index spec
`specs/SPEC-code-index.md`.

### C2. code-index: project loader
- Legacy `.csproj` reader, references (with runtime BCL fallback), compilation per project,
  project references as compilation references. Fixture `fixtures/CodeProject/`.
Acceptance: the fixture loads with 0 errors in own files; a missing reference is reported.

### C3. code-index: nodes and Unity rules
- Node rules, partial types, props; serialization table; messages and entry points.
Acceptance: serialization table test passes; golden node list for the fixture.

### C4. code-index: edges
- contains, inherits, implements, overrides, calls, uses_type, dispatches, script_defines.
Acceptance: golden edge list for the fixture; vendored files have declarations only.

### C5. code-index: incremental and `ug index`
- `CodeIndex.Full/Update`, lost-key dependents, csproj changes, new files by folder; CLI.
Acceptance: incremental tests pass; determinism test; ≥ 80% coverage on Code.

### C6. code-index: purge-squad measurement
- Acceptance test (env-gated, graph outside purge-squad). Fill in the spec's Measured section.
Acceptance: targets met, or the spec says why not.

### C6. code-index: purge-squad measurement / Q. query
Done; see `tasks/todo.md` and the specs' Measured sections.

## Asset-index (spec: `specs/SPEC-asset-index.md`)

```
A1 spec ──> A2 YAML + meta reader ──> A3 file indexer + fixture ──> A4 workspace, incremental, ug index ──> A5 query answers ──> A6 purge-squad
```

### A1. asset-index spec
`specs/SPEC-asset-index.md`; graph-core's `instance_of` row refined (script asset, not type).

### A2. asset-index: readers
- `UnityYaml` line reader and `MetaFile`; new project `src/UnityGraph.Assets`.
Acceptance: reader table test passes, including the unsupported-construct rows.

### A3. asset-index: file indexer
- Node and edge rules per content file and per `.meta`; fixture assets in `fixtures/CodeProject`.
Acceptance: golden asset rows for the fixture.

### A4. asset-index: workspace, incremental, `ug index`
- Directory walk, `AssetIndex.Full/Update`, shared `FileContribution`/`IndexSummary`, the
  CLI `GraphUpdate` coordinator used by `ug index` and queries.
Acceptance: incremental and determinism tests; ≥ 80% coverage on Assets.

### A5. query: asset answers
- Asset paths and GUIDs as symbols; `explain`/`refs` of assets; "on" and "unity event" lines
  for types and methods; `find` includes assets and game objects; skill update.
Acceptance: golden transcript extended; SPEC-query updated.

### A6. asset-index: purge-squad measurement
Acceptance: env-gated test passes; Measured section filled; targets met or explained.
