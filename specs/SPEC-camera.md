# Spec: cameras and Cinemachine (`ug camera`)

Part of ungrep's `editor-ops` module (see `../SPEC.md`, module table; `SPEC-editor-ops.md` for
addresses, Undo and the bridge). Status: **built** (O13, 2026-10-11). Closes the Unity MCP server's
`manage_camera` (its `screenshot` and `screenshot_multiview` actions are `ug shot`, O11).

## Objective

Camera work in a Cinemachine project means knowing which camera is live, why, and what drives it:
priorities, the brain, blends, a camera's Body, Aim and Noise. Today that takes `ug inspect` on
every camera and guessing what the brain will pick. `ug camera` lists the cameras and the live one,
creates cameras from the same presets as Cinemachine's GameObject menu, swaps and tunes the
pipeline stages, sets blends, and forces a camera live for a look. Every answer ends with the live
camera and any blend in progress, as the brain sees it after the change.

## Scope

In: `ug camera [list]`, `ug camera create|priority|body|aim|noise|blend|force|release|brain`;
Cinemachine 3 (`com.unity.cinemachine` 3.x, namespace `Unity.Cinemachine`) in the bridge
sub-assembly `Ungrep.Bridge.Editor.Cinemachine`; plain Unity cameras without it.

Out:

- **Cinemachine 2.x** (namespace `Cinemachine`) **is refused**, not supported (decided 2026-10-11).
  Its API differs throughout (`CinemachineVirtualCamera`, `m_Priority`, the hidden pipeline child
  object, `CinemachineBrain.m_DefaultBlend`), so support would mean a second sub-assembly and test
  fixture for a line Unity replaced in 2023. The header of `ug camera list` names the installed
  2.x version; the Cinemachine commands answer `Cinemachine 2.10.7 is installed; ug camera knows
  Cinemachine 3.x only: ug set and ug component edit 2.x cameras`. `ug set`, `ug component`,
  `ug inspect` and `ug find --live --component CinemachineVirtualCamera` work on 2.x as on any
  component. A future 4.x gets the same refusal until it is checked.
- Cameras inside prefab assets (`Assets/X.prefab:Cam`): refused, `ug camera works on the loaded
  scenes: ug set and ug component edit a prefab's camera`. Liveness and forcing only exist in a
  scene.
- Timeline's Cinemachine track, `CinemachineImpulse` sources, `CinemachineTargetGroup` members,
  spline knot editing: `ug set`, `ug component` and `ug object` do these.
- Lens, extensions, and every other field: `ug set` and `ug component add|remove` already do them
  (MCP parity below); `ug camera` adds nothing that duplicates them.

## Commands

All take `--project <dir>`; all need bridge 0.6.0 (`bridge 0.5.0 is too old for ug camera create:
run ug bridge install again`). A camera is an object address in the loaded scenes (Addressing
objects); the object holds a `CinemachineVirtualCameraBase` (any Cinemachine camera) or a Unity
`Camera`. Edits are one Undo group named `ug camera <action>` and stay unsaved until `ug save`.
Every answer except a refusal ends with the live lines (Answers).

