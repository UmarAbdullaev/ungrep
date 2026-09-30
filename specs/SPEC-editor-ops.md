# Spec: editor-ops

Part of unitygraph (see `../SPEC.md`). Status: **draft** (2026-09-30). Builds on `editor-link`
(bridge, client, discovery). Requested by the user on 2026-09-30 so that `ug` covers what people
reach for a Unity MCP server to do, and unitygraph can be the one plugin a Unity developer needs.

## Objective

Let Claude work in the open editor, not only read the project: see and switch the open scenes,
enter and leave play mode, read the live hierarchy and component values (including unsaved and
play-mode state the YAML graph cannot see), and make scene and asset edits: create, delete,
rename, reparent and toggle objects, add and remove components, set serialized fields.

Why through `ug` and the bridge rather than a Unity MCP server: an MCP server puts every tool's
schema in the context of every session, used or not; `ug` costs one skill line when idle and a
few lines per answer. Measuring that difference on purge-squad is part of this module (O10), and
is the measured reason SPEC Boundaries ask for before an MCP-shaped feature.

## Scope

In:

- Bridge endpoints (package version 0.2.0 and up) for scenes, play mode, reading objects and
  editing, prefabs, assets, project data, screenshots and API lookup.
- CLI: `ug scene`, `ug scene open|save|new|close|active`, `ug play|stop|pause`, `ug tree`,
  `ug inspect`, `ug find --live`, `ug object create|delete|rename|move|active|duplicate`,
  `ug component add|remove`, `ug set`, `ug undo|redo`, `ug select`, `ug prefab ...`,
  `ug asset ...`, `ug project`, `ug package list|add|remove`, `ug shot`, `ug api`, `ug docs`,
  `ug perf`, menu items through `ug run`, and editor commands inside `ug batch`.
- The `unity` skill: the commands and when to use them.
- BridgeHost fixture scenes, prefab and ScriptableObject; EditMode tests; a `UG_UNITY` end to end.

