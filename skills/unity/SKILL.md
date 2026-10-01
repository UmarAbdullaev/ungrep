---
name: unity
description: Unity project questions and editor work. Use the ug CLI before grep or reading YAML.
---
Run `"${CLAUDE_PLUGIN_ROOT}/bin/ug" <cmd>` (`--project <dir>` outside the project). Options are in `ug <cmd> --help`: read it, don't guess.

Graph (no Unity needed; one call beats grep and reads, answers carry `file:line`, read only those lines):
`explain <symbol>` what it is, users, prefabs and scenes holding it; `refs <symbol>` everything pointing at it; `find <text>` types, members, assets, objects (`--kind`); `index`, `doctor`, `stats`.
`<symbol>`: type, `Type.Member`, asset path, name or GUID, object name or path, or a `cs:`/`a:`/`o:` key. `[exact]` is compiler-true, `[heuristic]` is a string match: verify. `ambiguous:` and `not found:` list candidates. Run a `more` line only if needed.

Open editor (bridge; "Unity not reachable" answers come from Editor.log, check their age):
- `status`, `compile` (after every script edit, instead of reading Editor.log), `run`, `bridge`
- read: `scene`, `tree`, `inspect`, `find --live`, `perf`
- edit: `object`, `component`, `set` (also structs, lists, curves, gradients), `event`, `prefab`, `undo`, `redo`, `select`, `save`, `play`, `stop`, `pause`
- assets and project: `asset`, `project`, `package`
- loop: `console` (`--after <id>` for new only), `test` (`--mode`, `--filter`), `log` (last play session), `play --for <s>` (play, stop, log); a Stop hook reports compile errors
- play-test (play mode): `ui` lists clickable uGUI elements, `click <label|addr|x,y> [--value v]`
- look up: `shot`, `api <Type>`, `docs <page>`
- `batch "step" "step"` runs many of these in one call.
Address objects as `Level/Enemy`, `Enemy#2`, `Main:Level/Enemy`, `Assets/X.prefab:Child`; a component as `Object:Component`.

Rules:
- Closed scenes and prefabs: the graph. Live or unsaved values and every edit: the editor.
- Edits are Undo steps, unsaved until `ug save`; save only when the task needs files written. Prefab-asset edits, `asset` and `package` write at once and cannot be undone.
- Ask first: `bridge install --copy`, `package add|remove`, `asset delete`, `run` of code the task doesn't need.
- Play-mode edits are lost on stop. `Packages/` and vendored folders are refused.
- UI work: read `${CLAUDE_PLUGIN_ROOT}/skills/unity/ui.md` first.
