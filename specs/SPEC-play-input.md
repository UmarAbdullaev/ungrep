# Spec: play-input

Part of unitygraph (see `../SPEC.md`, module table). Status: **draft** (2026-10-01). Builds on
`editor-link` (bridge, `BridgeClient`), `editor-ops` (object addresses, play mode, `ug shot`,
`ug batch`, `SPEC-editor-ops.md`) and `unity-loop` (`ug console`, the console buffer,
`SPEC-unity-loop.md`). Closes what the Unity MCP servers' `manage_playmode`, `simulate_ui_input`
and `input_keyboard` / `input_mouse` / `input_gamepad` / `input_touch` do (hold seconds, stick x/y,
batched actions), in answers sized for Claude.

## Objective

Claude can edit a scene and enter play mode, but it cannot play: it does not know which buttons are
on screen, cannot press one, hold a key, push a stick or tap, and must read the console and a
screenshot to learn what happened. `play-input` adds the hands and the eyes for that: `ug ui` lists
what a player could use right now, `ug click`, `ug press`, `ug stick`, `ug tap`, `ug swipe` and
`ug wait` act, and every action answers with a one-line digest of what changed (new errors,
objects changed, scene change), so a play-test loop needs no screenshot and no console call.

## Scope

In: `ug ui`, `ug click`, `ug press`, `ug stick`, `ug tap`, `ug swipe`, `ug wait`,
`ug play --step`, the digest after each action and the `--shot` option; eight bridge requests
(bridge 0.4.0); uGUI (`com.unity.ugui`, the `UnityEngine.UI` and TextMeshPro components) for `ug ui`
and `ug click`; the Input System package (`com.unity.inputsystem`) for devices.

Out:

- **UI Toolkit** (UIDocument, UXML) **is deferred**: `ug ui` only says how many UI Toolkit documents
  it did not list (`2 UI Toolkit documents not listed` in the headline). Driving a runtime panel
  needs its own event simulation (`PanelEventHandler`, `UIElements.Event` pooling) and a way to find
  and address a `VisualElement`; that is a spike (open point 1), not part of this build.
- 3D objects that take clicks through a `PhysicsRaycaster` (`IPointerClickHandler` on a collider),
  the legacy Input Manager (`Input.GetKey`), pointer drags other than `swipe`, scroll wheels,
  multi-touch, text typing outside an input field, recording and replaying sessions.
- Anything outside play mode: nothing here edits a scene, an asset or a project setting (Safety).

## Commands

All take `--project <dir>`. Exit 0 for any answer, 1 only for no Unity project, a malformed
argument (`--hold` out of range, `x,y` not two numbers) or an invalid config. Plain text. Every
answer is **at most 6 lines** (`ug ui` at most 42), as in editor-ops. The module switch is
`play-input` (`play-input is off in .claude/unitygraph.json`).

Common refusals, one line, in this order:

| When | Answer |
|---|---|
| no bridge | `Unity not reachable (<reason>); ug <cmd> needs the bridge` and the install hint |
| bridge older than 0.4.0 | `bridge 0.3.0 is too old for ug <cmd>: run ug bridge install again` |
| editor not in play mode | `not playing: ug play first` |
| a `press`, `stick`, `tap`, `swipe`, `wait` while paused | `paused: input is read on frames; ug play resumes, ug play --step <n> advances` |
| another action still running | `an input action is running (press Space, 3.2 s left)` |

Coordinates are **Game view pixels from the top-left corner**, y down, the numbers `ug ui` prints
(its first line gives the size, `ug shot game --width W` has the same proportions). The bridge
converts to Unity's bottom-left screen space.

### `ug ui [--all]`

The interactable uGUI elements of the loaded scenes, active in the hierarchy, in reading order (top
to bottom, then left to right), as the addresses `ug click`, `ug inspect` and `ug set` take.

- An element is a `UnityEngine.UI.Selectable` (button, toggle, slider, scrollbar, input field,
  dropdown, and the TextMeshPro input field and dropdown, which are Selectables) or any other
  object with an `IPointerClickHandler`, under a Canvas.