| Command | Does |
| --- | --- |
| `ug camera [list] [--all]` | Header (Cinemachine version or none, counts), one line per camera (30, then `... N more: ug camera list --all`), the live lines. |
| `ug camera create [<name>] [--preset P] [--follow O] [--look-at O] [--priority N] [--fov F] [--parent O] [--at x,y,z]` | A camera from a preset (Presets). Default name: `CinemachineCamera`, or `Camera` for `plain`; Unity's unique sibling name (`Camera (1)`). Placed at `--at`, else where the main camera stands (the `MainCamera`-tagged one, else the only enabled camera: so making it live does not jump), else the origin; under `--parent` or at the active scene's root. A Cinemachine camera copies its lens from that camera, as Cinemachine's menu does from the main camera; `--fov` sets the vertical field of view. Makes sure a brain exists (`brain` below), as the menu does; undoing the create also puts back the pose and lens the new brain drove. |
| `ug camera priority <camera> <n>` | Cinemachine: `Priority` (and turns its Enabled on). Plain: `Camera.depth`, the draw order. |
| `ug camera body <camera> [<type>\|none] [field=value ...] [--follow O\|none]` | The Body stage (position): with a type, swaps the stage's component for it (`Follow`, `CinemachineFollow` and `follow` all name `CinemachineFollow`); `none` removes it; `field=value` pairs set the stage component's fields in `ug set`'s grammar (`CameraDistance=5`, `ShoulderOffset=0.8,-0.4,0`); `--follow` sets `Target.TrackingTarget`. With nothing: the stage's component and the types it can take. |
| `ug camera aim <camera> [<type>\|none] [field=value ...] [--look-at O\|follow\|none]` | The Aim stage (rotation), the same way; `--look-at` sets `Target.LookAtTarget` (and `CustomLookAtTarget`), `follow` goes back to looking at the follow target. |
| `ug camera noise <camera> [<profile>\|none] [--amplitude A] [--frequency F]` | Noise: adds `CinemachineBasicMultiChannelPerlin` when missing, sets `NoiseProfile` (an asset path or a profile name: `6D Shake`, `Handheld_normal_mild`, the package's presets and the project's own), `AmplitudeGain`, `FrequencyGain`; `none` removes it. With nothing: the noise and the profiles (12 named, then `... N more: ug camera noise <camera> --all (or any NoiseSettings asset path)`; `--all` names every profile, bridge 0.9.0, C1). |
| `ug camera blend [--style S] [--time T] [--from A --to B] [--brain O]` | The brain's default blend, or with `--from`/`--to` (camera names, `any`) one entry of its custom blends asset. Styles: `Cut`, `EaseInOut`, `EaseIn`, `EaseOut`, `HardIn`, `HardOut`, `Linear`, `Custom` (its curve: `ug set "Main Camera" DefaultBlend.CustomCurve ...`). A new entry defaults to `EaseInOut 2 s`, Cinemachine's own default. No custom blends asset: refused with the two commands that make and assign one (`ug asset create Assets/CameraBlends.asset CinemachineBlenderSettings`, `ug set "Main Camera" CustomBlends Assets/CameraBlends.asset`), since creating assets is `ug asset`'s job. With no options: the default and the custom blends. |
| `ug camera force <camera> [--brain O]` | Makes the camera live now through the brain's override stack (`CinemachineBrain.SetCameraOverride`), a cut. Changes no data: nothing to undo or save. It holds until `ug camera release`, a play mode change or a script reload (the brain clears its overrides when it is disabled). Forcing another camera replaces the first. |
| `ug camera release [--brain O]` | Ends the force; the brain goes back to priorities. `nothing forced on Main Camera` when there is none. |
| `ug camera brain [<camera>] [--style S] [--time T]` | Makes sure a `CinemachineBrain` is on the camera (default: the main camera, else the only enabled camera), then prints its settings: update method, blend update method, default blend, custom blends asset, channel mask, ignore time scale, world up override. `--style`/`--time` set the default blend as `blend` does. The other fields are `ug set "Main Camera" UpdateMethod LateUpdate` and so on. |

The brain for `blend`, `force` and `release`: `--brain <camera>`, else the one active brain, else
the brain on the main camera; several and none of them on the main camera is refused with the
list; none at all is `no CinemachineBrain: ug camera brain adds one to the main camera`.

`body`, `aim` and `noise` take a `CinemachineCamera` (the other Cinemachine cameras, ClearShot,
StateDriven, Sequencer, Mixing, have no pipeline: refused, naming the type).

## Presets

`create --preset` mirrors Cinemachine 3.1's GameObject > Cinemachine menu (`CinemachineMenu.cs`)
and MCP's preset names (`third_person` and `third-person` both work):

| Preset | Components | Notes |
| --- | --- | --- |
| `static` (default with Cinemachine) | `CinemachineCamera` only | The menu's Cinemachine Camera: stays where it is put. |
| `follow` | `CinemachineFollow`, `CinemachineRotationComposer` | The menu's Follow Camera. |
| `third-person` | `CinemachineThirdPersonFollow` (shoulder 0.8,-0.4,0, side 1, arm 1.2, distance 4), `CinemachineThirdPersonAim` | The menu's Third Person Aim Camera; the aim extension only when the physics module is in the project (Cinemachine compiles it under `CINEMACHINE_PHYSICS`). |
| `freelook` | `CinemachineOrbitalFollow` (three rings), `CinemachineRotationComposer`, `CinemachineInputAxisController`, `CinemachineFreeLookModifier` (disabled) | The menu's FreeLook Camera. |
| `dolly` | `CinemachineRotationComposer`, `CinemachineSplineDolly` on a new `Dolly Spline` object (`SplineContainer` with knots at 0 and 1 m right, auto-smooth, plus `CinemachineSplineSmoother`) next to the camera | The menu's Dolly Camera with Spline; the spline object starts at the camera's position. |
| `top-down` | `CinemachinePositionComposer`, camera turned to look straight down (90, 0, 0) | Frames the follow target from above. |
| `side-scroller` | `CinemachinePositionComposer`, camera facing +z (0, 0, 0) | The menu's 2D Camera; orthographic when the main camera is (the lens is copied), `ug set <cam> Lens.ModeOverride Orthographic` forces it. |
| `plain` (default without Cinemachine) | a Unity `Camera` | No AudioListener and no MainCamera tag, so the scene keeps one of each. |

