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
- CLI: `ug scene`, `ug scene open|new|close|active`, `ug save`, `ug play|stop|pause`, `ug tree`,
  `ug inspect`, `ug find --live`, `ug object create|delete|rename|move|active|duplicate`,
  `ug component add|remove`, `ug set`, `ug event list|add|remove`, `ug undo|redo`, `ug select`, `ug prefab ...`,
  `ug asset ...`, `ug project`, `ug package list|add|remove`, `ug shot`, `ug api`, `ug docs`,
  `ug perf`, `ug camera ...` (cameras and Cinemachine 3, `SPEC-camera.md`), menu items through
  `ug run`, and editor commands inside `ug batch`.
- The `unity` skill: the commands and when to use them.
- BridgeHost fixture scenes, prefab and ScriptableObject; EditMode tests; a `UG_UNITY` end to end.

Out: hooks that edit anything (SPEC Boundaries: never from a hook); building players; animation,
timeline and shader graph editing; ProBuilder (ask when wanted); script editing
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
| `manage_gameobject`, `manage_components` | `ug object`, `ug component`, `ug set`, `ug event` (UnityEvent calls such as `Button.onClick`) |
| `manage_editor` | `ug play|stop|pause`, `ug undo|redo`, `ug select`, `ug project` (tags, layers) |
| `execute_menu_item` | `ug run <menu path>` (editor-link's runner already calls `ExecuteMenuItem`) |
| `manage_prefabs` | `ug prefab create|apply|revert|unpack`, prefab-content addresses; `instantiate` is `ug object create --prefab`, no second command |
| `manage_asset`, `manage_material`, `manage_texture` | `ug asset folder|create|move|rename|duplicate|delete|reimport`; `ug set` on materials and importers |
| `manage_packages` | `ug package list|add|remove` through Unity's Package Manager API (no `search`/`resolve`: `add` resolves, and browsing a registry is a network use rule 10 does not cover) |
| `manage_camera` (screenshot) | `ug shot game|scene|<camera>`: a PNG Claude reads only when needed |
| `manage_camera` (every other action: `ensure_brain`, `get_brain_status`, `create_camera`, `set_target`, `set_priority`, `set_lens`, `set_body`, `set_aim`, `set_noise`, `add_extension`, `remove_extension`, `list_cameras`, `set_blend`, `force_camera`, `release_override`, `ping`) | `ug camera [list]|create|priority|body|aim|noise|blend|force|release|brain` (O13); action by action, with lens and extensions through `ug set` and `ug component`, in `SPEC-camera.md`, Unity MCP parity |
| `manage_scene` `screenshot` (every mode) and `scene_view_frame`; `manage_camera` `screenshot`, `screenshot_multiview` | `ug shot` viewpoints (O11, see Viewpoints): `camera` is `ug shot <camera>` (or `--camera` for a temporary camera's settings); `capture_source` `game_view`/`scene_view` is `ug shot game`/`scene`; `view_target` is `--look` (game), `--frame` (scene) or the target of `--angles`/`--orbit`; `view_position` is `--from`; `view_rotation` is `--rotation` (MCP lets it override `view_target`; `ug` refuses the pair, since one of them would be ignored); an instance ID for `camera` or `view_target` is an address (`ug` names objects by path, Addressing objects; `ug find --live` gives the path); `batch: "surround"` and `screenshot_multiview` are `--angles`; `batch: "orbit"` with `orbit_angles`, `orbit_elevations`, `orbit_distance`, `orbit_fov` is `--orbit N --elevations --distance --fov`; `max_resolution` is `--width`/`--height` (one tile of a sheet, as MCP's cap is per tile); `include_image` is Claude reading the PNG (the answer gives its path, never base64); `scene_view_frame` (`scene_view_target`) is `ug shot scene --frame <object> --keep` |
| `manage_graphics`, `manage_physics`, `manage_profiler` | `ug perf` (render, memory, profiler counters; `ug stats` is the token stats); settings through `ug set` on `ProjectSettings/*.asset` |
| `manage_ui` (every action: `ping`, `create`, `read`, `update`, `delete`, `attach_ui_document`, `detach_ui_document`, `create_panel_settings`, `update_panel_settings`, `get_visual_tree`, `render_ui`, `link_stylesheet`, `list`, `modify_visual_element`) | U2 (bridge 0.7.0): `ug ui`, `ug click`, `ug nav`, `ug tree --ui`, element addresses in `ug inspect`/`ug set`, `ug shot <UIDocument object>` and `ug shot <x.uxml> --panel`, `ug asset create` for `.uxml`/`.uss`/`.tss` and PanelSettings, `ug component add|remove UIDocument`; action by action in `SPEC-play-input.md`, UI Toolkit, Unity MCP `manage_ui` parity |
| `unity_reflect`, `unity_docs` | `ug api <Type[.Member]>`, `ug docs <page>`: reflection, then the offline Scripting Reference, then docs.unity3d.com (trimmed, cached) |
| `batch_execute` | `ug batch` with editor commands, one process and one connection |
| `set_active_instance` | One editor per project root |
| `execute_custom_tool` (`tool_name`, `parameters`) | `ug run <Type.Method> [args...]` (SPEC-editor-link, Running code): any static method of the project with typed arguments, overloads, defaults, `out` values, awaited `Task`/`IEnumerator`. A custom tool's `HandleCommand(JObject)` is `ug run MyTool.HandleCommand '{"count": 3}'` (`JObject.Parse`); the tool list is `ug run --list HandleCommand` |
| execute-code calls (MCP `execute_code`; unity-editor-mcp `eval` (`code`, `timeout`), `eval_file` (`file`, `timeout`), `run_script` (`file`, `entry`, `args`, `timeout_ms`, `dry_run`); throwaway editor scripts) | `ug eval "<C#>"`, `ug eval --file x.cs [--entry Type.Method] [args...]` (SPEC-editor-link, Running code): `code` is the argument, `file` is `--file` (`-` for standard input), `entry` is `--entry`, `args` are the arguments after it in `ug run`'s grammar, `timeout`/`timeout_ms` is `--timeout` in seconds, `dry_run` is `--check` (compile only, Unity may be closed). No temporary script in `Assets/`, no domain reload |

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

A component is addressed as `<object>:<Component>` (`Canvas:UiHandler`, `Level/Enemy#1:Transform`,
`Main:Player:Rigidbody`; the part after the last `:` is the component). It is accepted where a
reference is set: `ug set Spawner target Level/Enemy#1:Transform` fills a `Transform` field,
and a field typed `Object` (like `UnityEvent`'s `m_Target`) gets the component, not its game
object. A game object address in a component-typed field still means that object's component of
the field's type. Reads print a component held in an `Object` field the same way, `Canvas:UiHandler`.

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
| `/object/move?object=&parent=&index=` | Reparents (empty `parent`: scene root, `ug object move --root`), keeping the world position. |
| `/object/active?object=&value=` | `SetActive`. |
| `/component/add?object=&type=` | Refused for types that are not components or not found. |
| `/component/remove?object=&component=` | Refused when another component requires it. |
| `/event/list?object=&event=&component=` | The persistent calls of the UnityEvents on an object (`Button.onClick`, a script's `UnityEvent` fields): `addr  Comp.event`, then `  0  Target:Comp.Method(arg)` per call, `(event argument)` for a dynamic call, `off` or `editor and runtime` when the call state is not runtime only. Without `event`, every event that has calls; with it, that event (also when empty). `component` when two components have the event. |
| `/event/add?object=&event=&target=&method=&arg=&component=` | Appends one call through `SerializedObject` (one Undo group, `ug event add`, RuntimeOnly state as in the inspector). `target` is a component address (`Canvas:UiHandler`) or a game object address (the component with the method; the game object itself for `SetActive`). `method` is a public instance `void` method with no argument, or one `int`, `float`, `string`, `bool` or `Object` argument (`arg` is then required), or the event's own argument type (taken from the event when `arg` is absent); a property is `set_<name>` or just `<name>`. Inside a prefab asset the target must be in the same prefab (`Assets/X.prefab:Obj:Comp`); the asset is written at once. Refusals name the methods the target has. |
| `/event/remove?object=&event=&index=&component=` | Removes one call (the only one, or `index`; several without `index` is refused with the count). |
| `/set?object=&component=&field=&value=` | Sets one serialized field through `SerializedObject`. Without `component`, the field is looked up on the game object (name, tag, layer, active, static) and then on its components; two components with the field is ambiguous. |

Added for parity (O6–O9; same rules, same answer shape):

| Request | Does |
| --- | --- |
| `/find?name=&tag=&layer=&component=` | Live objects matching all given filters, as addresses. At most 100. |
| `/object/duplicate?object=`, `/object/move?...&scene=` | Duplicate next to the original; move a root object to another loaded scene. |
| `/scene/new?path=&additive=`, `/scene/close?path=`, `/scene/active?path=` | New scene saved at `path`; close (refused when modified); set the active scene. |
| `/undo`, `/redo`, `/select?object=&clear=` | Unity's Undo/Redo (answers the group name, `ug <command>`); selection read, set or cleared. |
| `/prefab/create?object=&path=`, `/prefab/apply?object=`, `/prefab/revert?object=`, `/prefab/unpack?object=&completely=` | Save an object as a prefab (kept linked; a prefab instance becomes a variant); apply or revert an instance's overrides; unpack (`completely=true` unpacks nested prefabs too). Each is one undo group `ug prefab <sub>`. `create` refuses an existing path, a path outside `Assets/` or without `.prefab`, a missing folder, and objects inside a prefab asset or inside an instance (give the root). `apply` and `revert` answer the overrides they applied or reverted, one line per object (`Crate  Turret.speed`, `added Turret`, `removed object Barrel`); `apply` writes the asset at once and says so, `create` too. The client sends `vendored=<prefixes joined by '|'>` on `apply`, whose asset is unknown until the bridge resolves the instance; the bridge refuses Packages/ and those folders. |
| `/asset/folder`, `/asset/create?path=&type=`, `/asset/move?from=&to=`, `/asset/rename?path=&name=`, `/asset/duplicate?path=&to=`, `/asset/delete?path=`, `/asset/reimport?path=` | Through `AssetDatabase`, so `.meta` files and GUIDs stay right. `create` makes folders, materials (`type=Material&shader=`) and ScriptableObjects, and from bridge 0.7.0 `.uxml`, `.uss` and `.tss` files with Unity's starting content (SPEC-play-input, UI Toolkit, Setup; a PanelSettings may make Unity write its default theme, which the answer names). `delete` moves to the OS trash (`MoveAssetToTrash`). |
| `/set?object=Assets/X.png&importer=true&...` | Importer settings (texture, model, audio) through the importer's `SerializedObject`, then reimport. Material properties by name (`_BaseColor`, `_MainTex`). |
| `/project` | Unity version and render pipeline, tags, layers (Unity's physics layers, as `index name`), sorting layers, build scenes (enabled ones with their build index). Read-only. |
| `/package/list`, `/package/add?id=`, `/package/remove?name=` | `UnityEditor.PackageManager.Client`. The answer waits for the request across editor updates (the route returns a poll that gives null until the request has finished; the connection stays open) and gives the resolved `name  version  source` (`local`, `git`, `registry`; the location for local and git). `list` reads the local cache (offline) and shows the manifest's direct dependencies, counting `com.unity.modules.*` in one line. `id` is a name, `name@version`, a git URL or `file:` path. Refused while compiling or importing and in play mode (a package change reloads scripts); `remove` refuses `com.unitygraph.bridge` (removing it would end the connection). Failures answer `could not add <id>: <Unity's message>`. |
| `/run?target=<menu path>` | Menu items through editor-link's `/run`, which already calls `EditorApplication.ExecuteMenuItem`; now refused for File/Quit, File/Exit, `File/Build...` and `Build/...` menu items (matched case-insensitively, spaces around `/` ignored): `did not run File/Quit: refused, it would close the editor (ug does not quit Unity)`. The refusal is in the bridge's runner, so nothing is executed. |
| `/shot?view=game\|scene\|<camera address>&width=&height=` (bridge 0.6.0 adds `from`, `look`, `rotation`, `fov`, `frame`, `keep`, `angles`, `orbit`, `elevations`, `distance`, `camera`: Viewpoints) | Renders to `Library/unitygraph/shots/game.png`, `scene.png` or `camera-<name>.png` (overwritten each time) and answers `path  WxH  N KB  (Game view, 1 camera)`. Default 960x540, `width` and `height` 64 to 2048. `game` renders every enabled Game camera in depth order into a temporary render texture (each camera's own target is put back; root Screen Space - Overlay canvases are pointed at the last camera for the render, since no camera draws them, and put back after, without an Undo entry or a dirty flag; UI Toolkit screen panels are drawn over the cameras from bridge 0.7.0 by pointing their PanelSettings at the shot's texture for the render, and the answer counts them, `(Game view, 1 camera, 2 UI Toolkit documents)`; before 0.7.0 the answer ended `, N UI Toolkit documents not drawn`; an object with a UIDocument and a `.uxml` path with `panel=` draw UI Toolkit alone: SPEC-play-input, UI Toolkit); `scene` renders the Scene view's camera; an address renders that one camera. Refused with `cannot take a screenshot: this Unity runs without a graphics device` when the editor runs with `-nographics` (a blank PNG would mislead). Read-only. |
| `/api?type=&member=` | Types, members and signatures by reflection over the loaded assemblies: `Transform`, `UnityEngine.Transform`, `Physics.Raycast` (split at the last dot when no type has that name); short names match case-sensitively first, Unity's namespaces win over System's, an ambiguous name lists its candidates. The type view is under 300 tokens for the common types (kind, base, assembly, constructors, then property, method, static and event names, longest lists cut with `+N more`); `member` lists overloads with parameter names, `out`/`ref`/`params` and default values. Members inherited from a base say `(from Object)`; obsolete ones are left out. When reflection finds nothing the answer is `{"error":..., "online":[page addresses]}`, or `{"local":"<file>"}` when the Hub's Documentation module has the page. |
| `/api?page=Manual/<page>\|ScriptReference/<page>\|<package>[@version]/<path>` | Names a documentation page (same answer shape: `local` file, else `error` and `online`); `ug docs` uses it. Bare `<page>` means `Manual/<page>`. The package version is the installed one's major.minor. The bridge never opens a connection: fetching, trimming and caching are the client's (rule 11). |
| `/stats` (`ug perf`) | Lines: play state; `render` (draw calls, batches, triangles, set-pass calls, render ms from `UnityStats`, which only counts in play mode, else `only counted in play mode (ug play, then ug perf)`); memory (allocated, reserved, scripts heap, graphics driver, MB); objects (game objects, active, components in the loaded scenes); assets (files under `Assets/` with the main kinds counted). Read-only. |

`field` is the serialized name (`speed`, `m_LocalPosition`, `waves[2].count`); display names
(`Speed`) also match. `value` by the field's type: numbers (invariant culture), `true`/`false`,
strings, enum names, vectors `1,2,3`, colors `#ff8800` or `1,0.5,0,1`, object references as an
object address, asset path or `null`, array size via `field.size`. Compound values read what
`ug inspect` prints, so a value read can be set back:

| Type | Text |
|---|---|
| Bounds | `center 0,1,0 size 2,2,2` or six numbers |
| AnimationCurve | `0:0, 1:1` (time:value), led by `linear`, `constant` or `smooth` (default); one number is a flat curve; `empty` |
| Gradient | `#ff0000@0, #0000ff80@1` (color@time, at most 8; without `@` the stops spread evenly), led by `fixed` for a stepped gradient |
| Hash128 | 32 hex digits |
| struct, class | `{count: 3, offset: 1,2,3}` or `count: 3`; fields not named keep their value |
| list or array | `[a, b, c]`, `a, b, c` or `empty`; vector items as `(1,2,3)` or bare in runs of their size; struct items as `{...}` |
| `[SerializeReference]` | `TypeName`, `TypeName {radius: 4}`, `{radius: 4}` (keeps the type) or `null`; the type is a short or full name of a concrete type that fits the field, else the error lists them |

A problem in any part sets nothing (`pairs[1]: count: not a whole number: x`). Still refused:
private fields without `[SerializeField]`, properties and statics (not serialized; `ug run`
or a script change), and anything under `Packages/` or vendored code.

## Viewpoints (O11, bridge 0.6.0)

`ug shot` also renders from where Claude asks, not only through the cameras the scene has.

```
ug shot [game|<camera>] --from x,y,z [--look <object|x,y,z> | --rotation x,y,z] [--fov f]
ug shot [game|<camera>] --look <object|x,y,z> [--fov f]
ug shot scene --frame <object> [--keep]
ug shot <object|x,y,z> --angles [--distance d] [--fov f] [--camera <camera>]
ug shot <object|x,y,z> --orbit [N] [--elevations 0,30,-15] [--distance d] [--fov f] [--camera <camera>]
```

All take `--width`/`--height`. Request: `/shot?view=&from=&look=&rotation=&fov=&frame=&keep=&angles=&orbit=&elevations=&distance=&camera=&width=&height=`.

- **Temporary camera.** `--from`, `--look`, `--rotation`, `--angles` and `--orbit` render through a camera made
  for the request: `EditorUtility.CreateGameObjectWithHideFlags("ug shot camera", HideFlags.HideAndDontSave,
  typeof(Camera))`, disabled (it never draws to the Game view by itself) and destroyed in a `finally`. Its
  settings are `Camera.CopyFrom` of a source camera: `--camera`, else the camera the view names (viewpoints),
  else `Camera.main`, else the enabled Game camera drawn last, else Unity's defaults; the answer names it
  (`settings of Main Camera`). Under URP the source's `UniversalAdditionalCameraData` is copied too
  (`EditorUtility.CopySerialized`: renderer, post-processing, anti-aliasing, volume layers) by the
  sub-assembly `UnityGraph.Bridge.Editor.Urp` (`versionDefines` on `com.unity.render-pipelines.universal`),
  with the camera stack emptied and the render type set to Base: a viewpoint shows the world from that
  point, so URP overlay cameras (a weapon model, a UI camera) and overlay canvases are left out. HDRP's and
  Post Processing v2's camera components are not copied (not tested; the plain copy still renders). Nothing
  goes on the Undo stack and no scene is marked dirty (the tests check both); being HideAndDontSave, the
  camera is never saved even if Unity dies mid-render.
- **`--from x,y,z`** places it; **`--look <object|x,y,z>`** aims it at a point or at an object's centre
  (the bounds of its renderers, else its colliders, else its position); **`--rotation x,y,z`** sets Euler
  angles instead (with `--look` it is refused: both aim). `--look` alone keeps the source camera's position
  (the three-quarter view of the target when there is no source camera), `--from` alone its rotation.
  **`--fov`** 1 to 179 degrees (orthographic sources keep their size). File `view.png`.
- **`ug shot scene --frame <object>`** frames the Scene view on the object as its F key does
  (`SceneView.Frame(bounds, true)`), poses the Scene view's camera to match, renders it, and puts the
  pivot, rotation and size back (`LookAtDirect`) and the camera's pose and clip planes, so the user's
  view does not move. **`--keep`** leaves the Scene view framed: MCP's `scene_view_frame`. File
  `scene.png`. Like `ug shot scene`, it renders the Scene view's camera: gizmos and the grid are drawn
  by the Scene view window, not its camera, so they are not in the picture (Open points).
- **`--angles`**: six views of an object or a point in one PNG, 3×2 tiles in this order: front, back,
  left, right, top, three-quarter (azimuth 45°, elevation 30°). Front is the side the object faces (its
  forward flattened to the horizontal; world +Z for a point or an object facing straight up); top looks
  down with the front at the top of the tile. MCP's surround has bird_eye in place of three-quarter:
  `--orbit 1 --elevations 60` is a high oblique view. File `angles-<target>.png`.
- **`--orbit [N]`**: N azimuth steps (default 8, 1 to 36; 0° is the front, 90° the target's right) by
  `--elevations` (default `0,30,-15` as MCP's, -89 to 89 each, at most 6), one row per elevation in the
  given order. File `orbit-<target>.png`.
- **Distance.** `--distance d` (sheets only), else fitted: the bounds' bounding sphere fills the
  narrower of the vertical and horizontal field of view (radius / sin(fov / 2) × 1.05); an orthographic
  source gets an orthographic size that fits instead. A point, or an object with no renderer or
  collider, has no size: 10 units. The near plane moves in and the far plane out so the target is never
  clipped.
- **Sizes.** `--width`/`--height` (64 to 2048) are the picture, or one tile of a sheet (default tile
  320×240 for `--angles`, 240×180 for `--orbit`). A sheet is at most 4096 pixels per side, else refused
  with the tile width that fits. Each tile renders into one temporary render texture and is read into
  the sheet's `Texture2D` with `ReadPixels` at its place, 2 px dark gaps between tiles; no image library.
- **Answers** give the path, size, KB and what was drawn; a sheet adds its layout:

  ```
  $ ug shot Player --angles
  Library/unitygraph/shots/angles-Player.png  964x482  180.2 KB  (Player, 6 views of 320x240, distance 4.2, settings of Main Camera)
    rows: front, back, left | right, top, three-quarter
  $ ug shot Player --orbit 4 --elevations 0,45
  Library/unitygraph/shots/orbit-Player.png  966x362  120.5 KB  (Player, 4 azimuths x 2 elevations of 240x180, distance 4.2, settings of Main Camera)
    columns: azimuth 0, 90, 180, 270 (0 = front, 90 = right); rows: elevation 0, 45
  $ ug shot --from 0,10,-10 --look Enemy
  Library/unitygraph/shots/view.png  960x540  95.1 KB  (from 0, 10, -10 looking at Enemy, settings of Main Camera)
  ```
- **Refusals** (checked before the graphics-device check, so they answer under `-nographics` too): `scene`
  with `--from`, `--look`, `--rotation`, `--angles` or `--orbit` (the Scene view moves only with
  `--frame`); `--frame` without `scene`; `--keep` without `--frame`; `--angles` with `--orbit`; a sheet
  without a target or with `game`/`scene`; `--elevations` or `--distance` without a sheet; `--fov`,
  `--distance` (above 0) or a count out of range; a vector that is not three numbers; `--from` on the
  `--look` point; two different cameras (`--camera` and a camera view); a sheet over 4096 pixels.
- **Older bridge.** A bridge before 0.6.0 ignores parameters it does not know and would answer a plain
  Game view, so `ug` refuses the new options on it (`bridge 0.5.0 is too old for ug shot --angles: run
  ug bridge install again`).
- **Play mode** works the same: the camera is made and destroyed between two frames of the game.

Measured on purge-squad (2026-10-11, Unity 6000.3.14f1 open with its window, URP 17.3.0, bridge
0.6.0 copied in; fresh `ug` process, wall clock, answer bytes): plain `ug shot` 254 ms, 74 B;
`--from 0,12,-12 --look Player` 273 ms, 113 B; `--look Player` 216 ms, 115 B; `Player --angles`
252 ms, 185 B (964x482, 142 KB); `Player --orbit` (8 × 3 tiles) 423 ms, 255 B (1934x544,
387 KB); `--orbit 4 --elevations 0,45` 238 ms, 230 B; `0,0,0 --angles --distance 30` 247 ms,
189 B; `scene --frame Player` 256 ms, 81 B, Scene view pivot, rotation and size the same after,
and with `--keep` 222 ms, 94 B, left framed on the player. In play mode `Player --angles` 655 ms.
One call each where MCP's surround or orbit is also one call; the answers are paths, never base64.
The sheet reads correctly: the face in the front tile, the left and right sides matching the
model's colours, the top with the front up. URP, checked with `ug eval` through the shot camera:
with the source's post-processing off, SMAA, dithering on and volume mask 32, the shot camera's
`UniversalAdditionalCameraData` had the same, render type Base, and was HideAndDontSave. The scene
stayed unmodified after every shot, the editor's undo records stayed at 0, and the viewpoint and
sheet pictures leave out the overlay HUD that `ug shot` (game) draws.

## Safety rules

1. **Undo.** Every edit goes through Unity's Undo (`RegisterCreatedObjectUndo`,
   `DestroyObjectImmediate`, `SetTransformParent`, `AddComponent`, `ApplyModifiedProperties`),
   one undo group per request named `ug <command>`, so Ctrl+Z in the editor reverts it.
   One exception (U2): `ug set` on a UI Toolkit element changes a live `VisualElement`, which is
   not a Unity object and has no Undo; nothing is saved or marked dirty, the document rebuilds the
   element from its UXML on its next enable, and the answer says `live element only: not saved,
   no undo` (SPEC-play-input, UI Toolkit).
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
   Asset edits are not on the Undo stack: they write at once and answer `not undoable`. They are
   refused for `Packages/`, vendored folders, existing targets, `Assets/` itself and open scenes.
10. **Packages.** `ug package add|remove` only on an explicit command, through Unity's Package
    Manager (Unity writes `Packages/manifest.json`, not ug). Added 2026-09-30 by the user. `add`
    of a registry or git package downloads it through the Package Manager: the one network use
    besides rule 11, done by Unity and only for the id the user gave; `list` and `remove` use
    the local cache. A `file:` id is local. Never in play mode, never for the bridge itself.
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

$ ug event add Canvas/Button onClick Canvas:UiHandler OnPlay
Canvas/Button  Button.onClick  + Canvas:UiHandler.OnPlay()  (UiTest.unity modified, not saved)

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
- Edits are checked against the vendored folders in the client too, before any request:
  `Assets/Plugins/X is in the vendored folder Assets/Plugins/, which ug does not edit`.
- `ug package add|remove` wait up to `--timeout` seconds (default 300) for the Package Manager,
  `ug package list` up to 60 s; past it: `no answer from Unity (no answer within 300 s: Unity is
  busy)`. A package change that reloads scripts before Unity answers ends the connection
  (`no answer from Unity (bridge answered 503: ...)`): `ug package list` says whether it applied.
- `ug play` and `ug stop` wait up to `--timeout` seconds (default 60) for the state, riding out
  the domain reload; past it: `Unity was still idle after 60 s (entering play mode)`. An editor
  that exits meanwhile: `Unity (pid N) exited while entering play mode`.
- `ug shot`, `ug api`, `ug docs` and `ug perf` (O9). `ug api` and `ug docs` print a documentation
  page as trimmed text (headings, paragraphs, lists, code; navigation, scripts and markup dropped;
  cut to the answer budget with a line naming the page) in the order rule 11 gives: local file,
  the client's cache, then one fetch. The fetch is https to `docs.unity3d.com` only (redirects
  stay on that host), 15 s timeout, 2 MB cap, no cookies or project data in the request. The
  cache is one trimmed text file per page address under `%LOCALAPPDATA%/unitygraph/docs`
  (`~/Library/Caches/unitygraph/docs` on macOS, `$XDG_CACHE_HOME` or `~/.cache` on Linux).
  A page that cannot be fetched (`docs.online: false`, no network, HTTP error) prints the
  bridge's error and the page address to read: `not fetched, read it at: <url> (<reason>)`.
  `.claude/unitygraph.json`: `{ "docs": { "online": false } }`. An older 0.2.0 bridge without
  these endpoints answers `unknown request`.
- `ug batch "step" "step" ... [--file <path>|-]` runs editor-ops commands in one process over one
  bridge connection (one `/status`, then one request per step). A step is a command as typed
  after `ug` (a leading `ug` is allowed; quote words with spaces). Only the editor-ops commands
  are steps (`scene`, `save`, `play`, `stop`, `pause`, `tree`, `inspect`, `object`, `component`,
  `set`, `prefab`, `asset`, `project`, `package`, `shot`, `api`, `docs`, `perf`, `undo`, `redo`,
  `select`); `find`, `run`, `compile`, `status`, `bridge` and `batch` are not. Every step is
  parsed first: one that is unknown or malformed exits 1 with `step N (<step>): <why>` and
  `nothing ran`. Steps then run in order; the first refusal, vendored refusal, timeout or lost
  connection stops the rest (`stopped at step N: M steps not run. Earlier steps stay done; ug
  undo reverts them one by one.`). Output is `i/n <step>` followed by the step's own lines.
  Each step is its own undo group, so `ug undo` reverts one step; asset steps are not undoable.
  `--file` reads one step per line, `#` starts a comment, `-` reads standard input. Exit code 0
  as for other commands, 1 for a usage error.
- `ug save` is a top-level command (it saves scenes and assets alike), and `ug object move
  --root` stands for the endpoint's empty `parent`.
- `ug tree`/`ug inspect` on scenes that are not open: the graph (`ug explain`) already answers
  from the YAML; the skill says to use it and keep the editor for live state and edits.

## Performance targets

| Operation | Target |
| --- | --- |
| Any read (`scene`, `tree`, `inspect`) on an idle editor, fresh `ug` process | ≤ 300 ms |
| One edit on an idle editor, fresh process | ≤ 300 ms |
| `ug play` until playing, BridgeHost | ≤ 5 s |

Measured by the `UG_UNITY` end to end (O5, 2026-09-30, Unity 6000.3.14f1 in batch mode,
Windows 11): fresh-process `ug status` 128 ms, `ug tree` 125 ms, `ug inspect` 124 ms and
`ug object active` (an edit) 125 ms, medians of 5; `ug play` until playing 1.07 s, `ug stop`
until edit mode 0.26 s. In process, reads and edits take 8–37 ms (`ug save` 37 ms); the rest of
a fresh process's time is .NET start-up.

## Testing

- EditMode tests in the package (BridgeHost, batch mode): addressing (paths, `#n`, scene prefix,
  prefab contents, ambiguity), every endpoint, each safety rule (undo reverts, nothing saved,
  modified scene blocks `open`, refused while compiling is covered by the handler check), value
  parsing per field type.
- Client and CLI tests against the fake bridge: answer formatting and refusals.
- `UG_UNITY` end to end on a copy of BridgeHost (`Assets/Scenes/Main.unity`,
  `Assets/Prefabs/Turret.prefab`, `Assets/Scripts/Turret.cs`): open a scene, tree, inspect,
  create a prefab instance, set a field, move, delete, save and read the saved YAML back through
  the graph (`ug explain o:<scene guid>:<file id>`), prefab create from a scene object, a field set
  on an instance, apply (asset read back with `ug explain Assets/Prefabs/Crate.prefab` and the YAML),
  revert and unpack, object duplicate with undo and redo, select, scene new, active and close, play,
  `find --live`, and stop. Assets: folder, ScriptableObject create and `set`, material with a shader
  and a property set, a texture importer setting, move, rename, duplicate (`.meta` GUID kept or new),
  delete to the trash, reimport, and the refusals. Project and packages (O8): `ug project`; `ug
  package list`; add of a local `file:` package made in the copy (manifest read back, then `list`),
  remove, and the refusals (the bridge itself, no id, a package that is not installed); menu run
  of `Tools/BridgeHost/Log Hello` (the editor log carries its message), a missing item, and the
  File/Quit and File/Build refusals with the editor still answering. Nothing is downloaded; a
  registry or git add is never run by the tests.
  Screenshots, API, docs, stats and batch (O9): `ug shot` refused with the graphics-device message
  in the `-nographics` editor (the PNG itself is written and read back by EditMode tests only where
  a graphics device exists: 3 tests skip in batch mode); `ug api Transform` in under 300 tokens,
  `Physics.Raycast`, a member, an unknown type (the fake fetch stands in for docs.unity3d.com: no
  test uses the network); `ug perf` in edit and play mode; `ug batch` with a stop at a refusal and
  `ug undo` reverting the steps. UI (post-O10): `ug event add|remove|list` (void, argument, event
  argument, GameObject target, prefab asset, refusals) and component references in `ug set`, each
  in EditMode tests, plus a UI scene built with `ug` and shot in the real editor (the overlay canvas
  is in the picture, the canvas settings and the dirty flag are as before). Token counts of `ug api <Type>` (bytes / 4, measured by the
  EditMode tests): Transform 207, GameObject 190, Rigidbody 214, Rigidbody2D 212, Camera 246,
  Collider 109, Vector3 164, Physics 257, Time 156, MonoBehaviour 88, Mathf 171, Input 227.
  Viewpoints (O11): every refusal in EditMode tests (they run before the device check, so they
  pass under `-nographics`); with a device, a viewpoint and its rotation read back by pixel (a blue
  unlit cube on a red-clearing source camera), `--look` alone from the source's place and with no
  camera at all, `--angles` (six tiles, each with the cube in the middle and background in the
  corner, the gap colour between), `--orbit` (layout, point target, `--distance`), and the trace
  test: after a viewpoint and a sheet the saved scene is not dirty, has the same roots, no `ug shot
  camera` is left, and one `Undo.PerformUndo` reverts the edit made before the shot. The batch-mode
  editor has no Scene view, so `--frame` is checked there for its refusal and live in purge-squad.
  URP's camera data copy is checked live in purge-squad only (BridgeHost is built-in; no package is
  added for it). CLI tests: each option's request and the old-bridge refusal; the `UG_UNITY` end to
  end adds two refusals and a sheet refused for want of a device.

## Boundaries

- Writes scene and asset files only on `ug save`, for prefab-content edits (rule 3), and for
  explicit `ug prefab`, `ug asset` and `ug package` commands.
- Never from a hook; never edits package contents or vendored folders; never discards unsaved
  work.
- The only network access is rule 11's docs fallback, to docs.unity3d.com only, and the
  Package Manager's own downloads when the user runs `ug package add` (rule 10).
- purge-squad is the testing ground: the user allowed edits there (2026-09-30; it is in git).

## Open points

1. Addressing by the graph's `o:` keys (scene file + fileID) through `GlobalObjectId`, so a
   `ug refs` answer can feed `ug inspect` directly.
2. Whether `ug play --for <s>` (play, wait, stop, digest) belongs here or in unity-loop's
   `ug log`.
3. Gizmos, icons and the grid in `ug shot scene`: the Scene view window draws them around its
   camera's render, so a camera render leaves them out. Getting them means repainting the window
   and reading its pixels back (an internal grab or a screen read of a window that must be visible
   and unobscured); not built until a task needs the handles in the picture.