- Per line: address, kind, label or value, state words, centre `x,y` and size `WxH`. The label is
  the first Text or TMP text under it, cut at 30 characters.
- Default: only what a player can use now. Disabled (`interactable` off), blocked (the top
  EventSystem raycast at its centre is not it or inside it) and off-screen elements are counted in a
  last line. `--all` lists them with the state word `disabled`, `blocked by <address>` or
  `offscreen`.
- **Token budget.** At most 40 element lines by default (about 80 bytes a line, 800 tokens at the
  worst; a typical menu is 8 lines, about 160 tokens); `--all` at most 150. Past that a last line says
  `(+N more: ug ui --all)` or `(+N more not shown)`. The SPEC answer budget (1,200) stays the
  ceiling.

```
$ ug ui
ui  1280x720  playing  2 canvases
Canvas/Menu/Play  button  "Play"  640,360  300x76
Canvas/Menu/Sound  toggle  "Sound"  on  640,440  200x40
Canvas/Menu/Volume  slider  0.8 (0-1)  640,500  400x20
Canvas/Menu/Name  input  "Bob"  640,560  300x40
Canvas/Menu/Quality  dropdown  "High" (3 options)  640,620  300x40
  3 more not usable (2 disabled, 1 blocked): ug ui --all
```

Without uGUI: `com.unity.ugui is not in this project: ug ui and ug click need it`. With no active
Canvas: `ui  1280x720  playing  no canvas`. With no EventSystem the elements are still listed and the
headline gets `no EventSystem (nothing receives clicks)`; `ug click` then refuses with `no EventSystem in the loaded scenes: ug click needs one (add it in edit mode: GameObject > UI > Event System)`. The UI Toolkit count joins the headline (`..., 2 UI Toolkit documents not listed`), so `ug ui` stays within 42 lines.

### `ug click <element> [--value <v>] [--shot]`

Sends a pointer click through the EventSystem, as a mouse would. `<element>` is one of, tried in this
order: `x,y` (a point, no element: whatever is under it); an object address (`Canvas/Menu/Play`,
`Play#2`, `Main:Canvas/Play`); a label from `ug ui` (case-insensitive, exact first, then a part;
several matches: `ambiguous: "Play" (Canvas/Menu/Play, Canvas/Pause/Play)`).

1. The point is the element's centre. The bridge raycasts there (`EventSystem.RaycastAll`) and
   takes the first hit as the target, so a button under a panel is refused, not clicked:
   `blocked: Canvas/Dim is on top of Canvas/Menu/Play at 640,360`.
   An element nothing catches a click on: `Canvas/Menu/Play has no raycast target: nothing at
   640,360 receives a click (a Graphic with Raycast Target on)`.
2. On the target, in order: `pointerEnter`, `pointerDown` (with `select` for a Selectable),
   `pointerUp`, `pointerClick`, through `ExecuteEvents`, with the event data a real click has
   (position, press position, raycast result, camera, button Left, click count 1). A disabled
   Selectable gets them and ignores them, as in a player; `ug ui` says `disabled` first.
3. The answer waits for the settle (Digest) and prints the action line and the digest.

`--value <v>` operates the element through its own component instead of a click, for what a click
cannot set: slider and scrollbar a number in their range, an input field its text (then
`onEndEdit` and `onSubmit` are raised), a dropdown an option text or index, a toggle `on` or `off`.
The component's own events fire (`onValueChanged`). Anything else is refused:
`Canvas/Menu/Play is a button: --value takes a slider, scrollbar, input, dropdown or toggle`.

```
$ ug click Play
clicked Canvas/Menu/Play (button "Play") at 640,360
  now: scene Main -> Level1; 1 error; changed 4 objects
  error  NullReferenceException: Object reference not set  Assets/Scripts/Level.cs:12

$ ug click Volume --value 0.2
set Canvas/Menu/Volume (slider) 0.8 -> 0.2
  now: nothing changed
```

### `ug press <key|button> [--hold s] [--shot]`