Out: hooks that edit anything (SPEC Boundaries: never from a hook); building players; animation,
timeline and shader graph editing; ProBuilder and Cinemachine (ask when wanted); script editing
(Claude's own Write and Edit do it, then `ug compile`); editing package contents in `Packages/`
or vendored folders; Unity's own CLI adapter.

## Unity MCP parity

Requested 2026-09-30: cover everything the Unity MCP server ("MCP for Unity") offers, and more.

| MCP tool | In unitygraph |
| --- | --- |
| `refresh_unity`, `validate_script` | `ug compile` (editor-link) |
| `read_console`, `run_tests`, `get_test_job` | `ug console`, `ug test` (unity-loop) |
| `create_script`, `script_apply_edits`, `apply_text_edits`, `delete_script`, `get_sha`, `find_in_file` | Claude's Write, Edit, Grep; `ug compile` after. Not duplicated. |
| `find_gameobjects` | `ug find` (graph, editor closed) and `ug find --live` (name, tag, layer, component) |
| `manage_scene` | `ug scene` and its subcommands; `ug object move --scene` |
| `manage_gameobject`, `manage_components` | `ug object`, `ug component`, `ug set` |
| `manage_editor` | `ug play|stop|pause`, `ug undo|redo`, `ug select`, `ug project` (tags, layers) |
| `execute_menu_item` | `ug run <menu path>` (editor-link's runner already calls `ExecuteMenuItem`) |
| `manage_prefabs` | `ug prefab create|instantiate|apply|revert|unpack`, prefab-content addresses |
| `manage_asset`, `manage_material`, `manage_texture` | `ug asset folder|create|move|rename|duplicate|delete|reimport`; `ug set` on materials and importers |
| `manage_packages` | `ug package list|add|remove` through Unity's Package Manager API |
| `manage_camera` (screenshot) | `ug shot game|scene|<camera>`: a PNG Claude reads only when needed |
| `manage_graphics`, `manage_physics`, `manage_profiler` | `ug perf` (render, memory, profiler counters; `ug stats` is the token stats); settings through `ug set` on `ProjectSettings/*.asset` |
| `unity_reflect`, `unity_docs` | `ug api <Type[.Member]>`, `ug docs <page>`: reflection, then the offline Scripting Reference, then docs.unity3d.com (trimmed, cached) |
| `batch_execute` | `ug batch` with editor commands, one process and one connection |
| `set_active_instance`, `execute_custom_tool` | One editor per project root; `ug run` for project editor methods |

Beyond the MCP server: the code and asset graph (answers with Unity closed), the compile gate,
guards on serialized-field renames and `.meta` safety, token stats, and no idle context cost.

## Addressing objects

Every command that takes an `<object>` accepts:

| Form | Means |
| --- | --- |
| `Player/Model/Hand` | A game object in the loaded scenes, by names from the root. A leading `/` is allowed. |
| `Enemy#2` (any segment) | The second sibling named `Enemy` (1-based, hierarchy order). Plain `Enemy` among several is ambiguous. |
| `Main:Player/Model` | Only in the loaded scene named `Main` (file name without `.unity`), for names found in several scenes. |
| `Assets/Prefabs/Turret.prefab` | A prefab asset's root; `Assets/Prefabs/Turret.prefab:Barrel/Muzzle` an object inside it. |
| `Assets/Data/Wave1.asset` | A ScriptableObject or other asset's main object (for `inspect` and `set`). |

Ambiguous or missing names answer like the graph does: `ambiguous: Enemy (3 in Main)` with the
candidates as addresses, or `not found: Playr` with near names. Components are named by type
(`Rigidbody`, `PlayerController`, full name when two types share a name), `#n` for the n-th of
the same type on one object.

## Endpoints

All `GET` with query parameters, token and origin checks as in editor-link, answered on the main
thread. Every answer is status 200 with `{"lines":[...]}`, the lines exactly as `ug` prints
them (formatted in the bridge, so the client stays thin and the tests see the real output), or
`{"error":"..."}` for a refusal. An answer may add `"wait":"playing"` or `"wait":"idle"`: the
client then polls `/status` until that state (across the domain reload) before printing, and
appends the time taken (`playing (entered in 2.1 s)`).

| Request | Does |
| --- | --- |
| `/scene` | Loaded scenes: path, active, modified, loaded, root count; play state. |
| `/scene/open?path=&additive=` | Opens a scene. Refused while playing, and (without `additive`) while any loaded scene is modified: the answer names them. |
| `/save?path=` | Saves modified loaded scenes (or the one given) and dirty assets. The only request that writes scene or asset files. |
| `/play`, `/stop`, `/pause` | Sets `EditorApplication.isPlaying`/`isPaused`. `/play` is refused while compile errors exist. Answers at once; the client waits for the state (domain reloads handled as in the compile flow). `/stop` answers the play session's time and error/warning counts. |
| `/tree?object=&depth=` | Children of an object (or the roots of the loaded scenes) to `depth` (default 2): name, active, components, child count past the depth. At most 200 lines. |
| `/inspect?object=&component=` | Components of an object, or one component's visible serialized fields: name, type, value (object references as addresses or asset paths; arrays with length and the first 10 items). At most 150 lines. |
| `/object/create?name=&parent=&prefab=&primitive=` | New empty object, primitive, or a prefab instance kept linked to its prefab. |
| `/object/delete?object=` | Deletes an object and its children. |
| `/object/rename?object=&name=` | |
| `/object/move?object=&parent=&index=` | Reparents (empty `parent`: scene root), keeping the world position. |
| `/object/active?object=&value=` | `SetActive`. |
| `/component/add?object=&type=` | Refused for types that are not components or not found. |
| `/component/remove?object=&component=` | Refused when another component requires it. |
| `/set?object=&component=&field=&value=` | Sets one serialized field through `SerializedObject`. |

Added for parity (O6–O9; same rules, same answer shape):

| Request | Does |
| --- | --- |
| `/find?name=&tag=&layer=&component=` | Live objects matching all given filters, as addresses. At most 100. |
| `/object/duplicate?object=`, `/object/move?...&scene=` | Duplicate next to the original; move a root object to another loaded scene. |
| `/scene/new?path=&additive=`, `/scene/close?path=`, `/scene/active?path=` | New scene saved at `path`; close (refused when modified); set the active scene. |
| `/undo`, `/redo`, `/select?object=` | Unity's Undo/Redo (answers the group name); selection read or set. |
| `/prefab/create?object=&path=`, `/prefab/apply?object=`, `/prefab/revert?object=`, `/prefab/unpack?object=&completely=` | Save an object as a prefab (kept linked); apply or revert an instance's overrides; unpack. |
| `/asset/folder`, `/asset/create?path=&type=`, `/asset/move?from=&to=`, `/asset/duplicate`, `/asset/delete?path=`, `/asset/reimport?path=` | Through `AssetDatabase`, so `.meta` files and GUIDs stay right. `create` makes folders, materials (`type=Material&shader=`) and ScriptableObjects. `delete` moves to the OS trash (`MoveAssetToTrash`). |
| `/set?object=Assets/X.png&importer=true&...` | Importer settings (texture, model, audio) through the importer's `SerializedObject`, then reimport. Material properties by name (`_BaseColor`, `_MainTex`). |
| `/project` | Tags, sorting and physics layers, build scenes, Unity version, render pipeline. |
| `/package/list`, `/package/add?id=`, `/package/remove?name=` | `UnityEditor.PackageManager.Client`; waits for the request; answers the resolved version. |
| `/run?target=<menu path>` | Menu items through editor-link's `/run`, which already calls `EditorApplication.ExecuteMenuItem`; now refused for File/Quit, File/Exit and Build menu items. |
| `/shot?view=game|scene|<camera address>&width=&height=` | Renders to `Library/unitygraph/shots/<name>.png`; answers the path and size. |
| `/api?type=&member=` | Types, members and signatures by reflection over the loaded assemblies. |
| `/stats` (`ug perf`) | Render stats (in play mode), memory, object and asset counts. |

`field` is the serialized name (`speed`, `m_LocalPosition`, `waves[2].count`); display names
(`Speed`) also match. `value` by the field's type: numbers (invariant culture), `true`/`false`,
strings, enum names, vectors `1,2,3`, colors `#ff8800` or `1,0.5,0,1`, object references as an
object address, asset path or `null`, array size via `field.size`.

## Safety rules

1. **Undo.** Every edit goes through Unity's Undo (`RegisterCreatedObjectUndo`,
   `DestroyObjectImmediate`, `SetTransformParent`, `AddComponent`, `ApplyModifiedProperties`),
   one undo group per request named `ug <command>`, so Ctrl+Z in the editor reverts it.
2. **No saving unasked.** Scene edits mark the scene modified and stay unsaved until `ug save`.
   ScriptableObject edits mark the asset dirty, saved by `ug save` too.
3. **Prefab assets** have no unsaved state outside prefab mode. Edits to
   `Assets/X.prefab:...` load the prefab contents, apply the edit and write the asset at once
   (`PrefabUtility.LoadPrefabContents` / `SaveAsPrefabAsset`); the answer says `written
   Assets/X.prefab`. Version control is their undo. Opening prefab mode in the user's editor was
   rejected: it changes what the user sees.
4. **Never discard.** `scene open` never closes a modified scene; nothing reverts the user's
   unsaved work.
5. **Play mode.** Scene edits while playing work but are lost when play stops; answers say so.
6. **Busy editor.** Requests are refused while compiling or importing (`Unity is compiling`).
7. **Read-only places.** Objects in `Packages/` or in vendored folders are refused for edits.
8. **Hooks never call these** (SPEC Boundaries).
9. **Assets through AssetDatabase.** Asset moves, renames, copies and deletes go through
   `AssetDatabase` so `.meta` files and GUIDs follow; deletes go to the OS trash, never gone.
10. **Packages.** `ug package add|remove` only on an explicit command, through Unity's Package
    Manager (Unity writes `Packages/manifest.json`, not ug). Added 2026-09-30 by the user.
11. **API lookup, online last.** `ug api` answers from reflection (signatures), then the local
    Scripting Reference when the Hub's Documentation module is installed, then docs.unity3d.com
    (Scripting Reference, Manual, package docs) when neither answers. The request carries only
    the type, member or page name, never code or project data; pages are trimmed to signature,
    summary, parameters and one example and cached under `%LOCALAPPDATA%/unitygraph/docs`
    (`~/Library/Caches/unitygraph/docs` on macOS); config `docs.online: false` turns the network
    step off, and a failed fetch says so and fails open. Approved by the user 2026-09-30.

## Commands

```
$ ug scene
Assets/Scenes/Main.unity  active  modified  12 roots
Assets/Scenes/UI.unity    loaded             3 roots
edit mode

$ ug tree Player --depth 1
Player  Rigidbody, PlayerController
  Model  Animator  (4 children)
  Gun  Gun

$ ug inspect Player --component PlayerController
Player  PlayerController  Assets/Scripts/Player/PlayerController.cs
  speed  float  5
  target  Transform  Main:Enemy#2
  guns  Gun[2]  Player/Gun, null

$ ug set Player speed 7.5 --component PlayerController
Player  PlayerController.speed  5 -> 7.5  (Main.unity modified, not saved)

$ ug object create Spawn --parent Level --prefab Assets/Prefabs/Spawner.prefab
created Level/Spawn (Spawner prefab instance)  (Main.unity modified, not saved)

$ ug play
playing (entered in 2.1 s)

$ ug stop
stopped after 12.4 s: 0 errors, 2 warnings (ug console)
```

- Answers are one line per change or per object, with the address to use next. `ug tree` leaves
  out the plain Transform every object has; `ug inspect <object>` shows it on one line.
- Exit code 0 for any answer, 1 only for no Unity project or a bad config (as editor-link).
- Without the bridge: `Unity not reachable (...); ug <command> needs the bridge` plus the
  install hint. A 0.1.0 bridge: `bridge 0.1.0 is too old for ug <command>: run ug bridge
  install again`.
- `ug tree`/`ug inspect` on scenes that are not open: the graph (`ug explain`) already answers
  from the YAML; the skill says to use it and keep the editor for live state and edits.

## Performance targets

| Operation | Target |
| --- | --- |
| Any read (`scene`, `tree`, `inspect`) on an idle editor, fresh `ug` process | ≤ 300 ms |
| One edit on an idle editor, fresh process | ≤ 300 ms |
| `ug play` until playing, BridgeHost | ≤ 5 s |

## Testing

- EditMode tests in the package (BridgeHost, batch mode): addressing (paths, `#n`, scene prefix,
  prefab contents, ambiguity), every endpoint, each safety rule (undo reverts, nothing saved,
  modified scene blocks `open`, refused while compiling is covered by the handler check), value
  parsing per field type.
- Client and CLI tests against the fake bridge: answer formatting and refusals.
- `UG_UNITY` end to end on a copy of BridgeHost: open a scene, tree, inspect, create a prefab
  instance, set a field, move, delete, save and read the saved YAML back through the graph, play
  and stop.

## Boundaries

- Writes scene and asset files only on `ug save`, for prefab-content edits (rule 3), and for
  explicit `ug prefab`, `ug asset` and `ug package` commands.
- Never from a hook; never edits package contents or vendored folders; never discards unsaved
  work.
- The only network access is rule 11's docs fallback, to docs.unity3d.com only.
- purge-squad is the testing ground: the user allowed edits there (2026-09-30; it is in git).

## Open points

1. Addressing by the graph's `o:` keys (scene file + fileID) through `GlobalObjectId`, so a
   `ug refs` answer can feed `ug inspect` directly.
2. Whether `ug play --for <s>` (play, wait, stop, digest) belongs here or in unity-loop's
   `ug log`.
