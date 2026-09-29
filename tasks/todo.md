# Todo: step 1

Details and acceptance criteria: `plan.md`.

## Spikes
- [x] S1 .NET runtime inside Unity 2022.3 / Unity 6 (Windows; macOS not tested): bundled runtime
  is 6.0.21, not usable for net8.0; Roslyn over Unity csproj works without MSBuild
- [x] S1 follow-up: launcher strategy decided: installed .NET 8+, install hint, fail open
- [x] S2 local plugin install commands confirmed (SPEC.md Commands; skill load by the model not
  checked: CLI login expired, hook firing confirmed)
- [x] S3 hook interception: retry rate and token delta measured (9/10 retried, +27% context
  tokens, +62% calls; no-go, off by default; subagents, model not told about unitygraph)

## Build
- [x] T1 solution skeleton + CI (Core/Stats/Hooks test projects come with their first test in
  T6/T2/T5: MTP fails a test project with zero tests; CI not run yet, no GitHub remote)
- [x] T2 stats: transcript reader and categorizer (matches Evidence ±2 pp; exact match with the
  prototype at `--until 2026-09-28T11:00Z`, the Evidence snapshot; acceptance test runs when
  UG_PURGE_SQUAD_TRANSCRIPTS is set)
- [x] T3 stats: report, JSON, baseline compare (golden files instead of Verify: Verify 33 needs
  a license declaration to build; baseline committed with the project name only)
- [x] T4 plugin-shell: plugin files, launcher, config, `ug doctor`
  - [x] plugin files, `bin/ug` + `bin/ug.cmd`, config, `ug doctor` (passes on purge-squad:
    Unity 6000.3.14f1, Force Text, default config, graph warning; launchers skip Unity's 6.0.21)
  - [x] local install per S2 (run by the user from the repo; `claude plugin details`: 1 skill,
    0 hooks, always-on ~40 tokens, on-invoke ~110)
  - [x] idle cost ≤ 300 tokens: measured offline (headless runs failed OAuth, so no API usage;
    diffed the prompt payload recorded in the transcripts, with and without `--plugin-dir`).
    The plugin adds only the skill listing line, 154 chars (~40 tokens, as `plugin details`
    says). With many skills installed the listing is capped: ours pushed two other skills'
    descriptions out, net +40 chars (~10 tokens). Re-measure with real usage once logged in.
- [x] T5 plugin-shell: hook dispatcher, fail-open (hooks.json wires PreToolUse/PostToolUse/Stop
  with narrow matchers, not every event; no production handlers yet. Payload fixtures follow
  the documented shape, not captured from a live session: re-record once `claude -p` can log
  in. Cold `ug hook` ~115 ms on Windows, under the 150 ms target, after taking the subshells
  out of `bin/ug`)
- [x] T6 graph-core: store (Microsoft.Data.Sqlite 10.0.12 + bundled e_sqlite3 with FTS5;
  `dist/ug` keeps win/osx/linux x64+arm64 natives, 10.9 MB total; 18 store tests)
  - [x] ≥ 80% line coverage on Core: 89.6% (Microsoft.Testing.Extensions.CodeCoverage 18.11.2)
- [x] T7 graph-core: freshness + concurrency (23 tests, 10 repeated runs clean on Windows;
  macOS not run yet: no Mac and no CI remote. Full index publishes via SQLite backup, not a
  rename; see SPEC-graph-core Concurrency)
- [x] T8 graph-core: performance targets (all met in Release on the dev machine; size the
  tightest at 49.3 of 50 MB; Debug and CI run with a 2x margin; table in SPEC-graph-core)

## Checkpoint
- [x] Spike results recorded in SPEC.md
- [ ] Step-2 module specs written (code-index, asset-index, editor-link)
  - [x] code-index (`specs/SPEC-code-index.md`)
  - [x] asset-index (`specs/SPEC-asset-index.md`)

# Todo: step 2 (query pulled forward, see plan.md)

- [x] C1 code-index spec
- [x] C2 code-index: project loader
- [x] C3 code-index: nodes and Unity rules
- [x] C4 code-index: edges
- [x] C5 code-index: incremental and `ug index` (Code line coverage 92.8%)
- [x] C6 code-index: purge-squad measurement (one-file update 1.21 s vs 1 s target: Roslyn cold start, see spec)
- [x] Q query: spec, `ug find/refs/explain`, skill update (p95 345 ms on purge-squad; Query coverage 94.7%)
- [x] A1 asset-index spec
- [x] A2 asset-index: YAML and meta readers
- [x] A3 asset-index: file indexer and fixture assets
- [ ] A4 asset-index: workspace, incremental, `ug index`
- [ ] A5 query: asset answers
- [ ] A6 asset-index: purge-squad measurement