Holds a key or a gamepad button down and releases it. `--hold` is 0.05 to 60 seconds, default 0.1.
It is real time (not scaled by `Time.timeScale`) but never shorter than two frames, so the game sees
the press (`wasPressedThisFrame` needs a frame between down and up).

- Keyboard: a `UnityEngine.InputSystem.Key` name, case-insensitive: `Space`, `A`, `Enter`,
  `LeftShift`, `UpArrow`, `Digit1`. Aliases: `Esc`, `Return`, `Shift`, `Ctrl`, `Alt`, `Up`, `Down`,
  `Left`, `Right`, and a bare digit `1` for `Digit1`.
- Gamepad: `pad:<name>`, with `south`, `east`, `west`, `north`, `a`, `b`, `x`, `y`, `start`, `select`,
  `lshoulder`, `rshoulder`, `ltrigger`, `rtrigger` (pressed fully), `up`, `down`, `left`, `right` (the
  d-pad), `lstick`, `rstick` (stick presses). The names that are not also keyboard names work
  without the prefix (`south`, `start`, `ltrigger`); `a`, `x`, `up` and the like need `pad:`.
- Mouse: `mouse:left`, `mouse:right`, `mouse:middle`, a button down at the pointer's current place.
- Unknown name: `no key or button Spaec; near: Space` (up to 5 near names, else the first 5 of the
  keyboard list and `pad:` hint).

```
$ ug press Space --hold 0.2
pressed Space for 0.2 s
  now: changed Player (moved), Bullet#3 (new)
```

### `ug stick <left|right> x,y [--hold s] [--shot]`

Pushes a gamepad stick to `x,y` (each -1 to 1, y up) for `--hold` seconds (0.05 to 60, default 0.5;
two frames at least), then back to 0,0. Answer `stick left 1,0 for 0.5 s`. Out of range:
`x,y must be -1 to 1, like 1,0`.

### `ug tap x,y [--hold s] [--mouse] [--shot]`

A one-finger touch down and up at the point (default hold 0.1 s). With `--mouse` a left click of the
Mouse device at the point instead, for games that read `Mouse.current`. Touch needs a Touchscreen
device, the Mouse a Mouse: when the machine has none, a virtual one is added (Safety). Answer
`tapped 640,360 for 0.1 s`. This goes through the Input System, so an uGUI element under it receives
it only if the scene's `InputSystemUIInputModule` is set up; `ug click` is the way to test a UI
without devices.

### `ug swipe x,y x,y [--over s] [--mouse] [--shot]`

Touch down at the first point, move in a straight line to the second over `--over` seconds (0.05 to
10, default 0.3, one move each frame, at least 3), touch up. With `--mouse` the left button is held
for it. Answer `swiped 200,400 -> 600,400 over 0.3 s`.

### `ug wait <s> [--shot]`

Lets the game run `s` real seconds (0.05 to 60) and prints the digest of what happened meanwhile,
for what does not follow an action at once (an animation, a spawner, a scene loading in the
background). Answer `waited 2.0 s`.

### `ug play --step [N]`

With the editor paused, advances `N` frames (1 to 600, default 1, one `EditorApplication.Step` each
update) and prints the digest. Answer `stepped 5 frames (frame 1042)`. Refused when not paused:
`not paused: ug pause first`. `--step` and `--for` together are a usage error (exit 1).

### Digest and `--shot` (I4)

Every action answers with one `  now:` line after its action line. It is made by the bridge from a
snapshot taken when the action started and a comparison after it **settled**: two frames and 0.15
real seconds after the action's last event (so a click that changes scene shows the new scene).
Changes slower than that show in a following `ug wait`.

Parts, in this order, joined with `; `, left out when empty; with none: `nothing changed`:

- **console**: `1 error`, `3 warnings` (new entries since the snapshot; errors, asserts and
  exceptions count as errors). Up to 2 of the new errors follow as `ug console`'s own lines
  (`  error x2  text  Assets/File.cs:42`, folded, first line of the text cut at 160 characters).
