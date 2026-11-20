# P5: Unity MCP gaps in four groups (R1+X1, O11+O13, U2, O12)

Repo: `D:\Projects\unitygraph`. Read first: `SPEC.md` (module table, Decisions, Boundaries),
`specs/SPEC-editor-link.md`, `specs/SPEC-editor-ops.md` (Safety rules, the MCP tool table),
`specs/SPEC-play-input.md`, `tasks/todo.md` (section "Unity MCP gaps"), your memory files, and
MCP for Unity's tool list in `~/.claude/skills/unity-mcp-skill/references/tools-reference.md`.

## How this prompt runs
This prompt is reused for four chats. Each chat does ONE group: the first group below whose tasks
are not all ticked in `tasks/todo.md` (if a group is half done, finish it). Inside a group, run the
tasks back to back with no stop except the asks listed. When the group is done: report and stop.
Do not start the next group; it runs in a new chat with this same prompt.

The groups were set when the user asked for grouped runs (2026-10-10). The todo order is R1 X1
O11 U2 O12 O13; O13 sits with O11 because both work on cameras (temporary camera, live camera
readout). O10 runs after all four groups.

## Rules that hold in every group
- Spec first for each task, no review stop; the user reviews only where this prompt says "ask".
  After each task: tests, `dotnet format`, tick `tasks/todo.md` with its numbers, commit (O8 style,
  no attribution trailer, no Co-Authored-By), one or two report lines.
- Full capability (user, 2026-10-10): every action and parameter of the MCP tool a task closes gets
  a `ug` command or flag, or a line in the spec naming the `ug` equivalent that already covers it.
  Nothing is left out to save tokens; answers stay compact with `more` handles. Update the MCP tool
  table in `SPEC-editor-ops.md` as rows close.
- Bridge: one version bump per group (now 0.4.0; group 1 makes it 0.5.0, and so on). Optional
  packages get their own sub-assembly with `versionDefines` + `defineConstraints`, like
  `Editor/InputSystem` and `Editor/Ugui`; no reflection where an asmdef can do it. Reinstall the
  bridge in `fixtures/BridgeHost` and purge-squad after a bump.
- Editor-ops rules hold: Undo for every edit, nothing saved unasked, refusals live in the bridge.
- Skill: one line per new command in `skills/unity/SKILL.md`, body under 600 tokens (the budget
  test checks it); detail goes in `--help`.
- Do not commit `tasks/*-prompt.md` or `specs/SPEC-viewer.md`. If `git status` still shows an
  uncommitted change to `skills/unity/ui.md` that you did not make, leave it out of commits and
  ask the user about it before U2 edits that file.
- Ask before: any new NuGet or UPM package (Roslyn is approved and already in `UnityGraph.Code`),
  any network call other than the docs.unity3d.com fallback (a UPM package download counts), a
  graph schema version bump, a permanent delete path, writes into a user's Unity project beyond
  what the spec lists. Anything the spec does not cover: ask, do not invent. Questions from the
  user are not approvals.
- Tests need no network and no Unity unless env-gated (`UG_UNITY`); EditMode tests run in
  `fixtures/BridgeHost` in batch mode; shot tests need a graphics device (no `-nographics`) and
  skip without one, as O9's do. LF files; TreatWarningsAsErrors; follow surrounding style.
- Unity launches via PowerShell `Start-Process` with absolute `-testResults` and `-logFile`;
  Unity 6000.3.14f1 at `D:\Program Files\Unity Editors\6000.3.14f1\Editor\Unity.exe`.
- purge-squad (`D:\Projects\Unity\purge-squad`) is the testing ground: edits allowed, saved in
  git. It has URP 17.3.0, Cinemachine 3.1.7 and the Input System. Each task ends with a
  purge-squad measurement recorded in its spec (calls, answer sizes, times).
