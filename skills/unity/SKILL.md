---
name: unity
description: Unity project questions and editor work. Use the ug CLI before grep or reading YAML.
---
Run `"${CLAUDE_PLUGIN_ROOT}/bin/ug" <cmd>` (`--project <dir>` elsewhere). Options: `ug <cmd> --help`, don't guess.

Graph (no Unity; one call beats grep; answers carry `file:line`, read only those):
`explain <symbol>` what it is, users, prefabs and scenes holding it; `refs <symbol>` what points at it; `impact <symbol>` what breaks if it changes; `outline <file>` members or object tree, no bodies; `find <text>` types, members, assets, objects (`--kind`); `index`, `doctor`, `stats`.
`<symbol>`: type, `Type.Member`, asset path, name or GUID, object name or path, a `cs:`/`a:`/`o:` key. `[exact]` is compiler-true, `[heuristic]` is a string match: verify. `ambiguous:` and `not found:` list candidates. Run `more` lines if needed.

Open editor (bridge; "Unity not reachable" answers are from Editor.log: check age):
- `status`, `compile` (after each script edit, not Editor.log), `bridge`
- code: `run <Type.Method> [args]` (static; `set`-style args; `--list`); `eval "<C#>"` any C# or `--file x.cs` (edits need `Undo.RecordObject`)
- read: `scene`, `tree`, `inspect`, `find --live`, `perf`
- edit: `object`, `component`, `set` (structs, lists, curves too), `event`, `prefab`, `camera` (live one, Cinemachine), `undo`, `redo`, `select`, `save`, `play`, `stop`, `pause`
- assets and project: `asset`, `project`, `package`
- loop: `console` (`--after <id>`: new only), `test`, `log` (last play), `play --for <s>`; Stop hook reports compile errors
- play-test (each prints what changed, `--shot` a picture): `ui` lists UI elements; `click <label|addr|x,y> [--value v]`, `nav <dir>`, `press <key>`, `stick left 1,0`, `tap|swipe x,y`, `wait <s>`, `play --step` (paused)
- look up: `shot [<obj> --angles]`, `api <Type>`, `docs <page>`
- `batch "a" "b"`: many in one call.
Addresses: `Enemy#2`, `Main:Level/Enemy`, `Assets/X.prefab:Child`; components `Object:Component`.

Rules:
- Closed scenes and prefabs: the graph. Live or unsaved values and every edit: the editor.
- Edits are Undo steps, unsaved until `ug save`; save only when files must be written. Prefab-asset edits, `asset` and `package` write at once and cannot be undone.
- Ask first: `bridge install --copy`, `package add|remove`, `asset delete`, `run`/`eval` of code the task doesn't need.
- Play-mode edits are lost on stop. `Packages/` and vendored folders are refused.
- UI work: read `${CLAUDE_PLUGIN_ROOT}/skills/unity/ui.md` first.