- **scene**: `scene Main -> Level1` (active scene changed), `scene +Level1` or `scene -UI` (loaded or
  unloaded).
- **objects**: `changed Player (moved), Door (inactive), Bullet#3 (new), 2 gone`: game objects of
  the loaded scenes compared by instance id on active state, parent, name and world position (moved
  = more than 0.01); at most 3 named, then `+N more`. Component values are not compared
  (`ug inspect` reads them). Above 20,000 objects the part is `objects not compared (over 20000)`.
- **play**: `play stopped` when the game left play mode meanwhile (a script quit, an exception
  stopped it): the answer ends there.

`--shot` (on every action) appends one line after the digest: the `ug shot game` answer (path, size).
A shot that cannot be taken (`-nographics`) appends its refusal line; the action is not undone or
repeated. `ug batch` takes all of these commands as steps (`ug batch "click Play" "wait 1"
"press Space --hold 0.3" "click Quit"`): each step prints its own lines, a refusal stops the rest as
in editor-ops. `ug ui` is a step too.

## Bridge change (0.4.0)

Package version and `Bridge.Version` become 0.4.0; the client knows it as `MinimumInputBridge`
(`BridgeClient.HasPlayInput`). `ug` shows `bridge 0.3.0 is too old for ug <cmd>` for the new commands
only: `ug console`, `ug log` and the rest keep working on 0.3.0. The requests are `GET`, token and
origin checks as in editor-link, answers `{"lines":[...]}` or `{"error":"..."}` (status 200) as in
editor-ops. All but `/ui` finish on a later editor update (the connection stays open, as for the
package requests) and answer when the action has run and settled; the client's timeout for them is
the action's length plus 10 s.

| Request | Does |
|---|---|
| `/ui?all=` | The list above. |
| `/ui/click?element=&value=` | The click or the value set; `element` is `x,y`, an address or a label. |
| `/input/press?key=&hold=` | Key or gamepad button down, up after the hold. |
| `/input/stick?side=&x=&y=&hold=` | Stick deflection and release. |
| `/input/tap?x=&y=&hold=&mouse=` | Touch (or left mouse button) down and up. |
| `/input/swipe?x1=&y1=&x2=&y2=&over=&mouse=` | Touch (or left button) down, move, up. |
| `/input/wait?seconds=` | The snapshot and the digest after `seconds`. |
| `/input/step?frames=` | Frame steps while paused, then the digest. |

Three assemblies, because the first two compile only where their package is present (as
`UnityGraph.Bridge.Editor.TestRuns` does for the test framework): `UnityGraph.Bridge.Editor` has the
requests, the action queue, the digest and the refusals; `UnityGraph.Bridge.Editor.Ugui`
(`versionDefines`: `com.unity.ugui` gives `UG_UGUI`) lists and clicks; `UnityGraph.Bridge.Editor.InputSystem`
(`com.unity.inputsystem` gives `UG_INPUT_SYSTEM`) drives devices. Each registers an interface the
core calls (`IUiInput`, `IDeviceInput`); when one is missing the core answers the refusal. The
requests refuse unless `ENABLE_INPUT_SYSTEM` is also defined (Project Settings,
Player, Active Input Handling set to Input System or Both): in a legacy project the answer is
`this project reads the old Input Manager (Player settings, Active Input Handling): ug press needs
the Input System or Both; ug click still works`. A project without the package:
`com.unity.inputsystem is not in this project: ug press needs it; ug click still works`.

## Safety

1. **Play mode only, nothing saved.** Every action is a runtime event or a runtime call. Nothing here
   marks a scene dirty, writes an asset, a prefab or a project setting, or uses Undo (play-mode
   changes are lost when play stops, as for any play-mode edit). No hook calls these commands
   (SPEC Boundaries).
2. **Release when play stops.** Pending actions live in the bridge's queue. When play mode is left
   (`ExitingPlayMode`, from `ug stop`, a script, or the user) or the assemblies reload, the queue is
   emptied first: held keys, buttons and sticks get a released state event, a pointer gets an up, the
   in-flight request answers `<action>: play mode ended after 0.4 s, released`, and devices the bridge
   added are removed. A crash of `ug` while holding changes nothing: the release is the bridge's own
   timer, not the client's.