- Scratch output in the session scratchpad. Absolute paths in Bash; never `cd` into a subfolder
  (it moves the session's working folder).
- Chat replies terse (caveman); every status message ends with x/y tasks done or %.

## Group 1: run code in the editor (R1, X1)
Shared by both: one formatter for return values (null, numbers, strings quoted and clipped,
enums, vectors and colours as `ug set` prints them, a `UnityEngine.Object` as an address
`ug inspect` accepts, collections capped with `more`, other objects as public members one level
deep) and one shape for exceptions (message plus the first project frame as `file:line`;
`StackFrames.cs` exists). Both work in edit and play mode and inside `ug batch`.

R1: extend `ug run` (SPEC-editor-link.md; bridge `/run` in `MethodRunner.cs`; CLI in
`EditorCommands.cs`). Today it runs static methods without parameters and menu items.
- Parameters: numbers, bool, string, enum, vectors, colours, object addresses, asset paths, with
  `ug set`'s grammar. `Values.Parse` writes into a `SerializedProperty`; R1 needs the same grammar
  for a `System.Type` target, so factor the text grammar out instead of copying it.
- Overloads chosen by argument count, then by which parse succeeds; an ambiguous call lists the
  overloads. Optional parameters, defaults, `params` arrays.
- Return value printed through the shared formatter.
- Decide and write down: non-public methods; `Task` and `IEnumerator` results (await with a
  timeout?); a way to find runnable methods (`ug run --list <Type>` or through `ug find`).
- Menu-item refusals (File/Quit, Build) stay as they are.

X1: `ug eval "<C#>"` and `ug eval --file <x.cs>`, unrestricted (user, 2026-10-10): no API
blocklist, no sandbox.
- Compile in `ug` with Roslyn. Take the references from the bridge (the locations of the
  assemblies loaded in the running editor, cached until the next compile or domain reload) so they
  match the editor exactly; fall back to `Library/ScriptAssemblies` and the editor's Managed
  folders. Match Unity's language version and target profile (check what 6000.3's Mono loads).
- Snippet shapes: a bare expression, statements with `return`, a whole file with classes. Default
  `using`s: System, System.Linq, System.Collections.Generic, UnityEngine, UnityEditor,
  UnityEngine.SceneManagement. Compile errors give snippet `line:col`, capped with `more`.
- Bridge side: `Assembly.Load(byte[])`, run on the main thread, no domain reload. Loaded
  assemblies stay until the next reload: say so in the spec and measure memory per eval.
- Decide and write down: what an endless loop does (main thread; a timeout can report but not
  abort), and whether a snippet's edits form one Undo group so `ug undo` reverts them.
- Measurement: redo purge-squad chores that recorded sessions did with execute-code style calls or
  throwaway editor scripts, using `ug run` and `ug eval`; record calls, answer tokens, and time
  (compile + run).

## Group 2: cameras (O11, O13)
O11: `ug shot` viewpoints, in SPEC-editor-ops.md (`ShotOps.cs`).
- The temporary camera is hidden (`HideFlags.HideAndDontSave`), copies the main camera's settings
  (including URP camera data), and is destroyed afterwards. Tests assert the scene's dirty flag and
  the Undo history are unchanged.
- Build `--from x,y,z --look <object|x,y,z>`, `ug shot scene --frame <object>`,
  `ug shot <object> --angles` (front, back, left, right, top, three-quarter in one PNG contact
  sheet), and an `--orbit` angle × elevation grid. Stitch the sheet in the bridge with `Texture2D`,
  no new image library. Add size options.
- Cover built-in and URP. purge-squad is URP; check what BridgeHost uses, and ask before any
  package download. Overlay canvases keep working; drawing UI Toolkit is U2's job.

O13: `ug camera list|create|priority|aim|body|noise|blend|force|release|brain`.
- Put the spec in `specs/SPEC-camera.md`, or in a section of SPEC-editor-ops.md if it stays
  short. Name its module in SPEC.md's module table.
- Cinemachine 3 (`Unity.Cinemachine`: CinemachineCamera, CinemachineBrain, the procedural
  components Follow, OrbitalFollow, ThirdPersonFollow, PositionComposer, RotationComposer,
  HardLookAt and PanTilt, BasicMultiChannelPerlin with NoiseSettings, blender settings) goes in a
  sub-assembly `UnityGraph.Bridge.Editor.Cinemachine`, gated on com.unity.cinemachine 3.x.
