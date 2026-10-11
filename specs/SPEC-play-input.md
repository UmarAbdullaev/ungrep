# Spec: play-input

Part of unitygraph (see `../SPEC.md`, module table). Status: **draft** (2026-10-01; UI Toolkit added 2026-10-11, U2). Builds on
`editor-link` (bridge, `BridgeClient`), `editor-ops` (object addresses, play mode, `ug shot`,
`ug batch`, `SPEC-editor-ops.md`) and `unity-loop` (`ug console`, the console buffer,
`SPEC-unity-loop.md`). Closes what the Unity MCP servers' `manage_playmode`, `simulate_ui_input`
and `input_keyboard` / `input_mouse` / `input_gamepad` / `input_touch` do (hold seconds, stick x/y,
batched actions), and since U2 `manage_ui` (UI Toolkit, see UI Toolkit below), in answers sized for
Claude.

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

U2 (bridge 0.7.0) adds **UI Toolkit** runtime documents (UIDocument, UXML, USS) to `ug ui` and
`ug click`, the new `ug nav`, `ug tree --ui`, element addresses in `ug inspect` and `ug set`, UI
Toolkit in `ug shot`, and UXML/USS/TSS in `ug asset create` (UI Toolkit below). Until 0.7.0 UI
Toolkit was deferred and only counted (`2 UI Toolkit documents not listed`).

Out:

- 3D objects that take clicks through a `PhysicsRaycaster` (`IPointerClickHandler` on a collider),
  the legacy Input Manager (`Input.GetKey`), pointer drags other than `swipe`, scroll wheels,
  multi-touch, text typing outside an input field, recording and replaying sessions.
- Anything outside play mode: nothing here edits a scene, an asset or a project setting (Safety).
  Since U2, `ug ui` and `ug tree --ui` also read in edit mode, and `ug set` on a UI Toolkit element
  changes the live element only (UI Toolkit, Safety).
- UI Toolkit editor windows (EditorWindow, inspectors, UI Builder), world-space panels and panels
  drawn into a render texture (counted in the headline, not listed), the dropdown menu a
  `DropdownField` opens (its items live outside the document; `--value` chooses an option).

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
| editor not in play mode (not `ug ui`, which reads edit mode too from 0.7.0) | `not playing: ug play first` |
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

Without uGUI (bridge 0.4.0 to 0.6.0): `com.unity.ugui is not in this project: ug ui and ug click need it`;
from 0.7.0 the uGUI part is left out and UI Toolkit is still listed. With no active
Canvas: `ui  1280x720  playing  no canvas`. With no EventSystem the elements are still listed and the
headline gets `no EventSystem (nothing receives clicks)`, or `no EventSystem (uGUI receives no clicks)` when UI Toolkit documents are listed too (they need none); `ug click` on a uGUI element then refuses with `no EventSystem in the loaded scenes: ug click needs one (add it in edit mode: GameObject > UI > Event System)`. Until 0.7.0 the UI Toolkit count joined the headline (`..., 2 UI Toolkit documents not listed`); UI Toolkit below says how they are listed now.

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
  now: 1 error; scene Main -> Level1; changed Player (new), Menu (inactive), 12 gone
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
Changes slower than that show in a following `ug wait`. While the editor is paused no frame
runs, so only the 0.15 s counts. `ug wait` and `ug play --step` do not settle further: they print the
digest at their own end. An error that repeats one already last in the console counts only its new
repeats.

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

## UI Toolkit (U2, bridge 0.7.0)

Runtime UI Toolkit: a `UIDocument` (a UXML `VisualTreeAsset` plus a `PanelSettings` asset) on a game
object, drawn by a runtime panel. Editor windows and the UI Builder are not part of this.

### Spike (2026-10-11, purge-squad, Unity 6000.3.14f1, URP 17.3.0)

Run with `ug eval` against a live editor (scratch scripts, not kept), on a UIDocument made with
`ug object create`, `ug component add`, `ug set` and a hand-written UXML/USS. The answers:

1. **Listing works in play and in edit mode.** An enabled UIDocument with a PanelSettings has a
   runtime panel in edit mode too (`IPanel.contextType` Player). In edit mode nothing lays the panel
   out: every `worldBound` is NaN until the internal `UIElementsRuntimeUtility.UpdatePanels()` runs
   (reflection; one call lays out every runtime panel). After it, `worldBound` (panel pixels, y
   down), `enabledInHierarchy`, `visible`, `resolvedStyle`, `pickingMode`, `focusable` and
   `GetClasses()` are all readable, and `IPanel.Pick(point)` finds the top element at a point.
   Panel pixels map to Game view pixels linearly: `RuntimePanelUtils.ScreenToPanel(panel, p)` takes a
   Game view point with y down (no flip) and divides by the panel scale (1.5 for the default
   `ConstantPhysicalSize` PanelSettings on a 1440x2960 Game view; 0.75 for `ScaleWithScreenSize`,
   1920x1080 reference, match 0, same view); the bridge inverts it from two points.
   Elements with event callbacks are found through the public `HasBubbleUpHandlers()` /
   `HasTrickleDownHandlers()`: a plain `VisualElement` with `RegisterCallback<ClickEvent>` reports
   one; labels and containers do not; the inner parts of controls (slider dragger, text input) do,
   so they are skipped by being inside a control.
   Found on the way: `ug set <obj> panelSettings <asset>` on an enabled UIDocument in edit mode does
   not attach the document to a panel (`UIDocument.OnValidate` leaves `rootVisualElement.panel` null)
   until the component is disabled and enabled again. The bridge does that after `ug set` on a
   UIDocument (Setup below).
2. **Events work, synchronously, with handlers running as for a real click.**
   `PointerMoveEvent`, `PointerDownEvent` and `PointerUpEvent` made with `GetPooled(Event)` from an
   `Event` (`MouseMove`/`MouseDown`/`MouseUp`, `mousePosition` in panel pixels, button 0, click count
   1) and sent with `panel.visualTree.SendEvent(e)` are dispatched at once: the target is picked by
   position, the pointer is captured on down, and `Button.clicked`, `ClickEvent` and
   `PointerDownEvent` callbacks all ran. `NavigationSubmitEvent`, `NavigationCancelEvent` and
   `NavigationMoveEvent.GetPooled(Vector2)` go to the focused element: submit clicked a focused
   button, down moved the focus to the next element, right on a focused slider raised its value
   (0.8 to 0.81). No `PanelEventHandler`, EventSystem or input device is involved, so this needs
   neither uGUI nor the Input System.