3. **One action at a time**, so a held key is never overwritten by a second session's.
4. **Input reaches the game without Game view focus.** The Input System drops keyboard and pointer
   input while no Game view has focus (the usual case when Claude drives the editor from another
   window). From the first device action of a play session until play stops the bridge sets
   `InputSystem.settings.editorInputBehaviorInPlayMode` to `AllDeviceInputAlwaysGoesToGameView` and
   `backgroundBehavior` to `IgnoreFocus` in memory, and puts both back when play stops. The settings
   asset is not marked dirty and not saved. Effect: while that session plays, the user's keyboard also
   reaches the game even when another window has focus.
5. **Virtual devices.** A keyboard, mouse, gamepad or touchscreen is added with `InputSystem.AddDevice`
   only when the machine has none (`Keyboard.current` is null), named `ug <kind>`, and removed when
   play stops. Real devices are used as they are; their physical state is not read or changed
   beyond the events the action queues (a full state event for the held keys).
6. **Busy editor.** Refused while compiling or importing (`Unity is compiling`), as in editor-ops.
7. **Clicks go where a player's would**: the raycast decides, never the element directly, so a
   covered button is refused. `--value` is the one exception and says so.

## Testing

Written once, after I4 (the build order of the task: no tests while building).

- **CLI** (`tests/UnityGraph.Cli.Tests`, fake bridge): each command's request path and query,
  argument checks (hold range, `x,y`, stick range, `--step` with `--for`), answers and refusals
  printed as the bridge sends them, the old-bridge answer, module off, unreachable, `--shot`
  appending the `/shot` answer, a step of each command inside `ug batch` with a refusal stopping it.
- **Client** (`UnityGraph.EditorLink.Tests`): `HasPlayInput` for 0.3.0 and 0.4.0, the long timeout.
- **Bridge** (EditMode in `fixtures/BridgeHost`): digest (new error, scene change, moved and new and
  gone objects, nothing changed, over the cap), the action queue (release after the hold and two
  frames, one at a time, cancel on play stop), key and button name parsing and near names, the
  uGUI list (kinds, state words, order, budget cut, blocked, disabled), click through the raycast
  (blocked refusal, no raycast target, `--value` per kind), the refusals (not playing, paused).
- **End to end** (`UG_UNITY`, copy of BridgeHost with `com.unity.ugui` and a local
  `com.unity.inputsystem` from Unity's package cache, no download): a UI scene built with `ug`, `ug
  play`, `ug ui`, `ug click` on a button that logs and changes an object (digest read back), a
  covered button refused, `--value` on a slider, a probe script that counts key and stick input
  (`ug press Space`, `ug stick left 1,0`, `ug tap`), a hold released by `ug stop`, `ug wait`,
  `ug pause` + `ug play --step 3`, an action in edit mode refused, `--shot` refused under
  `-nographics`, and one `ug batch` of several actions. Times and token sizes go in Measured.

## Boundaries

- Always: play mode only; a release for every hold; answers within their line and token limits.
- Ask first: any further request that changes the project, a project setting or an asset; reading
  or recording the user's real devices; a network call (none here).
- Never: save anything, use Undo for runtime events, leave a key or a setting held after play
  stops, send input from a hook.

## Measured

Not measured yet (the end-to-end run after I4 fills this in).

## Open points

1. UI Toolkit: whether a runtime panel can be listed (`UIDocument.rootVisualElement` query for
   clickables) and clicked (`PanelEventHandler` or `panel.visualTree` event dispatch) with an address
   a person can use (`doc/Name/#class`). Deferred, spike first.
2. 3D click targets (`PhysicsRaycaster`): list objects with a collider and a click handler.
3. A condition wait (`ug wait --until "console contains X"`): only if play-testing shows the digest
   and `ug wait <s>` are not enough.