`--follow`/`--look-at` on a preset set the targets; `--look-at` alone looks at that object with no
follow target. Without Cinemachine only `plain` exists; any other preset, `--follow` and
`--look-at` are refused (`--preset follow needs Cinemachine 3 ...`). `--priority` on a plain camera
is its depth.

## Answers

Each line is at most one camera or one fact; addresses as `ug tree` prints them.

```
$ ug camera
Cinemachine 3.1.7  2 Cinemachine cameras, 1 camera
FollowCam  priority 20  live  body Follow  aim RotationComposer  follow Level/Player
CloseUp  priority 10  standby  body ThirdPersonFollow  noise 6D Shake  follow Level/Player
Shots  ClearShot  priority 0  disabled
Main Camera  Camera  depth -1  fov 60  brain
live: FollowCam (brain Main Camera)

$ ug camera priority CloseUp 30
CloseUp  priority 10 -> 30  (Main.unity modified, not saved)
live: CloseUp (brain Main Camera), blending from FollowCam 0.0 of 2 s

$ ug camera force Shots
forced Shots live on Main Camera (until ug camera release, a play mode change or a script reload)
live: Shots (brain Main Camera, forced)
```

Camera lines: Cinemachine cameras by priority (highest first, the brain's order), then plain
cameras by depth. A Cinemachine line shows the type when it is not `CinemachineCamera`, `priority`,
`live`/`standby`/`disabled`, then only what is set: `body`, `aim`, `noise` (the profile's name),
`follow`, `look-at` (when it differs from follow), `ext` (extensions), `channel` (when not
Default). A plain line: `Camera`, `depth`, `fov` or `ortho <size>`, `display <n>` (when not 1),
`texture <path>` (renders into a texture), `brain` (holds one), `disabled`. The camera line in a
`create` answer leaves out `live`/`standby`: the live lines under it say which camera the brain
picked.

**Live lines** (the last lines of every answer): one per active brain, `live: <camera> (brain
<object>[, forced][, solo from the inspector])`, with `, blending from <camera> <t> of <d> s` while
a blend runs; `live: none (brain <object>: no enabled Cinemachine camera on its channels)`. When
the camera that draws last on display 1 holds no brain (or there is no Cinemachine), a plain line:
`live: Main Camera (Camera, depth -1, draws last on display 1)`; with no camera at all, `live: no
camera draws display 1`.

**After an edit the answer waits for the brain.** A brain picks its camera in `LateUpdate`, on the
next player-loop frame; reading it at once would show the camera before the change. Every
`ug camera` request except `list` therefore answers on a later editor update: it queues a player-
loop update (`EditorApplication.QueuePlayerLoopUpdate`, which runs edit-mode frames too) and
answers once two frames have run, at most 2 s later. While play mode is paused frames do not run:
the live lines then end `(paused: the brain picks on the next frame)`. In edit mode a change is a
cut (Cinemachine blends only in play mode or under an override), so the live camera is final; in
play mode a blend shows its progress.

## Safety

- Undo for every edit, one group per request; nothing saved (Answer.Unsaved suffixes as `ug set`).
- `force` and `release` change no serialized data and leave no undo record.
- **Undo names** (measured, Unity 6000.3): a group is listed under the name of its last record,
  not the name set on it, and a group with no records is still listed under its name, while
  `Undo.PerformUndo` skips it and reverts the group before. So a request that only shows (no
  type, field, profile or option given) opens no group; every edit ends with a complete-object
  record of the camera named `ug camera <action>` (after `Undo.FlushUndoRecordObjects`, since
  `SerializedObject` edits are recorded late under names like `Modified Camera Distance in Cam`);
  components are added with `Undo.AddComponent` and a complete record of the new component under
  that name (`AddComponent` with `RegisterCreatedObjectUndo` is named too, but undoing it
  deactivates the object the component sat on); and a refused edit reverts its group and puts the
  previous name back (`Answer.Undoable`, for every `ug` edit), so the next `ug undo` names the edit
  it really reverts.
- A brain moves its camera and sets its lens on the next frame, outside Undo: a complete record of
  the camera's transform and `Camera` comes before a new brain, so undoing it puts both back. The
  create records the new camera again after its stage components and before its targets; without
  that, `ug redo` brings the camera back with no targets (measured).
- `create` writes nothing outside the scene: no asset (the dolly's spline lives in the scene), no
  tag, no layer.
- Refusals live in the bridge; the client only gates the bridge version.

## Bridge

- Routes: `/camera`, `/camera/create`, `/camera/priority`, `/camera/body`, `/camera/aim`,
  `/camera/noise`, `/camera/blend`, `/camera/force`, `/camera/release`, `/camera/brain`
  (`CameraOps.cs`). Body and aim pairs travel as `n`, `a0`, `a1`, ... as `/run`'s arguments do.
- Plain cameras, the refusals and the settle-then-answer poll live in the main assembly; the
  Cinemachine side implements `ICinemachineRig` in `Editor/Cinemachine/` (asmdef with
  `versionDefines` `com.unity.cinemachine` `[3.0.0,4.0.0)` → `UG_CINEMACHINE`, references
  `Unity.Cinemachine` and `Unity.Splines`; `com.unity.modules.physics` → `UG_PHYSICS` for the
  third-person aim) and sets `CameraOps.Rig` from an `[InitializeOnLoad]` constructor, as
  `Editor/InputSystem` does. No reflection.
- Without the sub-assembly the bridge tells "no Cinemachine" from "Cinemachine 2.x" through
  `PackageManager.PackageInfo.FindForAssetPath("Packages/com.unity.cinemachine")`.
- Stage types come from `TypeCache.GetTypesWithAttribute<CameraPipelineAttribute>()`, without the
  `[Obsolete]` ones (Cinemachine's deprecated 2.x-style components), so a project's own pipeline
  components are found too.
- The force's override id is kept per brain in a static table; a play mode change clears it, and
  a script reload clears both it and the brain's stack.

## Unity MCP parity (`manage_camera`)

| MCP action and parameters | `ug` |
| --- | --- |
| `ping` | `ug camera` header: `Cinemachine 3.1.7`, `no Cinemachine`, or the unsupported 2.x version |
| `ensure_brain` (`camera`, `defaultBlendStyle`, `defaultBlendDuration`) | `ug camera brain [<camera>] --style --time` |
| `get_brain_status` | `ug camera brain` (settings) and the live lines of any `ug camera` |
| `create_camera` (`name`, `preset` follow/third_person/freelook/dolly/static/top_down/side_scroller, `follow`, `lookAt`, `priority`, `fieldOfView`) | `ug camera create [<name>] --preset --follow --look-at --priority --fov` (plus `--parent`, `--at`, and `plain`); falls back to a plain Camera without Cinemachine, as MCP does |
| `set_target` (`follow`, `lookAt`) | `ug camera body <cam> --follow <obj>`, `ug camera aim <cam> --look-at <obj>` (or `ug set <cam> Target.TrackingTarget <obj>`) |
| `set_priority` (`priority`) | `ug camera priority <cam> <n>` (a plain camera's depth) |
| `set_lens` (`fieldOfView`, `nearClipPlane`, `farClipPlane`, `orthographicSize`, `dutch`) | `ug set <cam> Lens.FieldOfView 40` (`Lens.NearClipPlane`, `Lens.FarClipPlane`, `Lens.OrthographicSize`, `Lens.Dutch`); a plain camera: `ug set <cam> field of view 40` (`near clip plane`, `far clip plane`, `orthographic size`; a plain camera has no dutch: roll its transform) |
| `set_body` (`bodyType`, component properties) | `ug camera body <cam> [<type>] [field=value ...]` |
| `set_aim` (`aimType`, component properties) | `ug camera aim <cam> [<type>] [field=value ...]` |
| `set_noise` (`amplitudeGain`, `frequencyGain`) | `ug camera noise <cam> [<profile>] --amplitude --frequency` |
| `add_extension`, `remove_extension` (`extensionType`) | `ug component add <cam> CinemachineDeoccluder`, `ug component remove <cam> CinemachineDeoccluder` (Undo, same as any component); `ug camera` lists them as `ext` |
| `list_cameras` | `ug camera` / `ug camera list --all` |
| `set_blend` (`style`, `duration`) | `ug camera blend --style --time` (and `--from`/`--to` for custom blends) |
| `force_camera` | `ug camera force <cam>` |
| `release_override` | `ug camera release` |
| `screenshot`, `screenshot_multiview` | `ug shot` (SPEC-editor-ops, Viewpoints) |
| `target` (name, path or instance ID), `search_method` (`by_id`, `by_name`, `by_path`) | an object address (a name or a path, `#n` for same-named siblings); `ug find --live` turns a name, tag or component into addresses. Instance IDs are not addresses (SPEC-editor-ops, Addressing objects) |

Nothing in `manage_camera` is left without a `ug` command, flag or named equivalent.

## Testing

- CLI (fake bridge): every action sends its path and query; a 0.5.0 bridge is refused per action.
- EditMode in `fixtures/BridgeHost` (no Cinemachine, the "without" fixture): list and live lines
  for plain cameras, plain `create` (name, depth, fov, `--at`, unique names), `priority` as depth
  with Undo, the refusals of every Cinemachine-only command and preset, prefab-asset refusal, the
  2.x refusal text.
- EditMode with Cinemachine (`Tests/Editor/Cinemachine/`, compiled only when Cinemachine 3 is in
  the project): every preset's components, priorities and the live camera after the settle,
  body/aim swaps with fields and Undo, follow and look-at, noise profiles, default and custom
  blends, force and release, brain creation, the scene's dirty flag and the Undo history.
- The "with" fixture: the end to end's copy of BridgeHost gets Cinemachine 3, Splines,
  Mathematics and Settings Manager as `file:` packages from the package cache `UG_PACKAGE_CACHE`
  names (a Unity project's `Library/PackageCache`, e.g. purge-squad's: no download; the camera
  part is skipped when it is unset or lacks one of them) plus the physics module, and runs those
  EditMode tests in the open editor through `ug test`, then a few `ug camera` calls and their
  undos. It saves the copy's scene first: `ug test` refuses while a scene is modified, since the
  test runner would ask to save it (SPEC-unity-loop, Modified scenes and stopped runs).

## Measured on purge-squad

Unity 6000.3.14f1, Cinemachine 3.1.7, the open editor with one Cinemachine camera and its brain;
a fresh `ug` process per call (process start included). Tokens are bytes / 4.

| Command | Time | Answer |
| --- | --- | --- |
| `ug camera` (list) | 166 ms | 56 tokens |
| `ug camera create --preset follow` | 275 ms | |
| `ug camera create --preset third-person` | 220 ms | 46 tokens |
| `ug camera create --preset dolly` | 391 ms | |
| `ug camera create --preset plain` | 253 ms | |
| `ug camera priority` | 245 ms | |
| `ug camera body` (an edit / a show) | 238 ms | 106 tokens (a show, with the fields) |
| `ug camera aim none` | 213 ms | |
| `ug camera noise` (an edit / a show) | 249 ms | 68 tokens (a show, with the profiles) |
| `ug camera blend --style Cut` / a refusal | 254 / 239 ms | 27 tokens (a show) |
| `ug camera force` / `release` | 221 / 193 ms | |
| `ug camera brain` | 211 ms | 64 tokens |

Requests that wait for the brain (every one but `list`) cost 30-110 ms more than `list`: two
editor frames, and the dolly makes its spline too. End to end (BridgeHost copy, batch mode):
`ug test --filter CinemachineOpsTests` 12 passed in 0.5 s, 0.87 s with its compile check;
`ug camera` 34 tokens, the follow create 53 tokens.

## Open points

1. Custom blend entries cannot be removed by `ug camera blend`; `ug set` on
   `CustomBlends.Array.size` or the asset's inspector does it.
2. `ug test` (unity-loop) with a modified scene: the test runner asks to save it first, a dialog
   that blocks the run (in batch mode until `ug test`'s timeout). Found here; left to its own task.
