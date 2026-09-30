---
name: unity
description: Unity project questions and editor work (scripts, scenes, prefabs, assets, serialized fields, compile, play mode, scene edits). Use the ug CLI before grep or reading YAML.
---

# unitygraph

Run `ug` through the plugin launcher: `"${CLAUDE_PLUGIN_ROOT}/bin/ug" <command>`. Every command
takes `--project <dir>` when the shell isn't inside the Unity project.

Ask the graph first: one `ug` call replaces a grep-and-read streak. Answers carry `file:line`
anchors; read only the lines you need after that.

| Command | Answers |
| --- | --- |
| `ug explain <symbol>` | What it is: location, base types, Unity kind and script GUID, engine messages, entry points, serialized fields, who uses or calls it, which prefabs and scenes it sits on. For an asset: its objects, scripts, prefabs, what it uses, who uses it, UnityEvents, Addressables. `--members` lists members. |
| `ug refs <symbol>` | Everything pointing at it, with `file:line`: callers, string dispatch (`Invoke`, `SendMessage`), overrides, subclasses, implementations, type uses, components using a script, UnityEvent wiring, prefab instances, variants, serialized references and prefab overrides. |
| `ug find <text>` | Types, members, assets, game objects and prefab instances whose name starts with the words; `--kind type\|method\|field\|property\|event\|asset\|game_object\|prefab_instance`. |
| `ug index` | Build or update the code and asset graph (queries do this themselves). |
| `ug doctor` | Is the plugin working here: runtime, Unity project, config, graph. |
| `ug stats` | Where this project's context tokens went, by tool category. |
| `ug status` | The open editor: Unity version, idle or compiling, last compile's error count. |
| `ug compile` | Refresh, wait for the script compile, list errors as `file:line:col CODE text` (`--warnings` adds warnings). Without the editor it reads Editor.log and says how old that answer is. |
| `ug run <target>` | Run a static editor method without parameters (`Namespace.Type.Method`) or a menu item (`Tools/Build`) in the open editor; prints its result or exception. |
| `ug bridge install` | Print how to add the editor bridge that `status`, `compile` and `run` talk to. |

`<symbol>`: `LabStage`, `LabStage.Open`, `Game.Lab.LabStage.Open(int)`, an asset path
(`Assets/Prefabs/Turntable.prefab`), file name (`Turntable.prefab`) or GUID, a game object or
prefab instance by name or hierarchy path (`Spawn`, `Level/Spawn`; code symbols win a shared
name), or a `cs:`, `a:` or `o:` key from an answer. Overloads without parameters are answered
together.

Reading answers:
- `[exact]` came from the compiler; `[heuristic]` is a string match (`Invoke("Name")`); check it
  before relying on it.
- `ambiguous:` lists candidates and the command for the first; `not found:` lists near names.
- A `more` line means the answer was cut: run it only if you need the rest.
- Scenes, prefabs, ScriptableObjects and materials are in the graph: ask it before reading YAML.
- A `unity event` matched only by method name (no parameter types in the YAML) is `[heuristic]`.
- Calls into UnityEngine or packages are not in the graph; `refs MonoBehaviour` finds nothing.

Editor commands:
- After editing scripts, run `ug compile` instead of reading Editor.log or asking the user to
  paste errors. `no script changes` means Unity saw nothing new to compile.
- `Unity not reachable (...)` answers come from Editor.log or `compile.json`; check the time in
  the header before trusting them.
- The bridge writes into the user's project: ask before `ug bridge install --copy` or adding the
  package through Package Manager. `ug bridge remove` undoes `--copy`.
- `ug run` executes code in the user's editor: run only what the user asked for or what the
  task clearly needs.

Editor ops (the open editor, through the bridge; `ug <command> --help` for options):

| Command | Does |
| --- | --- |
| `ug scene` / `ug scene open\|new\|close\|active <path>` | Loaded scenes and play state; open (`--additive`), create, close, set active. |
| `ug save [path]` | Save modified scenes and dirty assets. Nothing else writes them. |
| `ug play` / `ug stop` / `ug pause` | Enter play mode and wait for it; stop reports time played and errors logged. |
| `ug tree [object] [--depth n]` | Live hierarchy with components. |
| `ug inspect <object> [--component T] [--importer]` | Live components or one component's serialized values. |
| `ug find --live <name>` | Live objects by name, `--tag`, `--layer`, `--component`, as addresses. |
| `ug object create\|delete\|rename\|move\|active\|duplicate` | Scene objects: `create Spawn --parent Level --prefab Assets/X.prefab`, `move A --parent B` or `--root`. |
| `ug component add\|remove <object> <type>` | Components. |
| `ug set <object> <field> <value> [--component T] [--importer]` | One serialized field (`speed 7.5`, `m_LocalPosition 1,2,3`, references by address or asset path). |
| `ug undo` / `ug redo` / `ug select [object]` | Unity's Undo and Redo; the editor's selection. |
| `ug prefab create <object> <Assets/X.prefab>` / `apply` / `revert` / `unpack [--completely]` | Save an object as a prefab and keep it linked; write an instance's overrides into its prefab (`apply` writes the asset at once); throw them away; cut the link. `apply`, `revert` and `unpack` take the instance root. A new instance is `ug object create --prefab`. |
| `ug asset folder <path>` / `create <path> <type> [--shader S]` / `move <from> <to>` / `rename <path> <name>` / `duplicate <path> [to]` / `delete <path>` / `reimport <path>` | Asset-database edits: folders, materials and ScriptableObjects (by type name), GUID-keeping move and rename, copies, delete to the OS trash, reimport. They write at once and are **not undoable**; existing targets, `Packages/`, vendored folders, `Assets/` itself and open scenes are refused. Material properties and importer settings go through `ug set` (`--importer`). |

- `<object>`: `Level/Enemy`, `Enemy#2` for the second of that name, `Main:Level/Enemy` for one
  scene, `Assets/Prefabs/Turret.prefab:Barrel` inside a prefab asset.
- Closed scenes and prefabs: ask the graph (`ug explain`, `ug refs`, `ug find`), not the editor.
  Use the editor for live state (unsaved or play-mode values) and for edits.
- Edits go through Undo and stay unsaved (`(Main.unity modified, not saved)`) until `ug save`;
  run `ug save` only when the task needs files written. Prefab-asset edits are written at once.
- Edits in play mode are lost on stop. Vendored folders and `Packages/` are refused.
