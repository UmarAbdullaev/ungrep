# P4: V1 viewer (`ug view`)

Repo: `D:\Projects\unitygraph`. Read first: `specs/SPEC-viewer.md` (decided by the user on
2026-10-01), SPEC.md (viewer module, Decision 5, Tech stack: Cytoscape.js vendored and inlined),
`specs/SPEC-query.md`, `tasks/todo.md`, your memory files. Do the V1 line only, then stop and
report; commit O8 style with no attribution trailer; do not commit `tasks/*-prompt.md`.

Build:
1. Project `src/UnityGraph.Viewer` (+ `tests/UnityGraph.Viewer.Tests`), `ug view` in the CLI,
   `viewer` module in config (already listed, default on).
2. Neighbourhood query over the existing graph (depth 1-4, `--kind`), exact vs heuristic edges,
   reference counts for node size, the 150-node cap with a "+N more" node.
3. The HTML page per the spec: dagre for depth <= 3, fCoSE above; palette, shapes, legend,
   dark mode; side panel with `file:line` and the `ug explain` command; double-click expands
   neighbours already baked in; search, kind checkboxes, depth slider, heuristic toggle.
4. Vendor Cytoscape.js and its layout plugins (dagre, fCoSE and their dependencies) with licence
   notices. Vendoring is a one-time download: ask the user before any network fetch and give the
   package names, versions and sizes first; if npm is unavailable, ask.
5. Tests per the spec's Testing section; add the command to `skills/unity/SKILL.md` as a human
   tool ("tell the user the path; do not read the file").

Rules: no network at view time; the page makes no requests; reads the graph only; LF files;
TreatWarningsAsErrors; `dotnet format` clean; ask before anything the spec does not cover.
Chat replies terse; end each status with x/y tasks done or %.
