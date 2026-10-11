---
name: unity
description: Unity project questions and editor work. Use the ug CLI before grep or reading YAML.
---
Run `"${CLAUDE_PLUGIN_ROOT}/bin/ug" <cmd>` (`--project <dir>` elsewhere). Options: `ug <cmd> --help`, don't guess.

Graph (no Unity; beats grep; answers carry `file:line`, read only those):
`explain <symbol>` what it is, users, scenes/prefabs holding it; `refs <symbol>` what points at it; `impact <symbol>` what a change breaks; `outline <file>` members/object tree, no bodies; `find <text>` types, members, assets, objects (`--kind`); `view <symbol>` HTML graph for people; `index`, `doctor`, `stats`.
`<symbol>`: type, `Type.Member`, asset path/name/GUID, object name/path, `cs:`/`a:`/`o:` key. `[exact]` is compiler-true, `[heuristic]` is a string match: verify. `ambiguous:`/`not found:` list candidates; `more` if needed.

Open editor (bridge; "Unity not reachable" answers are from Editor.log: check age):
- `status`, `compile` (after each script edit, not Editor.log), `bridge`
- code: `run <Type.Method> [args]` (static; `set`-style args; `--list`); `eval "<C#>"` or `--file x.cs` (edits: `Undo.RecordObject`)
- read: `scene`, `tree`, `inspect`, `find --live`, `perf` (`--record <s>`; `memory`, `snapshot`)
- edit: `object`, `component`, `set` (structs, lists, curves), `event`, `prefab` (`overrides`), `camera` (Cinemachine), `undo`, `redo`, `select`, `save`, `play`, `stop`, `pause`
- assets, project: `asset`, `project`, `package`
- loop: `console` (`--after <id>`: new only), `test`, `log` (last play), `play --for <s>`; Stop hook reports compile errors
- play-test (each prints changes, `--shot` a picture): `ui` lists elements; `click <label|addr|x,y> [--value v]`, `nav <dir>`, `press <key>`, `stick left 1,0`, `tap|swipe x,y`, `wait <s>`, `play --paused|--step|--pause-on-error`
- look up: `shot [<obj> --angles]`, `api <Type>`, `docs <page>`
- `batch "a" "b"`: many per call.
Addresses: `Enemy#2`, `Main:Level/Enemy`, `Assets/X.prefab:Child`; components `Object:Component`.

Rules:
- Closed scenes and prefabs: the graph. Live or unsaved values and every edit: the editor.
- Edits are Undo steps, unsaved until `ug save`; save only if files must be written. Prefab-asset edits, `asset`, `package` write at once, no undo.
- Ask first: `bridge install --copy`, `package add|remove`, `asset delete`, `run`/`eval` of code the task doesn't need.
- Play-mode edits die on stop. `Packages/`, vendored folders: refused.
- UI work: read `${CLAUDE_PLUGIN_ROOT}/skills/unity/ui.md` first.
