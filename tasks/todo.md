# Todo: step 1

Details and acceptance criteria: `plan.md`.

## Spikes
- [x] S1 .NET runtime inside Unity 2022.3 / Unity 6 (Windows; macOS not tested): bundled runtime
  is 6.0.21, not usable for net8.0; Roslyn over Unity csproj works without MSBuild
- [ ] S1 follow-up: launcher strategy decided (installed runtime vs self-contained)
- [x] S2 local plugin install commands confirmed (SPEC.md Commands; skill load by the model not
  checked: CLI login expired, hook firing confirmed)
- [x] S3 hook interception: retry rate and token delta measured (9/10 retried, +27% context
  tokens, +62% calls; no-go, off by default; subagents, model not told about unitygraph)

## Build
- [ ] T1 solution skeleton + CI
- [ ] T2 stats: transcript reader and categorizer (matches Evidence ±2 pp)
- [ ] T3 stats: report, JSON, baseline compare
- [ ] T4 plugin-shell: plugin files, launcher, config, `ug doctor`
- [ ] T5 plugin-shell: hook dispatcher, fail-open
- [ ] T6 graph-core: store
- [ ] T7 graph-core: freshness + concurrency
- [ ] T8 graph-core: performance targets

## Checkpoint
- [x] Spike results recorded in SPEC.md
- [ ] Step-2 module specs written (code-index, asset-index, editor-link)