- Without the package, the plain-Camera parts work. Where `ug set` already does the job, point
  there instead of duplicating it.
- Cinemachine 2.x (namespace `Cinemachine`): decide between a clear refusal and support, and
  write it down.
- `force` makes a camera live and `release` undoes that. Every answer shows the live camera and
  any active blend. Undo for every edit; nothing saved.
- Tests need a fixture with the package and one without. The global UPM cache may already hold
  3.1.7 from purge-squad, but adding it to a fixture can still download: ask first, giving the
  package, version and size.

## Group 3: UI Toolkit (U2)
Spike first; write the results into SPEC-play-input.md, which deferred UI Toolkit. The spike
covers three questions:
1. Listing runtime panels and UIDocuments, in play and edit mode.
2. Sending pointer, click and navigation events to an element so its handlers run as they would
   for a real click.
3. Drawing UI Toolkit into `ug shot` (swap `PanelSettings.targetTexture`, or render the panel into
   the camera's texture), including the limits in batch mode and with no graphics device.

If a part can't be done: write why and the best fallback into the spec, build the rest, and report
it. Do not stop.

Then build what todo U2 lists:
- `ug ui` gets `doc/Name` addresses with name, classes, text, screen rect, enabled and visible.
- `ug click` works on those addresses.
- `ug tree --ui <UIDocument>` prints the visual tree.
- `ug shot` draws UI Toolkit; drop the "not drawn" note.
- Check that `ug component add` and `ug asset create` can set up UIDocument, PanelSettings, UXML,
  USS and theme style sheets. Add new commands only where something is missing.
- Asset-index edges (UXML Template and Style `src`, USS `url()`/`resource()`, UIDocument → UXML
  and PanelSettings), written into SPEC-asset-index.md. Ask before a graph schema version bump.
- The UI Toolkit recipe goes into `skills/unity/ui.md`. See the rule above about its uncommitted
  change.

## Group 4: profiler (O12)
Extend `ug perf` (SPEC-editor-ops.md, `StatsOps.cs`, `/stats`) with everything todo O12 lists.
- Counters: `ProfilerRecorder`.
- `ug perf --record <s>`: record frames through `ProfilerDriver` and `HierarchyFrameDataView` to
  get the top markers by self ms, GC alloc and frame spikes.
- `ug perf profiler start|stop|status`: `.raw` files through `Profiler.logFile` and
  `enableBinaryLog`, or `ProfilerDriver.SaveProfile`; areas through `Profiler.SetAreaEnabled`.
- `ug perf memory`: top objects by `Profiler.GetRuntimeMemorySizeLong`, plus `--object <x>`.
- Snapshots: Unity's built-in `Unity.Profiling.Memory.MemoryProfiler.TakeSnapshot` should take
  them without the package (check in 6000.3). Comparing them needs com.unity.memoryprofiler or a
  reader of our own; ask before adding the package to any project.
- Frame Debugger enable/events/disable: its API is internal. Reach it by reflection, check the
  names against 6000.3, and refuse clearly when the members are missing.
- Works in edit and play mode; every list is capped with `more`.

After group 4, tell the user O10 (three-way measurement) is next. `tasks/P1-prompt.md` Part 2
predates the three-way decision and needs updating first.

## Report at the end of a group
For each task, a few lines:
- what was built;
- test counts (dotnet, EditMode, e2e);
- purge-squad numbers;
- MCP actions with no `ug` equivalent (should be none; else why);
- decisions the spec now records;
- open asks.

Then: "Group N done. Next: group N+1 (tasks), in a new chat with tasks/P5-prompt.md." End with
x/y tasks done (the "Not built yet" list plus O10).

Not in this prompt: O10, W1, V1, B1, D1, and the scripted `claude -p` check of PreToolUse
`additionalContext` (it waits for the user's OK).
