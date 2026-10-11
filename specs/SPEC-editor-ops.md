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
| `manage_prefabs` | `ug prefab create|apply|revert|unpack`, prefab-content addresses; `instantiate` is `ug object create --prefab`, no second command. `ug prefab overrides` (C1) lists an instance's overrides, which `manage_prefabs` has no action for |
| `manage_asset`, `manage_material`, `manage_texture` | `ug asset folder|create|move|rename|duplicate|delete|reimport`; `ug set` on materials and importers |
| `manage_packages` | `ug package list|add|remove` through Unity's Package Manager API (no `search`/`resolve`: `add` resolves, and browsing a registry is a network use rule 10 does not cover) |
| `manage_camera` (screenshot) | `ug shot game|scene|<camera>`: a PNG Claude reads only when needed |
| `manage_camera` (every other action: `ensure_brain`, `get_brain_status`, `create_camera`, `set_target`, `set_priority`, `set_lens`, `set_body`, `set_aim`, `set_noise`, `add_extension`, `remove_extension`, `list_cameras`, `set_blend`, `force_camera`, `release_override`, `ping`) | `ug camera [list]|create|priority|body|aim|noise|blend|force|release|brain` (O13); action by action, with lens and extensions through `ug set` and `ug component`, in `SPEC-camera.md`, Unity MCP parity |
| `manage_scene` `screenshot` (every mode) and `scene_view_frame`; `manage_camera` `screenshot`, `screenshot_multiview` | `ug shot` viewpoints (O11, see Viewpoints): `camera` is `ug shot <camera>` (or `--camera` for a temporary camera's settings); `capture_source` `game_view`/`scene_view` is `ug shot game`/`scene`; `view_target` is `--look` (game), `--frame` (scene) or the target of `--angles`/`--orbit`; `view_position` is `--from`; `view_rotation` is `--rotation` (MCP lets it override `view_target`; `ug` refuses the pair, since one of them would be ignored); an instance ID for `camera` or `view_target` is an address (`ug` names objects by path, Addressing objects; `ug find --live` gives the path); `batch: "surround"` and `screenshot_multiview` are `--angles`; `batch: "orbit"` with `orbit_angles`, `orbit_elevations`, `orbit_distance`, `orbit_fov` is `--orbit N --elevations --distance --fov`; `max_resolution` is `--width`/`--height` (one tile of a sheet, as MCP's cap is per tile); `include_image` is Claude reading the PNG (the answer gives its path, never base64); `scene_view_frame` (`scene_view_target`) is `ug shot scene --frame <object> --keep` |
| `manage_graphics`, `manage_physics` | `ug perf` (render, memory; `ug stats` is the token stats); settings through `ug set` on `ProjectSettings/*.asset` |
| `manage_profiler` (every action: `ping`, `profiler_start` (`log_file`, `enable_callstacks`), `profiler_stop`, `profiler_status`, `profiler_set_areas` (`areas`), `get_frame_timing`, `get_counters` (`category`, `counters`), `get_object_memory` (`object_path`), `memory_take_snapshot` (`snapshot_path`), `memory_list_snapshots` (`search_path`), `memory_compare_snapshots` (`snapshot_a`, `snapshot_b`), `frame_debugger_enable`, `frame_debugger_get_events` (`page_size`, `cursor`), `frame_debugger_disable`) | O12 (bridge 0.8.0, see Profiler): `ping` is `ug status` (the bridge answers) and `ug perf profiler` (the profiler's state); `profiler_start` is `ug perf profiler start [--file [<path>.raw]] [--callstacks]`; `profiler_stop`, `profiler_status` are `ug perf profiler stop`, `ug perf profiler [status]`; `profiler_set_areas` is `ug perf profiler areas CPU=on GPU=off ...`; `get_frame_timing` is `ug perf timing`; `get_counters` is `ug perf counters [<name>...] [--category <c>] [--markers]`; `get_object_memory` is `ug perf memory --object <x>`; `memory_take_snapshot` is `ug perf snapshot take [<path>]`; `memory_list_snapshots` is `ug perf snapshot list [--folder <dir>]`; `memory_compare_snapshots` is `ug perf snapshot compare <a> <b>` (native objects by type and name, not MCP's file sizes; no package needed, Unity may be closed); `frame_debugger_*` is `ug perf frame-debugger enable|events [--from <cursor>] [--top <page_size>]|disable`. Beyond it: `ug perf --record <s>` and `--frames` (top markers by self time, GC allocations, spikes), `ug perf memory` (top objects), one frame debugger event's detail |
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

From bridge 0.12.0 a component address is also the `<object>` of `ug inspect`, `ug set` and
`ug event list|add|remove` when no `--component` is given: `ug set Boot:AudioSource volume 0.25`
is `ug set Boot volume 0.25 --component AudioSource`, `ug inspect Canvas:UiHandler` lists that
component's fields, and `ug event list Canvas:UiHandler` that component's events. With
`--component` the address is an object address, as before. Before 0.12.0 these commands read the
whole address as an object (`not found: Canvas:UiHandler in the loaded scenes`).

`X:Y` reads two ways, and every place that takes a component address (the object of `inspect`,
`set` and `event`, a reference value in `set`, an event target, `perf memory --object`) tries both:

- the object reading: the whole address as an object, `Y` in the loaded scene or prefab asset `X`
  (or, when `X` is neither, an object whose name holds the `:`);
- the component reading: the part after the last `:` is a component of the object the part
  before it names.

A reading matches when it finds what it names, or finds several (its own `ambiguous:` answer; for
the component reading also when the part before the last `:` names several objects and the part
after it is a component type's name, as `Camera:Transform` with two scenes loaded, each with a
`Camera`: `Main:Camera:Transform` picks one).

| Matches | Answer |
| --- | --- |
| Only the object reading | That object. `Main:Player` in a project without an object `Main`. |
| Only the component reading | That component. `Boot:AudioSource` where scene `Boot` has no root `AudioSource` and the object `Boot` has an AudioSource. |
| Both | `ambiguous: Boot:AudioSource is the object Boot:/AudioSource or the component Boot:Boot:AudioSource`. Each form matches one reading only: the object form puts a `/` after the scene or prefab, so nothing after the last `:` can be a component type; the component form names its object with the scene in front, so the object reading looks for one object named `Boot:AudioSource`. A component of a prefab asset's root is given as `Assets/X.prefab::Rigidbody` (the empty path after `Assets/X.prefab:` is the root). An object before the last `:` that is itself ambiguous gives its candidates instead: `... or a component of Other (2 in the loaded scenes): Main:Other, Other:Other`. |
| Neither | The object reading's `not found:` when the part before the last `:` names no object; else that object's `<object> has no Y (has: ...)`, after the object reading's `not found: Y in X (near: ...)` and `; ` when `X` is a loaded scene or a prefab asset (`Boot:AudioSrc` is a typo of either). |

Answers print addresses that read one way only, so a printed address can be passed back as is.
An object prints as `Main:/Camera` (two or more scenes loaded) or `Assets/X.prefab:/Camera` when
`Main:Camera` would also read as the Camera of an object `Main`, or `Assets/X.prefab:Camera` as
the prefab root's Camera; a component prints as `Main:Main:AudioSource` or
`Assets/X.prefab::AudioSource` when the short form would also name an object. Otherwise the short
forms print, as before 0.12.0.

