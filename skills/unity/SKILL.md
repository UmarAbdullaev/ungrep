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
| `ug run <target>` | Run a static editor method without parameters (`Namespace.Type.Method`) or a menu item (`Tools/Build`) in the open editor; prints its result or exception. Menu items that quit the editor (`File/Quit`, `File/Exit`) or build a player (`File/Build...`) are refused. |
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
| `ug set <object> <field> <value> [--component T] [--importer]` | One serialized field (`speed 7.5`, `m_LocalPosition 1,2,3`, `#ff8800`, references by address or asset path; a component as `Object:Component`, like `Level/Enemy#1:Transform`). |
| `ug event list [object]` / `add <object> <event> <target> <method> [--arg V] [--component T]` / `remove <object> <event> [--index N]` | Persistent calls of a UnityEvent: `ug event add Canvas/Button onClick Canvas:UiHandler OnPlay`. `target` is `Object:Component` or an object (the component with the method; `SetActive` for the object itself); a method with one argument needs `--arg`, or takes the event's own value. One Undo group each. |
| `ug undo` / `ug redo` / `ug select [object]` | Unity's Undo and Redo; the editor's selection. |
| `ug prefab create <object> <Assets/X.prefab>` / `apply` / `revert` / `unpack [--completely]` | Save an object as a prefab and keep it linked; write an instance's overrides into its prefab (`apply` writes the asset at once); throw them away; cut the link. `apply`, `revert` and `unpack` take the instance root. A new instance is `ug object create --prefab`. |
| `ug asset folder <path>` / `create <path> <type> [--shader S]` / `move <from> <to>` / `rename <path> <name>` / `duplicate <path> [to]` / `delete <path>` / `reimport <path>` | Asset-database edits: folders, materials and ScriptableObjects (by type name), GUID-keeping move and rename, copies, delete to the OS trash, reimport. They write at once and are **not undoable**; existing targets, `Packages/`, vendored folders, `Assets/` itself and open scenes are refused. Material properties and importer settings go through `ug set` (`--importer`). |
| `ug project` | Unity version, render pipeline, tags, layers, sorting layers and build scenes (read-only; change them with `ug set` on `ProjectSettings/*.asset`). |
| `ug package list` / `add <id>` / `remove <name>` | Unity's Package Manager: direct dependencies with resolved versions; add a package by name, `name@version`, git URL or `file:` path; remove one. **Only when the user asked**: Unity writes `Packages/manifest.json`, an add from a registry or git downloads, and both may recompile (`ug compile` waits). Not undoable. `--timeout` seconds (default 300). The bridge package itself is refused. |
| `ug shot [game\|scene\|<camera>] [--width W] [--height H]` | Save a PNG of the Game view (all enabled cameras), the Scene view or one camera (default 960x540, 64 to 2048) to `Library/unitygraph/shots/` and print its path and size; open the file only when the picture matters. Overlay canvases are drawn; UI Toolkit documents are not (the answer says so). Refused when Unity runs without a graphics device (`-nographics`). |
| `ug api <Type>` / `<Type> <member>` / `<Type.member>` | Unity and .NET API from the loaded assemblies by reflection: a type's kind, base, constructors and member names in about 200 tokens, or a member's signatures, overloads and defaults. Unknown types and pages fall back to the local Scripting Reference, then docs.unity3d.com (text only, cached). |
| `ug docs <page>` | A documentation page as trimmed text: `Manual/Prefabs`, `ScriptReference/Physics.Raycast`, `com.unity.cinemachine/manual/index`. Local copy, then cache, then docs.unity3d.com (`docs.online: false` in `.claude/unitygraph.json` turns the network off). |
| `ug perf` | Play state, frame render stats (draw calls, batches, triangles: play mode only), memory, object and asset counts. Read-only. |
| `ug batch "<step>" ... [--file <path>\|-]` | Several editor commands in one call over one connection; stops at the first failure and says which steps did not run. Steps are `scene`, `object`, `set`, ... (not `find`, `run`, `compile`); `--file` reads one step per line (`#` comments), `-` reads stdin. Each step is its own undo group. |


- `<object>`: `Level/Enemy`, `Enemy#2` for the second of that name, `Main:Level/Enemy` for one
  scene, `Assets/Prefabs/Turret.prefab:Barrel` inside a prefab asset.
- Closed scenes and prefabs: ask the graph (`ug explain`, `ug refs`, `ug find`), not the editor.
  Use the editor for live state (unsaved or play-mode values) and for edits.
- Edits go through Undo and stay unsaved (`(Main.unity modified, not saved)`) until `ug save`;
  run `ug save` only when the task needs files written. Prefab-asset edits are written at once.
- Edits in play mode are lost on stop. Vendored folders and `Packages/` are refused.

Building UI (a uGUI screen is about a dozen calls with `ug batch`):
- Make widgets with their menu items, which add the Canvas and EventSystem too: `ug run "GameObject/UI (Canvas)/Button - TextMeshPro"` (also `Text - TextMeshPro`, `Image`, `Panel`). First use may need `Window/TextMeshPro/Import TMP Essential Resources` through `ug run`.
- Lay out and style in one `ug batch`: `set Canvas/Button anchorMin 0.5,0.5`, `anchorMax`, `pivot`, `sizeDelta 300,76`, `anchoredPosition 0,-40`, `color #2EB89E --component Image`, `fontSize 32`, `horizontalAlignment Center`. Children draw in sibling order: `ug object move Canvas/Backdrop --index 0` puts a background behind.
- Wire a click with `ug event add Canvas/Button onClick Canvas:UiHandler OnPlay` and check it with `ug event list Canvas/Button`.
- Check the look with `ug shot game` and open the PNG: overlay canvases are drawn. UI Toolkit (UXML/USS files plus a UIDocument) is not drawn by `ug shot`; the answer says `UI Toolkit document not drawn`, so check it in play mode in the editor or by reading the files.
- Looks good with little effort: one dark background colour, one accent colour for buttons with dark text on it, a title at least twice the button font size, 300 by 76 buttons centred with space between, round numbers for sizes. For UI Toolkit use USS (`border-radius`, `:hover`, `:active`) instead of inline styles.