3. **Drawing into `ug shot` works.** After the cameras have drawn into the shot's render texture,
   the bridge sets each screen panel's `PanelSettings.targetTexture` to that texture, calls
   `UIElementsRuntimeUtility.UpdatePanels()`, `RepaintPanels(true)` and `RenderOffscreenPanels()`
   (internal, reflection), then puts `targetTexture` back and lays the panels out again. The UI is
   drawn over the camera image at the shot's size with the panel's own scaling (a 960x540 shot put
   the 76 px button at 52..248 x 101..141, as the panel scale says); a PanelSettings with Clear Color
   off keeps the camera image under it. The PanelSettings asset is not marked dirty by the swap
   (checked with `EditorUtility.IsDirty`). Setting the internal `BaseRuntimePanel.targetTexture`
   field instead draws nothing (the panel's screen size does not follow it): the public property
   is the way. **Limits:** with `-nographics` there is no device and the shot is refused as before;
   in batch mode with a graphics device it works (EditMode test in BridgeHost); UI Toolkit panels
   are drawn after the overlay canvases, so where a game interleaves the two by sort order the shot
   shows UI Toolkit on top; world-space panels (Unity 6.2+, drawn by cameras) and panels that render
   into their own texture are not drawn and are counted in the answer.

Internal members used (reflection, each looked up once; when one is missing in another Unity
version the request that needs it answers `this Unity has no UIElementsRuntimeUtility.UpdatePanels:
ug cannot lay out UI Toolkit in edit mode` and the rest keeps working):
`UnityEngine.UIElements.UIElementsRuntimeUtility.UpdatePanels`, `.RepaintPanels(bool)`,
`.RenderOffscreenPanels`.

### Element addresses

`<document>/<path>`: the address of the game object with the UIDocument (`Menu`, `UI/Hud`,
`Main:Menu`), then element segments. A segment matches **descendants at any depth** of the previous
one (the document's root for the first), so `Menu/Play` is the element named `Play` anywhere in
`Menu`'s document:

- `Play`: the element's `name` (exact, else any case);
- `Button`: when no element has that name, the elements of that type (`Button`, `Toggle`, a
  custom element's class name);
- `.primary` suffixes filter by USS class (`Button.primary`, `.card`);
- `#n` picks the n-th match in tree order (`Button#3`), as `Enemy#2` does for siblings.

Game object addresses win: an address is first resolved as a game object (`Menu/Play` as a child
object named `Play`), and only when that fails is the longest prefix that names a game object
with a UIDocument taken as the document. `ug ui` and `ug tree --ui` print the shortest address that
resolves back to the element: `Menu/Play` when the name is unique in the document, else
`Menu/Pause/Play` (the nearest uniquely named ancestor), else `Menu/Button#3`. A nested UIDocument
(`parentUI` set) is its own document; its elements are not repeated under the parent's.

Not found: `not found: Plya in Menu's UI Toolkit document (near: Play)`. Several:
`ambiguous: Play (2 in Menu): Menu/Pause/Play, Menu/Main/Play`.

### `ug ui` with UI Toolkit

Lists uGUI and UI Toolkit elements together, in reading order (the uGUI order: top to bottom, then
left to right). **Works in edit mode too** (both systems, so a scene can be checked before play;
`ug click` stays play-only): in edit mode the bridge lays the panels out first (spike 1), and the
headline says `edit mode` where it says `playing`/`paused`.

- A UI Toolkit element is a control or anything with event callbacks outside a control:
  `button` (Button, RepeatButton), `toggle` (Toggle), `radio` (RadioButton), `slider` (Slider,
  SliderInt, MinMaxSlider), `scrollbar` (Scroller), `input` (TextField and other text fields),
  `dropdown` (DropdownField, EnumField, PopupField), `foldout` (Foldout), `field` (any other
  `BaseField<T>`: IntegerField, FloatField, Vector fields), `clickable` (any other element with
  callbacks). Elements inside a control are its parts and are not listed.
- Per line, as for uGUI: address, kind, label or value (Button text, field label, the value for
  value controls: `on`/`off`, `0.8 (0-1)`, `"High" (3 options)`, `open`/`closed`), the element's
  own USS classes that do not start with `unity-` (up to 3: `.primary .wide`), centre `x,y` and
  size `WxH` in Game view pixels.
- States: `disabled` (`enabledInHierarchy` false), `hidden` (`visibility: hidden` or opacity 0 on
  it or an ancestor), `offscreen`, `blocked by <address>` (the top pick across the panels at the
  centre, highest sort order first, is not the element or inside it). Elements under
  `display: none` are left out, as inactive uGUI objects are.
- The headline counts both: `ui  1440x2960  edit mode  1 canvas, 2 UI Toolkit documents`, plus
  `N world-space or render-texture UI Toolkit documents not listed` when there are any. A document
  without PanelSettings or UXML counts as `1 UI Toolkit document not shown (no PanelSettings)`.
- Same budget as uGUI: 40 lines, 150 with `--all`, one line of what is left out.

```
$ ug ui
ui  1440x2960  edit mode  2 UI Toolkit documents
Menu/Play  button  "Play"  .primary  525,349  678x114
Menu/Sound  toggle  "Sound"  on  525,501  678x47
Menu/Volume  slider  0.8 (0-1)  525,555  678x47
Menu/Player  input  "Bob"  525,614  678x60
Menu/Quality  dropdown  "High" (3 options)  525,680  678x60
Menu/Button#3  button  "Unnamed"  525,746  678x60
Menu/Card  clickable  "Card"  .card  525,800  678x40
```

### `ug click` on UI Toolkit

`<element>` resolves as before (point, address, label), with UI Toolkit element addresses and labels
added; a point picks the top UI Toolkit element across the panels first (highest sort order first)
and falls back to the uGUI raycast. Play mode only, like every action. For a UI Toolkit element:

1. The point is the element's centre. Disabled elements are sent the click as a player's would be
   (UI Toolkit ignores it); `ug ui` says `disabled` first.
2. The top pick at the point across the panels must be the element or inside it, else
   `blocked: Menu/Dim is on top of Menu/Play at 525,349`; nothing pickable there (picking mode
   Ignore on it and its children): `Menu/Card has no pick target: nothing at 525,800 receives a click
   (picking-mode: ignore)`.
3. `PointerMoveEvent`, `PointerDownEvent`, `PointerUpEvent` at that point through the panel (spike
   2); the panel itself makes the `ClickEvent` and runs `Button.clicked`, a Toggle flips, a focusable
   element takes the focus. The answer and the digest are as for uGUI:
   `clicked Menu/Play (button "Play") at 525,349`.

`--value` sets the control's `value` (through `INotifyValueChanged<T>`, so `ChangeEvent` callbacks
run): `toggle`, `radio` and `foldout` take `on`/`off` (`open`/`closed` for a foldout), `slider` and
`scrollbar` a number in their range (MinMaxSlider `min,max`), `input` text, `dropdown` an option text
or index (EnumField an enum name), `field` its type's text (`3`, `0.5`, `1,2,3`). Disabled elements are
refused (`Menu/Play is disabled: a player could not set it`), other kinds as for uGUI.

### `ug nav <direction> [element] [--shot]`

The navigation a gamepad or keyboard UI uses (`submit`, `cancel`, `up`, `down`, `left`, `right`,
`next`, `previous`), sent to what has the focus. With `element`, that element takes the focus first
(UI Toolkit `Focus()`; uGUI `EventSystem.SetSelectedGameObject`). Without it, the focused UI Toolkit
element (the top panel's focus) or else the uGUI selected object; none: `nothing has the focus: give
an element (ug nav down Menu/Play)`. Play mode only, through the action queue, answers with the
digest.

- UI Toolkit: `NavigationSubmitEvent`, `NavigationCancelEvent`, `NavigationMoveEvent` (a direction
  vector, or `Next`/`Previous`) through the focused element's panel (spike 2).
- uGUI: `ExecuteEvents` `submitHandler`, `cancelHandler`, `moveHandler` (an `AxisEventData` with the
  direction) on the selected object, as `StandaloneInputModule` sends them. `next`/`previous` have no
  uGUI meaning: `uGUI has no next/previous: use up, down, left or right`.
- Answer: `nav down: Menu/Play -> Menu/Quit` (the focus before and after), `nav submit on Menu/Play
  (button "Play")`.

### `ug tree --ui <document or element> [--depth n]`

The visual tree under a document (or one element), like MCP `get_visual_tree`: the first line is the
document (`Menu  UIDocument  Assets/UI/Menu.uxml  PanelSettings Assets/UI/Panel.asset  sort 0`), then
one line per element, indented by depth: its short address after the document's (`Play`,
`Button#3`), type, classes not starting with `unity-`, text (cut at 30 characters), centre and
size, and state words (`disabled`, `hidden`, `display none`, `pick ignore`). Default depth 10, at
most 200 lines and the usual `... N more elements` line. The parts of controls are not expanded
unless the control itself is given (`ug tree --ui Menu/Volume` shows the slider's dragger and
tracker). Works in edit and play mode. A game object without UIDocument: `Menu has no UIDocument`.

### `ug inspect` and `ug set` on elements

`ug inspect Menu/Play` (an element address) answers the element's type, name, all classes, text,
centre and size (also in panel pixels), `enabled`, `visible`, `display`, `focusable`,
`pickingMode`, `tooltip`, the value for value controls, the style sheets on it and its ancestors
(asset paths), its inline styles (`style.*` that are set), and the resolved values of the layout
and look styles (width, height, position, margins, paddings, flex, colours, font size, border radius,
opacity, display, visibility). At most 40 lines.

`ug set <element> <property> <value>` changes the **live** element (MCP `modify_visual_element`):
`text`, `tooltip`, `name`, `enabled` (`SetEnabled`), `display` (`flex`/`none`; MCP's `visible`
maps here), `visibility` (`visible`/`hidden`), `class` (`+a -b ~c`: add, remove, toggle; several at
once), `value` (as `ug click --value`, with `ChangeEvent`, also in edit mode), and `style.<name>`
with the USS name (`style.background-color`) or the C# name (`style.backgroundColor`): colours
(`#2eb89e`, `rgb()`, `rgba()`, names), lengths (`12`, `12px`, `50%`, `auto`), numbers, keywords
(`flex-start`, `bold`), images and fonts as asset paths, `initial` or `null` to drop the inline
value. Answer: `Menu/Play  style.background-color  rgb(46, 184, 158) -> rgb(255, 0, 0)  (live
element only: not saved, no undo; edit Assets/UI/Menu.uxml or .uss to keep it)`. The document
rebuilds its tree from the UXML when it is enabled again, its UXML changes, or scripts reload, and
the change is gone then; in play mode it is gone when play stops.

### `ug shot` with UI Toolkit

- `ug shot game` draws the screen panels over the cameras (spike 3); the answer counts them:
  `(Game view, 1 camera, 2 UI Toolkit documents)`, and `, 1 world-space UI Toolkit document not
  drawn` when there are some. A Game view with UI Toolkit documents and no enabled camera is drawn
  on black (`Game view, no camera, 1 UI Toolkit document`) instead of being refused.
- `ug shot <object with a UIDocument>` (MCP `render_ui` `target`) draws that document's panel alone,
  on its PanelSettings clear colour or dark grey, to `ui-<object>.png`. Documents that share the
  PanelSettings share the panel, so they are drawn too; the answer names them.
- `ug shot Assets/UI/Menu.uxml [--panel Assets/UI/Panel.asset]` (MCP `render_ui` `path`) draws a
  UXML without a scene object: a hidden, never-saved game object with a UIDocument (like the shot
  camera, `HideAndDontSave`, no Undo) on a copy of the given PanelSettings (else the first
  PanelSettings in the project, else a new one with the first theme style sheet found), rendered at
  `--width`/`--height`, then destroyed. File `ui-Menu.png`. Refused for a path that is not a UXML.
- `--width`, `--height` and the file paths as before; MCP's `include_image` is Claude reading the
  PNG, `screenshot_file_name`/`output_folder` are the fixed path plus a copy, `max_resolution` is
  `--width`/`--height`.

### Setup: `ug component add`, `ug set`, `ug asset create`

Checked in the spike, nothing new needed except UXML/USS/TSS files:

- `ug component add Menu UIDocument`, then `ug set Menu sourceAsset Assets/UI/Menu.uxml`,
  `ug set Menu panelSettings Assets/UI/Panel.asset`, `ug set Menu sortingOrder 1` (MCP
  `attach_ui_document`; MCP creates a PanelSettings when none is given, `ug` does that with `ug
  asset create`). After `ug set` on an enabled UIDocument the bridge disables and enables it
  (spike 1: otherwise it shows nothing until the scene reloads); no Undo entry for that toggle,
  the value set is undoable as usual. `ug component remove Menu UIDocument` is
  `detach_ui_document`.
- `ug asset create Assets/UI/Panel.asset PanelSettings` makes a PanelSettings (a ScriptableObject).
  When the project has no theme style sheet, Unity's own `PanelSettings` code writes the default
  runtime theme to `Assets/UI Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss` and assigns it, as
  Assets > Create > UI Toolkit > Panel Settings does (seen in the spike). This is the one write
  beyond the given path; the answer names the file: `created Assets/UI/Panel.asset
  (UnityEngine.UIElements.PanelSettings; Unity also created its default theme Assets/UI
  Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss)`. When the project has theme style sheets,
  Unity assigns one of them, not always the default (in purge-squad it took a `.tss` made a moment
  before), so the answer names it: `(UnityEngine.UIElements.PanelSettings; theme
  Assets/UI/New.tss)`; `ug set Assets/UI/Panel.asset themeUss <path>` changes it. Every MCP
  `create_panel_settings` /
  `update_panel_settings` setting is a field `ug set` takes: `scaleMode`, `referenceResolution`
  (`1920,1080`), `screenMatchMode`, `match`, `referenceDpi`, `fallbackDpi`, `sortingOrder`,
  `targetDisplay`, `clearColor`, `colorClearValue`, `clearDepthStencil`, `themeUss` (the theme
  style sheet), `dynamicAtlasSettings.*`.
- `ug asset create <path>.uxml|.uss|.tss` (the type argument is optional for these extensions, or
  `UXML`, `USS`, `TSS`) writes Unity's starting content and imports it: UXML
  `<ui:UXML xmlns:ui="UnityEngine.UIElements" xmlns:uie="UnityEditor.UIElements"
  editor-extension-mode="False">` with an empty body, USS `VisualElement {}`, TSS
  `@import url("unity-theme://default");` and `VisualElement {}` (what Unity's Create menu
  writes). Refused when the file exists. Claude edits the content with Write and Edit (MCP
  `create`/`read`/`update` of `.uxml`/`.uss`); `ug asset delete` is `delete` (to the OS trash).

### Unity MCP `manage_ui` parity

| `manage_ui` action (parameters) | In `ug` |
| --- | --- |
| `ping` | `ug status` |
| `create`, `read`, `update` (`path`, `contents`) for `.uxml`/`.uss` | `ug asset create` for a new file with Unity's starting content, then Claude's Write, Read and Edit |
| `delete` (`path`) | `ug asset delete` (OS trash) |
| `attach_ui_document` (`target`, `source_asset`, `panel_settings`, `sort_order`) | `ug component add <obj> UIDocument`, `ug set <obj> sourceAsset|panelSettings|sortingOrder` |
| `detach_ui_document` (`target`) | `ug component remove <obj> UIDocument` |
| `create_panel_settings` (`path`, `scale_mode`, `reference_resolution`, `settings`), `update_panel_settings` | `ug asset create <path> PanelSettings`, then `ug set <path> <field> <value>` per setting (names above) |
| `get_visual_tree` (`target`, `max_depth`) | `ug tree --ui <obj> --depth n`; styles of one element: `ug inspect <element>` |
| `render_ui` (`target` or `path`, `width`, `height`, `include_image`, `max_resolution`, `screenshot_file_name`, `output_folder`) | `ug shot <obj>` or `ug shot <x.uxml> [--panel]`, `--width`, `--height`; reading the PNG is the image |
| `link_stylesheet` (`path`, `stylesheet`) | Claude's Edit adds `<Style src="..."/>` to the UXML; asset-index then has the edge (`ug refs`) |
| `list` (`filter_type` uxml/uss/PanelSettings, `page_size`, `page_number`) | `ug find .uxml --kind asset`, `ug find .uss --kind asset` (graph, editor closed); PanelSettings assets: `ug find --live --component` does not apply (assets), so `ug eval 'AssetDatabase.FindAssets("t:PanelSettings").Select(AssetDatabase.GUIDToAssetPath).ToArray()'` (paths; checked in purge-squad); answers are capped with a `more` line instead of pages |
| `modify_visual_element` (`target`, `element_name`, `text`, `add_classes`, `remove_classes`, `toggle_classes`, `style`, `enabled`, `visible`, `tooltip`) | `ug set <obj>/<element> text|class|style.<name>|enabled|display|tooltip <value>` |

Beyond `manage_ui`: elements in `ug ui` with their screen place and state, clicks, `--value` and
navigation that run the game's handlers, edit-mode listing, and the UXML/USS reference edges in the
graph (SPEC-asset-index, UI Toolkit files).

### Safety (UI Toolkit)

- `ug click`, `ug nav` and `--value` are play-mode actions like the rest of this spec.
- `ug ui`, `ug tree --ui`, `ug inspect` and `ug shot` read; in edit mode they lay out the runtime
  panels (`UpdatePanels`), which changes no asset and no scene and marks nothing dirty.
- `ug set` on an element changes the live element only: no Undo (an element is not a Unity
  object), nothing saved, nothing dirty; the answer says so. This is the one edit outside play
  mode that has no Undo; it is lost as described above, which is what makes it safe.
- `ug shot` swaps `PanelSettings.targetTexture` for one render and puts it back in a `finally`,
  without Undo or a dirty flag; the UXML shot's game object and PanelSettings copy are
  `HideAndDontSave` and destroyed after.

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

### Bridge change (0.7.0, U2)

Package version and `Bridge.Version` become 0.7.0; the client knows it as `MinimumUiBridge`
(`BridgeClient.HasUiToolkit`), required by `ug nav`, `ug tree --ui` and `ug shot --panel` / a `.uxml`
shot (`bridge 0.6.0 is too old for ug nav: run ug bridge install again`). `ug ui`, `ug click`,
`ug inspect`, `ug set` and `ug shot` keep their requests and simply know UI Toolkit from 0.7.0.

| Request | Does |
|---|---|
| `/ui?all=` | Both systems, edit mode too. |
| `/ui/click?element=&value=` | UI Toolkit elements and points added. |
| `/ui/nav?direction=&element=` | `ug nav`. |
| `/tree?ui=&depth=` | `ug tree --ui`. |
| `/inspect?object=`, `/set?object=&field=&value=` | An element address when `object` is not a game object or asset. |
| `/shot?view=<doc object or .uxml path>&panel=` | UI Toolkit shots. |
| `/asset/create?path=&type=` | `.uxml`, `.uss`, `.tss`. |

UI Toolkit is part of the engine (`UnityEngine.UIElementsModule`), so its code lives in the core
assembly `UnityGraph.Bridge.Editor` (no package, no `versionDefines`); the three internal methods
are reached by reflection (Spike). The uGUI assembly keeps its part behind `IUiInput`, which gains
the items for the merged list and `Nav`.

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

- **U2, UI Toolkit** (bridge 0.7.0): CLI tests for `ug nav`, `ug tree --ui`, `ug shot --panel`, the
  0.6.0 refusal, `ug asset create` without a type for `.uxml`/`.uss`/`.tss`; EditMode tests on a
  UIDocument built in the test from a UXML string and a PanelSettings made in memory: addresses
  (name, type, class, `#n`, any case, nested documents, game objects winning, near names), the
  list in edit mode (kinds, labels, classes, states, `display: none` left out), clicks and `--value` per kind
  and `ug nav` through the methods the queue calls (handlers counted), `tree --ui`, element
  `inspect` and `set` (text, class, style, enabled, display), the UIDocument re-attach after `ug
  set`, asset create for the three file types, and shots (game with a document, a document alone, a
  UXML file; skipped without a graphics device, as O9's); dotnet tests for the asset-index edges
  (SPEC-asset-index). End to end (`UG_UNITY`): a UI Toolkit menu built with `ug` in a BridgeHost
  copy, listed in edit mode, then `ug play`, `ug click`, `--value`, `ug nav`, `ug stop`; the
  edit-mode refusals of `ug click` and `ug nav`, and `ug shot` refused under `-nographics`.

## Boundaries

- Always: play mode only; a release for every hold; answers within their line and token limits.
- Ask first: any further request that changes the project, a project setting or an asset; reading
  or recording the user's real devices; a network call (none here).
- Never: save anything, use Undo for runtime events, leave a key or a setting held after play
  stops, send input from a hook.

## Measured

End to end on a copy of `fixtures/BridgeHost` with `com.unity.ugui` and Input System 1.14.2, Active Input
Handling "Both", Unity 6000.3.14f1, batch mode with `-nographics` (`UG_UNITY`, 2026-10-01). Tokens are bytes / 4
of the printed answer. The Game view is 640x480 without a window.

| What | Time | Answer |
|---|---|---|
| `ug ui`, 2 usable, 1 blocked | 0.03 s | 43 tokens |
| `ug click Start` with its digest (1 error, 2 objects changed) | 0.19 s | 41 tokens |
| `ug press Space` with its digest | 0.27 s | 13 tokens |
| `ug batch` of press, stick 0.2 s, tap, wait 0.2 s | 1.2 s | 98 tokens |

`--shot` under `-nographics` answers `cannot take a screenshot: this Unity runs without a graphics device`
after the action's own answer; the picture itself is not measured here.

### U2, UI Toolkit (2026-10-11)

**purge-squad** (Unity 6000.3.14f1, URP 17.3.0, open editor with a window, Game view 1440x2960
portrait, bridge 0.7.0, Release `ug`, fresh process per call, wall time including process start).
The menu is `Assets/UgSpike/Menu.uxml` + `Menu.uss` (title, Play, Quit, toggle, slider, text field,
dropdown, an unnamed button) on `Assets/UgSpike/Panel.asset` made with `ug asset create`. Boot.unity
loads another scene on play, so in play mode the document was built live with one `ug batch` of
`object create`, `component add UIDocument` and two `set`s (0.69 s, 153 tokens).

| What | Time | Answer |
|---|---|---|
| `ug ui`, edit mode, 7 elements, 2 canvases beside | 0.21 s | 117 tokens |
| `ug tree --ui UgMenu`, 10 lines | 0.19 s | 163 tokens |
| `ug inspect UgMenu/Play`, 12 lines | 0.29 s | 228 tokens |
| `ug shot UgMenu` (document alone, 960x540) | 0.26 s | 21 tokens |
| `ug shot Assets/UgSpike/Menu.uxml --width 640 --height 480` | 0.37 s | 32 tokens |
| `ug ui`, playing, 7 elements + 3 blocked uGUI | 0.24 s | 117 tokens |
| `ug click Play` with its digest | 0.41 s | 20 tokens |
| `ug click 525,442` (a point, picks UgMenu/Quit) | 0.40 s | 17 tokens |
| `ug click <slider/dropdown/toggle/input> --value` | 0.38 to 0.40 s | 14 to 17 tokens |
| `ug nav down UgMenu/Play` | 0.40 s | 15 tokens |
| `ug nav submit` (focus kept from the move) | 0.39 s | 16 tokens |
| `ug shot game` playing, 1 camera + 1 document | 0.25 s | 26 tokens |
| `ug refs Assets/UgSpike/Menu.uss` (the UXML's `Style` edge) | 0.56 s | 42 tokens |

A whole menu check (list, one click, one value, one nav) is four calls and about 170 tokens. The
document shot draws at the panel's own scale: the PanelSettings uses Unity's default Constant
Physical Size, so at the editor's 144 dpi the menu is 1.5x in any shot size, and the default
960x540 crops a menu laid out for the portrait Game view; `--width 720 --height 1480` shows it whole.

**End to end** (`UG_UNITY`, BridgeHost copy, `-nographics`, Game view 640x480 without a window):
`ug asset create` PanelSettings 0.21 s (Unity also writes its default theme), `ug tree --ui`
0.012 s / 67 tokens, `ug ui` in edit mode with 3 UI Toolkit and 3 uGUI elements 0.011 s / 82
tokens, `ug click Menu/Go` with its digest (1 error, 1 new object) 0.22 s / 33 tokens, `ug nav down`
0.16 s. The whole end-to-end run: 74 s.

## Open points

1. ~~UI Toolkit~~: done in U2 (UI Toolkit). Left open there: world-space panels (Unity 6.2+) and
   panels drawn into a render texture (listing would need the camera ray into the panel), the items
   of an open dropdown menu, and interleaving UI Toolkit with overlay canvases by sort order in a
   shot.
2. 3D click targets (`PhysicsRaycaster`): list objects with a collider and a click handler.
3. A condition wait (`ug wait --until "console contains X"`): only if play-testing shows the digest
   and `ug wait <s>` are not enough.