An object whose name holds a `:` can match both readings under any form: rename it (as names with
`/`, which `ug` refuses).

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
| `/play`, `/stop`, `/pause` | Sets `EditorApplication.isPlaying`/`isPaused`. `/play` is refused while compile errors exist. Answers at once; the client waits for the state (domain reloads handled as in the compile flow). `/stop` answers the play session's time and error/warning counts. Bridge 0.13.0 adds `/play?paused=&onerror=` and `/play/state`: Play-mode debugging. |
| `/tree?object=&depth=` | Children of an object (or the roots of the loaded scenes) to `depth` (default 2): name, active, components, child count past the depth. At most 200 lines. |
| `/inspect?object=&component=` | Components of an object, or one component's visible serialized fields: name, type, value (object references as addresses or asset paths; arrays with length and the first 10 items). At most 150 lines. Bridge 0.9.0 adds `field=` (one field whole) and `from=` (paging): Nothing out of reach. From bridge 0.12.0 `object` may be a component address when `component` is empty (Addressing objects). |
| `/object/create?name=&parent=&prefab=&primitive=` | New empty object, primitive, or a prefab instance kept linked to its prefab. |
| `/object/delete?object=` | Deletes an object and its children. |
| `/object/rename?object=&name=` | |
| `/object/move?object=&parent=&index=` | Reparents (empty `parent`: scene root, `ug object move --root`), keeping the world position. |
| `/object/active?object=&value=` | `SetActive`. |
| `/component/add?object=&type=` | Refused for types that are not components or not found. |
| `/component/remove?object=&component=` | Refused when another component requires it. |
| `/event/list?object=&event=&component=` | The persistent calls of the UnityEvents on an object (`Button.onClick`, a script's `UnityEvent` fields): `addr  Comp.event`, then `  0  Target:Comp.Method(arg)` per call, `(event argument)` for a dynamic call, `off` or `editor and runtime` when the call state is not runtime only. Without `event`, every event that has calls; with it, that event (also when empty). `component` when two components have the event. From bridge 0.12.0 `object` may be a component address when `component` is empty (Addressing objects). |
| `/event/add?object=&event=&target=&method=&arg=&component=` | Appends one call through `SerializedObject` (one Undo group, `ug event add`, RuntimeOnly state as in the inspector). `target` is a component address (`Canvas:UiHandler`) or a game object address (the component with the method; the game object itself for `SetActive`). `method` is a public instance `void` method with no argument, or one `int`, `float`, `string`, `bool` or `Object` argument (`arg` is then required), or the event's own argument type (taken from the event when `arg` is absent); a property is `set_<name>` or just `<name>`. Inside a prefab asset the target must be in the same prefab (`Assets/X.prefab:Obj:Comp`); the asset is written at once. Refusals name the methods the target has. From bridge 0.12.0 `object` may be a component address when `component` is empty (Addressing objects). |
| `/event/remove?object=&event=&index=&component=` | Removes one call (the only one, or `index`; several without `index` is refused with the count). From bridge 0.12.0 `object` may be a component address when `component` is empty (Addressing objects). |
| `/set?object=&component=&field=&value=` | Sets one serialized field through `SerializedObject`. Without `component`, the field is looked up on the game object (name, tag, layer, active, static) and then on its components; two components with the field is ambiguous. From bridge 0.12.0 `object` may be a component address when `component` is empty (Addressing objects). |

Added for parity (O6–O9; same rules, same answer shape):

| Request | Does |
| --- | --- |
| `/find?name=&tag=&layer=&component=` | Live objects matching all given filters, as addresses. At most 100 (`all=true` from bridge 0.9.0: every match). |
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
| `/stats` (`ug perf`) | Lines: play state; `render` (draw calls, batches, triangles, set-pass calls, render ms from `UnityStats`, which only counts in play mode, else `only counted in play mode (ug play, then ug perf)`); memory (allocated, reserved, scripts heap, graphics driver, MB); objects (game objects, active, components in the loaded scenes); assets (files under `Assets/` with the main kinds counted). Read-only. Bridge 0.8.0 adds the `/perf/...` requests: Profiler. |

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
  (`settings of Main Camera`). A source whose culling mask is Nothing (a boot or UI-only scene's camera)
  would give a blank picture: the shot camera draws every layer and the answer says `settings of Main
  Camera but every layer (its culling mask is Nothing)`; the source keeps its mask (O10 fix, bridge
  0.10.0). Under URP the source's `UniversalAdditionalCameraData` is copied too
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

## Profiler (O12, bridge 0.8.0)

`ug perf` keeps its one-screen summary (`/stats`) and gains the profiler: recorded frames read
back as top markers, the profiler session and its `.raw` file, counters, frame timing, object
memory, memory snapshots and the Frame Debugger. Unity's own APIs, no package; edit and play mode.

```
ug perf --record <s> [--max-frames N] [--top N]   ug perf --frames <a-b|all> [--top N]
ug perf profiler [status]                 ug perf profiler start [--file [<path>]] [--callstacks] [--editor]
ug perf profiler stop                     ug perf profiler areas [<Area>=on|off ...]
ug perf counters [<name>...] [--category <c>] [--markers] [--all]
ug perf timing
ug perf memory [--top N] [--type <T>]     ug perf memory --object <x>
ug perf snapshot take [<path>]            ug perf snapshot list [--folder <dir>] [--all]
ug perf snapshot compare <a> <b> [--top N]
ug perf frame-debugger enable|disable     ug perf frame-debugger events [<n>] [--from N] [--top N]
```

Requests: `/perf/record?seconds=&max-frames=&frames=&top=`, `/perf/profiler?do=&file=&callstacks=&editor=&areas=`,
`/perf/counters?names=&category=&markers=&all=`, `/perf/timing`, `/perf/memory?top=&type=&object=`,
`/perf/snapshot?path=`, `/perf/frame-debugger?do=&event=&from=&top=`. `snapshot list` and `compare`
run in the client and need no Unity. Every list is capped and ends with a `more` line naming the
command that shows the rest.

- **`--record <s>`** (0.1 to 60 s) records the editor's frames for that long and analyses them. When
  the profiler is off it sets `ProfilerDriver.profileEditor` (on in edit mode, off in play mode, as the
  Profiler window's target) and `ProfilerDriver.enabled`, and puts both back after; when it is already
  recording (the Profiler window, `ug perf profiler start`) it is left alone and the frames recorded
  meanwhile are read. Frames are never cleared. The analysis runs after the recording, so it does not
  slow the frames it measures, a slice of about 50 ms per editor update so the editor stays usable.
  Per frame, main thread: `ProfilerDriver.GetHierarchyFrameDataView(frame, 0, MergeSamplesWithTheSameName
  | InvertHierarchy, ...)`, whose top items are the markers with their self time and calls; the
  children of its `GC.Alloc` item are the callers with the bytes they allocated; `frameTimeMs` is the
  frame. The profiler keeps the last 2000 frames (Preferences > Analysis > Frame count): older ones are
  gone, and the answer says how many it analysed (`analysed the last 2000 of 2712 frames`). A
  batch-mode editor hands frames over late, so the request waits until the last frame index stops
  moving (at most 2 s). If a domain reload ends the request (a compile, play mode entered), the next
  bridge start turns the profiler back off when `--record` had turned it on (a `SessionState` flag).
- **Frame cap.** `--record` stops at `--max-frames` frames (10 to 2000; default 300, the Profiler
  window's default history) when that comes before the time asked, and the header says so (`300
  frames in 0.0 s (the 300-frame cap: --max-frames, up to 2,000)`). Frames are counted as editor
  updates, since the request is polled once an update and a batch-mode editor hands its frames over
  late. The reason is memory: the profiler's pool (Unity's `ProfilerEditor` label) grows with the
  frames it holds and is not given back while the editor runs (`ProfilerDriver.ClearAllFrames`
  empties the frames, not the pool; later recordings reuse it). Measured in BridgeHost's batch-mode
  editor, which updates about 21,000 times a second: one 0.3 s recording held 2000 frames, the
  profiler's memory peaked at 3.1–3.5 GB and settled at 1.3 GB. Three such recordings in the
  EditMode suite, on a machine with 2 GB of commit left, made Unity abort (`Could not allocate
  memory ... ProfilerEditor`). With the cap the suite passes. The cap binds in an interactive
  editor too: purge-squad's window updated about 2,000 times a second while `ug` waited, so 300
  frames took 0.2–0.3 s and `--max-frames 2000` about 1 s; a longer window needs `ug perf profiler
  start` or the Profiler window. `profiler start` has no cap: a session the user starts and stops
  is the Profiler window's behaviour, and its answer gives the frames held.
- **`--frames a-b|all`** analyses frames the profiler holds, from any recording (the Profiler window's
  too). Refused outside what it holds (`the profiler holds frames 3100-5099`) or with none.
- **Answer** of both, `--top` 1 to 200 (default 10 markers, 5 callers, 3 spikes):

  ```
  $ ug perf --record 5 --max-frames 2000
  play mode, 1312 frames in 5.0 s, main thread: median 3.6 ms (277 fps), p99 6.1 ms, max 14.2 ms; GC 1.2 KB a frame
  self ms a frame, share, calls a frame:
    EditorLoop  1.51  41%  1  (editor, not in a build)
    Gfx.WaitForPresentOnGfxThread  0.62  17%  1
    ...
    ... 412 more: ug perf --frames 2201-3512 --top 30
  GC allocated a frame, by caller:
    PlayerController.Update()  0.8 KB  (in 1312 frames)
  spikes over 7.2 ms (2x median): 4 frames; slowest:
    frame 3011  14.2 ms  GC.Collect 8.1, BehaviourUpdate 2.0, Physics.Simulate 1.1
  ```

  In edit mode the frames are the editor's own (`edit mode (the editor's frames)`); in play mode
  `EditorLoop`'s self time is the editor drawing itself, labelled as not in a build. The `more`
  line is `--frames` over the same frames, so it reads them again instead of recording again.
- **`profiler`** reports and drives the session. `status` (the default):

  ```
  profiler  recording play mode frames since frame 1200 (holds 1200-3199)
  file  Library/unitygraph/profiler/20261011-101500.raw  26.9 MB
  allocation callstacks  off
  areas on  CPU, Rendering, Memory, Audio, Video, Physics, Physics2D, NetworkMessages, NetworkOperations, UI, UIDetails, GlobalIllumination, VirtualTexturing
  areas off  GPU
  ```

  `start` sets `ProfilerDriver.enabled` (and `profileEditor` as `--record` does; `--editor` profiles
  the editor in play mode too, the window's Edit Mode target). `--file` writes Unity's binary log
  (`Profiler.logFile`, `enableBinaryLog`), which the Profiler window loads: a path relative to the
  project or absolute, `.raw` added when there is no extension; without a path
  `Library/unitygraph/profiler/<yyyyMMdd-HHmmss>.raw`. `--callstacks` sets
  `Profiler.enableAllocationCallstacks` (GC.Alloc samples carry call stacks; slower). Already
  recording: `start --file` adds the file, a plain `start` is refused. `stop` ends the log, turns
  callstacks and recording off, puts `profileEditor` back, and answers the time, the frame range
  with the `--frames` command that reads it, and the file and its size. Frames arrive late (batch
  mode most), so the answer waits until the last frame index holds still for 0.1 s (at most 2 s
  after stopping) and the range is the one the profiler holds then. `areas` lists the
  `ProfilerArea`s, or sets them (`Profiler.SetAreaEnabled`, which the Profiler window shares):
  names case-insensitive, `on|off|true|false`; an unknown name is refused with the list.
- **`counters`** reads `ProfilerRecorder`s. Without names or `--category`: the categories, with
  how many counters (`MarkerFlags.Counter`) and markers each has. `--category Render`: every counter
  of it with its value; `--markers`: the category's markers (time in the last frame) instead,
  slowest first, the idle ones counted. Names are counter names in any category (`Draw Calls Count`,
  `Total Used Memory`), case-insensitive; an unknown or partial name lists the near ones. The
  recorders run for 3 frames (play mode, or the editor's updates queued in edit mode; at most 2 s) and
  the value is the last sample. Units by `ProfilerRecorderDescription.UnitType`: bytes as KB/MB/GB,
  nanoseconds as ms, counts, percent, Hz. Lists cap at 40 (`... N more: ug perf counters --category
  Render --all`). Render counters are 0 in edit mode unless something draws.
- **`timing`**: `FrameTimingManager` over 5 frames (the first reading after an idle editor is the
  idle gap), the last frame's 12 fields: CPU frame, main thread, present wait, render thread and GPU
  ms; first submit, present called and frame complete as ms after the frame start; the frame start
  timestamp with the timer frequency; width and height scale; sync interval. A GPU time of 0 says
  `not measured`. Refused when `FrameTimingManager.IsFeatureEnabled()` is false, naming the player
  setting (`Frame Timing Stats`).
- **`memory`**: every loaded object (`Resources.FindObjectsOfTypeAll<Object>()`) sized by
  `Profiler.GetRuntimeMemorySizeLong`: the total, the 8 largest types (size and count), and the top
  objects with type and where they live: an asset path, a scene address, `hidden` (HideFlags: the
  editor's or the engine's own) or `not saved` (made by code). `--type` keeps one type and its
  subclasses (`Texture` takes `Texture2D` and `RenderTexture`). `--top` 1 to 500, default 10.
  `--object <x>` sizes one object: a game object with its components each, its children's total, and
  the assets it uses (`EditorUtility.CollectDependencies`, persistent ones, largest 5); an asset path
  with its sub-assets (largest 20). Sizes are native memory; managed memory is in the snapshot.
- **`snapshot take`**: `Unity.Profiling.Memory.MemoryProfiler.TakeSnapshot(path, callback,
  ManagedObjects | NativeObjects | NativeAllocations)` (the Memory Profiler package's default
  capture, without the package), waiting for the callback (at most 60 s). Default
  `Library/unitygraph/snapshots/<yyyyMMdd-HHmmss>.snap`; `.snap` added when there is no extension.
  Answers path, size and time. Unity can call back before the file is flushed, so the answer also
  waits for the file's end signature (`0xABCDCDAE`, below; at most 20 s past the callback, else
  refused with the size so far). Snapshots are big: 183 MB in 0.6 s for BridgeHost in batch mode,
  1.81 GB in 10–17.5 s for purge-squad with its window open.
- **`snapshot list`** (client): `Library/unitygraph/snapshots/` and `MemoryCaptures/` (the package's
  folder), or `--folder` (MCP's default, `Application.temporaryCachePath/MemoryCaptures`, is reached
  this way), newest first, at most 20 (`--all`): path, size, capture time, product, Unity version,
  platform, total used memory and native object count, read from the file. A file ug cannot read
  says why.
- **`snapshot compare <a> <b>`** (client): ug's own reader of the `.snap` format (below), so no
  `com.unity.memoryprofiler` in the project and no Unity running. Answers both files' metadata;
  memory totals with changes (total used, graphics, GC heap used and reserved, audio, profiler);
  native objects count and size; types by size change; objects new in `b`, gone from `a` and grown,
  largest first. Two snapshots of the same editor session (equal session GUID) match objects by
  instance ID; otherwise by type and name, in order. `--top` 1 to 200, default 10. Managed objects
  are counted in the GC heap only: crawling the managed heap is the Memory Profiler package's work.
- **`frame-debugger`** drives Unity's internal Frame Debugger by reflection
  (`UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility`: `locallySupported`, `count`,
  `limit`, `SetEnabled(bool, int)`, `GetFrameEvents`, `GetFrameEventInfoName`, `GetFrameEventData`,
  `GetBatchBreakCauseStrings`; `UnityEditor.FrameDebuggerWindow`). The names are Unity 6000.3's and
  are checked on each request: a missing one refuses (`the Frame Debugger's internals are not the
  ones ug knows (no FrameDebuggerUtility.count in this Unity)`). `enable` is refused where Unity
  cannot debug frames locally (`locallySupported` false: an editor rendering single-threaded, as
  `-batchmode` and `-automated` start it; the refusal says to start Unity with `-force-gfx-mt`).
  When the Frame Debugger window is open its own enable, disable and event limit are used (calling
  the utility under an open window makes it throw), else the utility, and no window is opened. Unity
  pauses play mode while debugging, as from the window; the answer says so and gives the event count
  once it settles (at most 5 s). Unity captures the next frame the Game or Scene view draws, and an
  editor in the background draws none (measured: a windowed editor behind another application held
  0 events until its window was clicked, then 4), so `enable` with no events yet says to click the
  editor's window, and `events` with none is refused with the same hint. `events`: index, event
  type, Unity's event name and the object (an address or asset path), from `--from` (default 0),
  `--top` (default 50, at most 500), `... N more: ug perf frame-debugger events --from 50`.
  `events <n>`: sets the limit to that event (the Game view shows the frame up to it, as clicking it
  in the window) and answers its detail: shader (real and original when they differ), pass and
  light mode, keywords, mesh, vertex, index, instance and draw call counts, render target name, size
  (not twice when Unity's name carries it: `TempBuffer 14 613x428`), format and count, compute
  shader and kernel, and the batch break cause. `disable` ends it; play mode stays paused. `events`
  and `disable` are refused while it is off.
- **Files.** ug creates `Library/unitygraph/profiler/` and `Library/unitygraph/snapshots/` (Library
  is never in version control) and never deletes what it writes there; answers give sizes. A path
  given is refused under the project's `Assets/` or `Packages/` (Unity would import a growing file),
  when the file exists, and when its folder does not.
- **State, not data.** Profiler recording, areas and the Frame Debugger are editor state: not on the
  Undo stack, nothing saved or marked dirty; the commands that change them say how to put them back.
- **Older bridge.** Before 0.8.0 the `/perf/...` requests are unknown: `bridge 0.7.0 is too old for
  ug perf --record: run ug bridge install again`. Plain `ug perf` works with any.

**The `.snap` format** (read by `UnityGraph.EditorLink`, written by Unity 2022.2 to 6000.3; checked
against the Memory Profiler package 1.1.12 sources): a file starts with `0xAEABCDCD` and ends with
`0xABCDCDAE` after a `long` directory offset. The directory (`0xCDCDAEAB`, version `0x20170724`, the
block section offset) lists one offset per entry type; the block section (`0x20170724`, block count,
offsets) gives each block's chunk size, total bytes and chunk offsets, and a block offset maps to the
chunk holding it. An entry's header (format, block, entries meta, header meta; 18 bytes packed) makes
it one value, an array of fixed-size elements, or of sized elements (strings: UTF-8). ug reads:
`Metadata_Version`, `Metadata_RecordDate`, `NativeTypes_Name`, `NativeObjects_NativeTypeArrayIndex`,
`_Flags`, `_InstanceId` (8 bytes from format 18), `_Name`, `_Size`, `ManagedHeapSections_Bytes`,
`ProfileTarget_Info` (session GUID, platform, Unity version, product) and `ProfileTarget_MemoryStats`.
A file without the signatures, or a format version above the newest ug knows, is refused by name.

Measured on purge-squad (2026-10-11, Unity 6000.3.14f1 open with its window, URP 17.3.0, bridge
0.8.0 copied in; fresh `ug` process, wall clock, answer bytes): `--record 1` 2.1–4.4 s, 1.9–2.1 KB
(about 500 tokens), 299 frames in 0.2–0.3 s ended by the cap, median 0.46–0.64 ms (1,550–2,170
fps); `--record 1 --max-frames 2000` 7.4 s, 2.1 KB, 1,151 frames in 1.0 s; the time past the
recording is the analysis, sliced across editor updates. The profiler's own memory (`Profiler Used
Memory`) was 2.10 GB before and 2.10–2.11 GB after three recordings. `profiler start --file` 208
ms; `stop` 2.3 s after 1 s of recording (`.raw` 26.4 and 35.4 MB) and 9.7 s after 3 s (11,000
frames): Unity drains the frames it queued on the main thread, and the ranges answered were the
ones it held afterwards. `profiler` (status) 217 ms, 261 B. Counters: categories 211 ms, 1.3 KB;
four Memory counters 198–258 ms, 204 B; the Render category 219 ms, 1.3 KB. `timing` 201 ms, 364
B. `memory` 239 ms, 1.1 KB (12,521 objects, 391.1 MB; RenderTexture 230.2 MB in 22); `--type
Texture2D` 192 ms, 604 B; `--object Boot` 314 ms, 768 B. `snapshot take` 1.81 GB in 17.5 and 10.0
s; `snapshot list` 206 ms, 312 B; `compare` of the two 246 ms, 958 B (240 tokens), the object made
between them found new. `frame-debugger enable` refused in 179 ms, 153 B: the Hub starts
purge-squad with `-automated`, which renders single-threaded. The Frame Debugger live, BridgeHost
with its window and `-force-gfx-mt`: `enable` 473 ms (4 events), `events` 202 ms, 359 B, event 1
(`Skybox/Procedural`, 5,040 vertices) 203 ms, 272 B, event 3 (`Hidden/BlitCopy` to a `TempBuffer`)
192 ms, 264 B, `disable` 185 ms; in play mode `enable` 507 ms paused the game, `disable` 193 ms
left it paused, and `ug stop` found 0 errors. In the `UG_UNITY` end to end (batch mode, in
process): `--record 1` 416 ms, about 390 tokens; `--frames` over a stopped session 1.13 s; two
counters 13 ms; `memory --top 5` 11 ms, about 157 tokens; `snapshot take` 645 ms (183 MB);
`compare` 24 ms, about 223 tokens; a 0.3 s `.raw` log 54.5 MB.

## Nothing out of reach (C1, bridge 0.9.0)

User rule (2026-10-10): full capability, nothing left out to save tokens; answers stay compact and
name the command that shows the rest. An audit (2026-10-11) found caps whose cut part no `ug`
command could reach. Each now has an escape; default answers keep their caps and size. The new
flags need bridge 0.9.0 and are refused by name on an older one (SPEC-editor-link, Bridge 0.9.0).

- **`ug inspect <object> --field <path>`** reads one serialized field whole. The component is
  found as `ug set` finds it (`--component`, else the game object, then the one component with the
  field); `--importer` reads an importer setting, and on a material `<path>` is a shader property.
  `<path>` is `ug set`'s (`waves`, `waves[2].count`, `m_LocalPosition`). Answer: the head line
  (`Level/Spawner  WaveSpawner  waves  Wave[12]`), then for an array or list one line per element,
  `  [3]  {count: 5, delay: 1.5, enemy: Assets/Prefabs/Grunt.prefab}`, else one line with the
  value. Values are whole: lists, curve keys and gradient keys all, structs nested to any depth
  (Unity's serialization depth bounds them; a cycle of `SerializeReference` objects stops at 32
  levels, a memory bound), strings whole with `\n`. At most 200 elements per answer (a transport
  bound); past them `  ... 300 more: ug inspect Level/Spawner --component
  WaveSpawner --field waves --from 200`. `--from <n>` starts at element `n`.
- **Field and property lists page.** A component's fields and a material's properties stop at 150
  lines as before, now with `  ... 12 more fields: ug inspect <object> --component <C> --from 150`
  (a material: `more properties`; materials had no `more` line). `--from <n>` starts at field `n`.
- **Cut values are said.** Arrays, lists and curve keys still show 10 items and structs one level
  (`{...}` deeper); gradients now say `... N more` past 10 stops too (they were cut silently). When
  any value of a field list was cut, the last line is `  (values cut: ug inspect <object>
  --component <C> --field <name>)`. The overview's children list ends `, ... N more (ug tree
  <object> --depth 1)`.
- **`ug prefab overrides <instance>`** lists an instance's overrides, read-only (`/prefab/overrides
  ?object=`): the head `Level/Crate  instance of Assets/Prefabs/Crate.prefab  4 overrides`, then one
  line per override: `  Level/Crate/Lid  Hinge.limits.max = 90` (an object reference as `->
  <address or path>`), `  Level/Crate  added Rigidbody`, `removed BoxCollider`, `  Level/Crate/Tag
  added object`, `removed object Barrel`. Root name, position and rotation, which every scene
  instance overrides, are left out, as in apply. Every override, no cap; `no overrides` when none.
  `ug inspect` on the instance root shows the same count in its head (`in prefab instance of
  Assets/Prefabs/Crate.prefab (4 overrides)`); it used to count objects with overrides instead of
  overrides, so O10 saw `1 override` beside a list of 4 (fixed in bridge 0.10.0).
  `ug prefab apply|revert` keep their 40-object report, now ending `... N more objects (ug prefab
  overrides <instance> lists every override before an apply or revert)`. MCP parity: MCP for
  Unity's `manage_prefabs` has no override listing (its actions are `get_info`, `get_hierarchy`,
  `create_from_gameobject`, `modify_contents`), nor has `unity-editor-mcp` (`apply_prefab_overrides`,
  `revert_prefab_overrides` only), so this goes beyond both.
- **`--all`** on `ug find --live` (every match, not 100), `ug select` (every selected object, not
  20), `ug run --list` (every line, not 40) and `ug api` (every member name, constructor, overload,
  candidate of an ambiguous name and enum value: no name budgets, no 25-overload or 4-constructor
  cap). Each cut answer names its `--all`. `ug tree` already names how to reach the rest (a
  narrower object or `--depth`).
- **`ug docs <page> --budget <t>`** (200 to 8,000, default `answerBudget`): the cut line becomes
  `... cut to 1200 tokens: ug docs <page> --budget 2400; the whole page: <file or URL>`.
- **`ug perf memory --object <x> --top <n>`**: `--top` now also sets how many sub-assets (default
  20) and used assets (default 5) are listed, and their cut lines name it (`... 31 more sub-assets:
  ug perf memory --object Assets/Hero.fbx --top 60`, `uses  12 assets, 9.1 MB: ..., +7: ug perf
  memory --object Player --top 15`; bridge 0.9.0). Without `--object`, `--top` also sets how many
  types the `by type` line names (default 8; its cut reads `+31 types (ug perf memory --top 24;
  --type <T>)`). The top-objects list stays at
  most 500 (`--top`'s range); at 500 its `more` line says `ug perf memory --type <T> narrows them
  (500 is --top's most)`. `ug perf --record` and `--frames` add one line after the spikes when a
  shown spike frame has more than its top 3 markers: `  a spike's other markers: ug perf --frames
  3011-3011` (the slowest spike).
- **Refusals that list choices** list them whole or name the command that does: enum names,
  shader properties (`ug set` on a material), a type's methods (`ug event add`), gamepad buttons
  and `SerializeReference` types were cut silently and are now whole; ambiguous addresses and
  elements past 10 (5 for `ug click`) name `ug find --live <name>`, `ug ui --all` or `ug tree --ui`.
  An unknown key's five example keys end `... (every key: ug api UnityEngine.InputSystem.Key
  --all)`; an unknown counter's names past 10 end `... N more: ug perf counters --category <c>
  lists a category's`. Near-name suggestions (`; near: ...`) stay at 5: they are guesses, not a list.
- **Caps that stay, as memory or transport bounds:** the console ring (2,000 entries) and its
  per-entry text and stack (8,000 characters each); the request body (16 MB) and the server's
  64 queued requests; shot sizes (2,048 px, sheets 4,096 px); the profiler's recorded frames (2,000);
  `ug perf memory --top` (500); 200 elements per `--field` page; the result file's walk (1,000
  items, two levels, 200 members); the play digest's object snapshot (20,000 objects:
  SPEC-play-input, Digest). Each says where it cut.

## Play-mode debugging (I5, bridge 0.13.0)

Requested by the user on 2026-10-11: stop the game where a problem starts, then read it with
`ug inspect`, `ug tree`, `ug eval` and `ug play --step`. Play mode only, nothing saved, answers in
the digest's shape. `ug play; ug pause` cannot do it: it lands a few frames late (purge-squad's
Boot has already started loading GalaxyMap), and an error scrolls past while the game runs on.

- **`ug play --paused`** enters play mode stopped on the first frame. The bridge sets
  `EditorApplication.isPaused` before `EnterPlaymode`, as Unity's Pause button pressed in edit mode
  does: Unity runs the first frame (`Awake`, `OnEnable`, `Start`, the first `Update`) and pauses
  (purge-squad, 6000.3.14f1: `Time.frameCount` 1, `Time.time` 0, Boot loaded; `ug play --step 2`
  then shows `scene +GalaxyMap` on frame 3). After the play wait the client asks `/play/state`:

  ```
  $ ug play --paused
  paused on frame 1 (entered in 6.2 s)
    loaded: Boot (active, 3 roots, 41 objects), DontDestroyOnLoad (2 roots, 9 objects); 1 error
    error  NullReferenceException: Object reference not set to an instance of an object  Assets/Scripts/Boot.cs:12
  ```

  `loaded:` names the loaded scenes in hierarchy order, then any other scene holding objects
  (DontDestroyOnLoad), each with its root and game object counts (objects hidden in the hierarchy
  are left out, as in the digest). The console part counts the errors and warnings logged since
  play mode started, with up to two error rows and `+N more errors: ug console --after <id>`, as
  the digest does. `ug play --step`, `ug inspect`, `ug tree` and `ug eval` then work as on any
  pause; `ug play` resumes, `ug stop` ends it (and Unity's pause goes off with it). While paused,
  `ug play --paused` changes nothing and answers the same state lines (where it is paused, and
  why); while playing it is refused: `already playing: ug pause pauses here; ug stop first to
  start again on frame 1`. `--paused` with `--for` is a usage error.
- **`ug play --pause-on-error`** arms an error pause: the bridge pauses the editor at the first
  error, assert or exception logged in play mode after it (the console's `error` type), once. On
  the main thread it pauses inside the log call, as Unity's Console Error Pause does, so the rest
  of that frame runs and no further frame; an error logged from another thread pauses on the next
  editor update. The arm survives the domain reload of entering play mode (SessionState), so an
  error in `Awake` counts; it ends when it fires or play mode ends. Unity's Console Error Pause
  setting is never read or changed, so there is nothing to restore; with it on, Unity also pauses
  at every error, as before. From edit mode `--pause-on-error` enters play mode; with `--paused`
  too it starts on frame 1 armed, so `ug play --step` stops at the first error. While playing it
  arms and resumes a pause, as `ug play` resumes; with `--paused` too it arms and stays paused.
  The answer is `/play/state`'s:

  ```
  $ ug play --pause-on-error
  playing on frame 3; pauses at the first error (entered in 6.4 s)
    loaded: Boot (active, 3 roots, 41 objects)

  (when it has fired, here or for a later --paused or --for)
  paused at the first error, frame 214, 3.5 s in
    #4105 error  NullReferenceException: Object reference not set to an instance of an object  Assets/Scripts/Enemy.cs:42
    more  whole: ug console --id 4105
  ```

  `frame` is `Time.frameCount` and `s in` is `Time.unscaledTime` when it fired; the row is the
  entry's first line (160 characters, as the digest's rows) and its own frame. The id is the
  entry's current one: a repeat folded into it later in that frame gives it a new id (then `x3`
  shows), and `ug console --id` takes that one. The answer keeps the error until the editor
  resumes.
- **`ug play --for <s> --pause-on-error`** plays as `--for` does but polls `/status` every 250 ms
  instead of sleeping. When the editor pauses (the error, or someone pausing it) it prints the
  `/play/state` lines and leaves the game paused: no stop, no log digest. Otherwise at `s` it stops
  and prints the `ug log` digest as before (`--whole` applies to that digest).
- **Actions while armed.** An action running when the pause fires (`press`, `click`, `wait`) ends
  there: it is released at once, and its digest's play part reads `paused at the first error
  #4105 (frame 214)` after the console part that shows the error row. Any pause under a running
  action now ends its hold the same way (play part `paused`): an action that waits for frames used
  to wait out its client's timeout and keep the queue busy. `ug play --step <n>` stops at the frame
  that paused: `stepped 3 frames of 10 (frame 217): the first error stopped it`.
- **Nothing left behind.** `ug stop` ends the pause; leaving play mode drops the arm and the error
  kept. A play start that Unity never begins (no play mode within 10 s) takes back the pause and the
  arm `ug play` set, so the user's next Play is not paused or armed by `ug`.
- **Requests.** `/play?paused=true&onerror=true` (either or both; without them `/play` is as
  before). `/play/state` answers the state lines above; it waits for the first frame to have run
  (at most 5 s) and is refused in edit mode with `not playing: ug play first`. The client asks
  `/play/state` after `/play` (and its wait) whenever `--paused` or `--pause-on-error` is given,
  and after `--for` sees a pause. An older bridge refuses the flags by name before any request:
  `bridge 0.12.0 is too old for ug play --paused: run ug bridge install again`. `ug batch` takes
  them as steps.
- **Usage errors** (exit 1): `--paused and --for cannot be used together`, `--step cannot be used
  with --paused or --pause-on-error` (`ug play --paused --pause-on-error`, then `--step`).

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

## Three-way measurement (O10)

The measured reason the Objective asks for. The question (user, 2026-10-10) is which editor side
to keep: A. `ug` does everything (current); B. `ug` graph only + MCP for Unity; C. `ug` graph only
+ Unity's CLI; D. hybrid, `ug` commands served through Unity's Pipeline (`[CliCommand]`), dropping
our bridge and transport. Decision rule agreed that day: if `ug` is not clearly cheaper than the
best other leg (about 25% fewer tokens at equal correctness), the report says D with the numbers
and D is not started. The choice is the user's.

**Legs.**

- `ug` (bridge 0.9.0 copied into purge-squad), from Bash.
- Unity's CLI, `unity` 1.0.0-beta.8 with `com.unity.pipeline` 0.7.0-exp.1 (in purge-squad's
  manifest since the user's commit 1a422d2), two ways in: `unity command <name> --<param> <value>`
  from Bash (the `unity:unity-cli` skill's syntax; beta.8 rejects the skill's `--caller` and
  `--skill` flags, so they are left off), and `unity mcp`, the user-level MCP server
  `unity-editor-mcp`. Both reach the same editor commands.
- MCP for Unity (CoplayDev): its Unity package comes from a git URL and its server runs under
  `uv`, both a network use and a package add, so it is installed only on the user's yes. Installed
  on that yes (2026-10-11): `com.coplaydev.unity-mcp` 10.3.0 by `ug package add` of
  `https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#v10.3.0`; the editor starts its
  server (`mcpforunityserver` 10.3.0 from the `uv` cache) over HTTP on `127.0.0.1:8080`. Driven by
  a small MCP client over that HTTP transport, not registered in Claude Code.

**Chores** on purge-squad's open editor (`Assets/Scenes/Boot.unity` open, nothing saved):

1. Find the progress bar `Bar` and read its live `ProgressBar` values.
2. Set `Boot/AdOverlay/Bar` `ProgressBar._value` to 0.5 and read it back.
3. Create a cube `Probe` under `Boot` at 1,2,3 with a `Rigidbody`.
4. Instantiate `Assets/Prefabs/UI/ButtonPrimary.prefab`, override a value on the instance, list
   the instance's overrides, revert them. (Apply writes the prefab asset, which purge-squad's
   unchanged `git status` rules out; revert is the same call shape.)
5. Enter play mode, read the console errors that are new, stop.
6. Break a script (a missing name), recompile, read the error, put the file back, recompile
   clean.
7. A game-view picture and a viewpoint of `Probe` from 4,4,-4.
8. Run the EditMode tests and read the failures.
9. One second of play-mode profiling with the top markers.
10. In play mode, click `DEPLOY` on the galaxy map and confirm the screen changed (the loading
    window opens on the way to `Arena_Greybox`), stop.
11. Graph, the editor not needed: which prefabs and scenes use `ProgressBar`. Only `ug` answers
    from the graph; the other legs fall back to Bash (find the script's GUID, grep the YAML).

**Method.** Deterministic first: per leg and chore, the call sequence an informed agent would use
(the leg's own command when it has one, its `eval` when it has none, counted like any call), run
for real; recorded are calls, request + answer bytes / 4 (a Bash call: the command line and its
output; an MCP call: the tool name, the arguments' JSON and the result's text), wall time, and
correctness, checked by a `ug` read after the chore. Pictures are counted by their answer text;
the PNG Claude reads costs the same image tokens on every leg at the same size. Polls (tests,
recompile, waits) count as calls. Idle cost per leg: `ug`'s skill description and SKILL.md body;
each MCP server's `tools/list` (bytes / 4 of the full schemas) and its deferred names
list; the `unity:unity-cli` skill's description and body, which Claude reads before a `unity`
call. Before numbers: `ug stats` on purge-squad's past sessions. Between legs every edit is undone
(`ug undo` until the scene is unmodified); `git -C D:\Projects\Unity\purge-squad status` is the
same before and after (the bridge copy is uncommitted there and stays). The Frame Debugger is
refused in the user's `-automated` editor and is left out.

Optional, on the user's yes only (it spends their usage): the same chores as `claude -p` runs, one
leg at a time (`--strict-mcp-config`, the other legs' tools disallowed), tokens and tool calls read
from the JSON output, to see whether a thin toolset pushes Claude to `eval`.

### Results (2026-10-11)

Per chore: calls / tokens (bytes / 4) / seconds. A Bash `sleep N` an agent needs between calls
counts as one call of 7 bytes. The Edit tool's break and fix in chore 6 is the same on every leg
and left out.

| chore | `ug` | `unity-editor-mcp` | `unity command` | MCP for Unity |
|---|---|---|---|---|
| 1 find, read | 2 / 80 / 0.4 | 2 / 399 / 0.5 | 2 / 370 / 2.0 | 2 / 151 / 4.7 |
| 2 set | 1 / 36 / 0.7 | 1 / 89 / 0.3 | 1 / 131 / 1.0 | 2 / 165 / 4.9 |
| 3 cube | 1 / 144 / 0.5 | 1 / 396 / 0.5 | 1 / 409 / 1.0 | 1 / 218 / 5.4 |
| 4 prefab | 1 / 249 / 0.4 | 4 / 342 / 0.9 | 4 / 550 / 4.7 | 4 / 468 / 14.1 |
| 5 play errors | 1 / 19 / 27.5 | 9 / 468 / 21.4 | 7 / 510 / 11.1 | 8 / 986 / 8.5 |
| 6 compile | 3 / 80 / 24.3 | 4 / 180 / 7.7 | 6 / 395 / 6.9 | 4 / 298 / 40.0 |
| 7 pictures | 6 / 410 / 1.4 | 2 / 297 / 0.2 | 2 / 527 / 2.2 | 2 / 217 / 0.6 |
| 8 tests | 1 / 9 / 4.6 | failed | failed | not run (saves scenes) |
| 9 profile | 3 / 338 / 8.7 | 8 / 673 / 6.9 | 7 / 1,163 / 12.8 | 8 / 1,442 / 10.5 |
| 10 click | 6 / 167 / 15.2 | 11 / 523 / 6.7 | 9 / 665 / 12.9 | 11 / 1,327 / 10.5 |
| 11 graph | 1 / 419 / 0.4 | 2 / 157 / 1.3 (grep) | 2 / 157 / 1.3 (grep) | 2 / 157 / 1.3 (grep) |
| **all but 8** | **25 / 1,939 / 79.5** | **44 / 3,524 / 46.4** | **41 / 4,877 / 55.9** | **44 / 5,430 / 100.4** |

Idle cost, tokens:

| leg | always in context | on first use |
|---|---|---|
| `ug` | skill description 25 | SKILL.md body 600 |
| `unity-editor-mcp` | 151 deferred names 1,509 (641 without the `mcp__unity-editor-mcp__` prefix) | schemas of the 19 tools used 2,927 (all 151: 24,023 when not deferred) |
| `unity command` | skill description 166 | skill body 8,977 |
| MCP for Unity | 49 deferred names 372 (188 without the `mcp__UnityMCP__` prefix); skill description 91 | schemas of the 9 tools used 5,774 (all 49: 28,711 when not deferred); skill body 3,543, tools reference 15,157 |

A session doing these ten chores, chores plus idle: `ug` 2,564, `unity-editor-mcp` 7,960,
`unity command` 14,020, MCP for Unity 11,576 (names and used schemas; 15,210 with its skill
description and body).

**Correctness**, each chore checked by a `ug` read (values, `Probe`'s transform and `Rigidbody`,
no overrides after revert, the picture files, the active scene, the profiler's top markers):

- All four legs did chores 1-7 and 9-11 right.
- Chore 8 failed on both Pipeline legs, in the user's `-automated` editor. Pipeline's `run_tests`
  listed 167 tests and never ran one.
  - The synchronous form timed out after 360 s. Every later Pipeline command then timed out until
    a domain reload (`EditorUtility.RequestScriptReload` through `ug eval`).
  - The async form returned "Total 0" (CLI) or never returned (MCP, 7 min 47 s). `test_status`
    stayed `running` for 45 polls, and `cancel_tests` cleared it.
  - `ug test` ran the 167 tests green in 4.6 s.
- Chore 6, `ug`: the second `compile` ran into the domain reload and answered "Unity not reachable
  (Unity is busy)" after 20.6 s. A third `compile` answered clean. All three calls are counted.
- Chore 6, CLI: `unity command recompile` answers `compiling` straight away, so the CLI leg polls
  `recompile_status`. MCP's `recompile` waits and answers with the errors.
- Chore 7, `ug`: `shot --from --look` copies the scene camera. In `Boot` that camera's culling
  mask is Nothing, so the picture was blank. It took 4 more calls: inspect, set the mask, shot,
  undo. The other legs' `eval` made a new `Camera`, whose default mask renders everything.
- Chore 10, MCP: on the first run, `list_open_scenes` still showed `GalaxyMap` 3 s after the
  click, a timing miss. The rerun is the recorded one. Its active-`LoadingWindow` check found
  nothing; `Arena_Greybox` confirmed the change.
- Chore 11:
  - `ug impact ProgressBar` names the 6 assets (4 prefabs, 2 scenes) and the field that holds
    each. `ug explain` names only `ProgressBar.prefab`, because the instances live in other assets.
  - Grepping the script's GUID finds the same 6 here only because every instance has a stripped
    stub carrying `m_Script`. A complete fallback adds the prefab-GUID hop: 2 more calls, 142 more
    tokens.
  - Unity Search `ref:` (MCP, 1 call, 555 tokens) missed `MapNode.prefab` and
    `ProgressBar.prefab`, and added 2 transitive users.
  - Grep was cheaper than `ug` on this chore.
- MCP for Unity:
  - Chore 8 was not run. Its `run_tests` saves every modified scene before a run
    (`TestRunnerService.SaveDirtyScenesIfNeeded`), which would have saved the user's unsaved
    `Boot.unity`.
  - Its five edits (set, create, create, set, revert) landed in one undo group: one `ug undo`
    (one Ctrl+Z) reverted them all, under the name "Revert Prefab Instance".
  - It has no overrides list, prefab revert or profiler marker read: chore 4's list and revert,
    chore 9 and chore 10's click are `execute_code` (CodeDom compiler). `manage_profiler
    profiler_start` sets `Profiler.enabled` and keeps the old frames.
  - `find_gameobjects` answers instance IDs only, so names take a resource read per object.
  - `mcpforunity://editor/state` is refreshed only on play and compile events. Its
    `ready_for_tools` stays false ("stale_status") and `is_changing` stays true through play
    mode. The first run polled `is_changing` and hit its 60-poll cap on every play; the recorded
    rerun polls `is_playing`. The first read after entering play fails with "Unity session not
    ready" after 6 s.
  - Wall time: each edit-mode `manage_*` call took about 5 s; `refresh_unity` waits 11.8 s on a
    compile error and 28.1 s through the domain reload.
  - The install added its line to `Packages/manifest.json` (Unity also moved
    `com.unity.pipeline` into sorted order) and to `packages-lock.json`. At start it logged
    "refreshed 5 client config(s)"; no client config file changed.
- Wall time: `ug` was slower than both Pipeline legs.
  - Chore 5: `play --for 3` took 27.5 s, because entering play mode varies from 6 to 26 s in this
    project.
  - Chore 6: the busy `compile` above.

**Before numbers**, `ug stats` on purge-squad (all time: 45 sessions, 3,866 turns, 4,630 tool
calls). Editor work was 36.8% of context:

- `eval`/`run_script`: 663 calls, 14.6%;
- compile, refresh and status: 5.0%;
- reading Unity YAML: 5.7%;
- console: 3.3%;
- play and screenshot: 3.2%;
- other MCP: 2.2%;
- tests: 2.1%;
- other CLI: 0.7%.

**Verdict against the rule.**

- On the chores, `ug` used 45% fewer tokens than the best other leg, `unity-editor-mcp`: 1,939
  against 3,524. With idle cost counted it used 68% fewer: 2,564 against 7,960. It also made 43%
  fewer calls.
- Against MCP for Unity, `ug` used 64% fewer tokens on the chores (1,939 against 5,430) and 78%
  fewer with idle cost (2,564 against 11,576).
- Its correctness was equal or better: both Pipeline legs failed chore 8, and MCP for Unity could
  not run it without saving the scene.
- The rule does not point to D. On these numbers, A holds over B, C and D.
- The `claude -p` runs were not made; they are open on the user's yes. The choice is the user's.

### Rooms (2026-10-11)

The user's follow-up: the same behaviour on every leg, playing a couple of rooms to completion.

**Setup.**

- Each run starts in edit mode with `Boot` open and goes on to two cleared rooms:
  1. Enter play mode and click `DEPLOY` on the galaxy map.
  2. Every 3 s, read the run's state: room, rooms cleared, gate, HP, level-up pick open, run
     ended, enemies.
  3. If the pick is open, click the first boon card. If the gate is open, walk into it.
  4. Stop once two rooms are cleared.
- A script drives the same policy on every leg, through that leg's own calls:
  - `ug`: `play`, `click`, `press W --hold 4.5`, `eval`, `stop`.
  - `unity command` and `unity-editor-mcp`: `editor_play`, then `editor_status` polled until
    playing; `eval` for the `DEPLOY` click, the card click and the gate walk (RunDirector's
    `Player.WalkToAsync`); `editor_stop`.
  - MCP for Unity: `manage_editor`, `editor/state` polled, `execute_code`.
- The player fires on its own; standing still cleared every room.
- A room clear writes the game's save (`profile.json` under LocalLow), so each run started from a
  backed-up copy of it, and the copy was put back after. purge-squad's `git status` was unchanged
  after every run.
- Three runs per leg in each poll form. The fights are random, so poll counts differ from run to
  run.

**Poll forms.**

- Inline: the state snippet (about 700 bytes) is sent with every poll.
- File: the cheapest repeat each leg has. The snippet is written once to `Temp/state.cs`, then
  read with `ug eval --file` on `ug` and `eval_file` on the Pipeline legs. MCP for Unity has no
  file form: it runs `execute_code` once, calls `get_history` once for the index, then
  `replay`s.

Means of 3 runs, calls / tokens / wall seconds. Every one of the 24 runs cleared 2 rooms, and no
player died.

| poll form | `ug` | `unity-editor-mcp` | `unity command` | MCP for Unity |
|---|---|---|---|---|
| inline | 18.0 / 1,385 / 43.3 | 30.0 / 2,729 / 43.3 | 25.3 / 4,383 / 52.7 | 31.3 / 3,802 / 50.0 |
| file | 23.0 / 496 / 56.0 | 31.3 / 1,038 / 46.0 | 26.0 / 1,470 / 55.0 | 31.0 / 2,286 / 48.3 |

Tokens per move, file form, in the order `ug` / `unity-editor-mcp` / `unity command` / MCP for
Unity:

- one poll (the sleep and the state read): 38 / 47 / 76 / 87;
- play until playing: 9 / 105 / 121 / 488;
- stop: 13 / 71 / 120 / 436;
- card click: 66 / 105 / 208 / 118;
- gate walk: 49 / 77 / 147 / 89.

With idle cost, file form:

- `ug`: 1,121.
- `unity-editor-mcp`: 2,837, which adds 1,509 for deferred names and 290 for the 5 schemas used.
- `unity command`: 10,613.
- MCP for Unity: 3,545, or 7,179 with its skill.

**Findings.**

- `ug` used 52% fewer tokens than the best other leg, `unity-editor-mcp`, in the file form, 49%
  fewer inline, and 60% fewer with idle cost.
- No leg reads room progress without an eval, `ug` included: the room count is game state, and
  `ug ui` shows only the panels.
- `ug eval` took a median 1.33 s on every call, the same file included, because it compiles each
  time. Pipeline's `eval` takes 18 ms and MCP for Unity's `replay` takes 7 ms.
  - Tool time per run: `ug` 27.0 s, `unity-editor-mcp` 8.9 s, MCP for Unity 13.1 s,
    `unity command` 23.8 s. The `unity` process takes about 0.9 s to start on every call.
  - Wall time is mostly the fights, and the differences between legs are within the
    run-to-run spread (39-62 s).
- `ug press W` runs into the gate. `WalkToAsync` walks slower, so the eval legs often sent the
  walk twice.
- `unity command`'s answers echo the parameters, so an inline poll there costs twice what it
  costs on the other legs.
- MCP for Unity: each `editor/state` read is 1.6 KB, and the first read after entering play fails
  after 6 s.

## Testing

- EditMode tests in the package (BridgeHost, batch mode): addressing (paths, `#n`, scene prefix,
  prefab contents, ambiguity; component addresses and their two readings, each row of that table,
  each answered form resolving to its reading alone, printed addresses reading back one way),
  every endpoint, each safety rule (undo reverts, nothing saved, modified scene blocks `open`,
  refused while compiling is covered by the handler check), value parsing per field type.
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
  Profiler (O12): EditMode tests in the batch editor, kept short because a batch editor makes
  thousands of frames a second: `--record 0.3` (frames, header, the 300-frame cap, the profiler
  put back off), a 30 s recording ended by `--max-frames 20` and its refusals, `--frames` over them
  and out of range, `profiler start --file`/`status`/`stop` (the `.raw` written, the stop's range
  ending at the last frame the profiler holds, callstacks and areas set and put back), the path
  refusals, counters by name, category and markers, timing, memory list, `--type` and `--object`
  (game object and asset), a snapshot taken and read back, and the Frame Debugger's refusal on the
  single-threaded batch editor (its reflection names checked there too). Client tests: the `.snap`
  reader on a file written by the test (chunks across block boundaries, both instance ID widths, a
  bad signature, a newer version), list and compare answers, each command's request, and the 0.7.0
  refusal. The `UG_UNITY` end to end records in edit and play mode, starts and stops a `.raw` log
  and reads its frames back, takes two snapshots and compares them, and checks the play-mode log
  holds no error but the batch editor's missing graphics device. The Frame Debugger's events are
  checked live in BridgeHost started with its window and `-force-gfx-mt` (MultiThreaded Direct3D12),
  in edit and play mode (below).

## Boundaries

- Writes scene and asset files only on `ug save`, for prefab-content edits (rule 3), and for
  explicit `ug prefab`, `ug asset` and `ug package` commands. Profiler logs and memory snapshots go
  to `Library/unitygraph/` or a path the user gives outside `Assets/` and `Packages/` (O12).
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
