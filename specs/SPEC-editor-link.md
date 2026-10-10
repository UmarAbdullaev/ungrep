# Spec: editor-link

Part of unitygraph (see `../SPEC.md`, Decisions #3). Status: **draft** (2026-09-29). Used by
`unity-loop` (compile gate, `ug console`, `ug test`, `ug log`) and later `worker-agent`.

## Objective

Give `ug` live access to the Unity editor that has the project open: its state, a refresh that
compiles changed scripts and reports the errors, the console, test runs and running a static
editor method, so the compile loop (5.3% of purge-squad's context, about 4 calls per edit cycle)
becomes one call that waits for Unity itself. When Unity is not reachable, `Editor.log` answers
what it can.

Live access goes through our own small editor-only package (`com.unitygraph.bridge`), not a
Unity MCP server: no MCP tools in the context (idle cost), no dependency on which MCP server the
user runs, and the answers are shaped for `ug`'s line format.

## Scope

In:

- `unity/com.unitygraph.bridge`: the editor package (Unity 2022.3 LTS or newer). It listens on
  loopback, publishes how to reach it in `Library/unitygraph/bridge.json`, and serves status,
  refresh-and-compile, compile messages, console entries, test runs and static-method runs.
- `src/UnityGraph.EditorLink`: the client (`BridgeClient`) and the `Editor.log` reader.
- CLI: `ug status`, `ug compile`, `ug run`, `ug bridge install|remove`.
- `fixtures/BridgeHost`: a small Unity 6 project that loads the package from this repo, with
  EditMode tests run in batch mode.

Out: the compile gate hook, compact `ug console`, `ug test` and `ug log` (all `unity-loop`,
which uses this client); entering or leaving play mode and scene or asset edits (added later
as `editor-ops`, `SPEC-editor-ops.md`); Unity's official CLI adapter (after v1, Decisions #3); Linux.

## The bridge

- **Loading.** An `[InitializeOnLoad]` class starts the server after every domain reload and
  stops it in `AssemblyReloadEvents.beforeAssemblyReload` and `EditorApplication.quitting`. The
  asmdef is editor-only and has no references outside Unity's editor assemblies. The test-run
  part compiles only when `com.unity.test-framework` is present (asmdef `versionDefines`); the
  package adds no dependency to the user's project.
- **Transport.** `System.Net.HttpListener` (Mono's managed listener: no URL reservation, no
  admin) on `http://127.0.0.1:<port>/`, loopback only. The port is the one used before the last
  domain reload when it is still free, otherwise a free one the OS picks. HTTP/1.1, `GET` only
  (other methods get `405`; Mono itself answers a bodiless `POST` with `411`), parameters in the
  query string, answers in JSON (written by a small writer in the package,
  not `JsonUtility`).
- **Discovery.** `Library/unitygraph/bridge.json`, rewritten on each start and deleted on quit:

  ```json
  {"bridge": "0.1.0", "port": 51234, "token": "<32 hex>", "pid": 20416, "unity": "6000.3.14f1"}
  ```

- **Access.** Every request carries `X-UG-Token: <token>`; a wrong or missing token is `401`.
  Requests with an `Origin` header (a browser) are `403`. Nothing is read from or written to
  paths a request names. The only files the bridge writes are `bridge.json`, `compile.json` and
  `result.txt` (a cut `ug run`/`ug eval` result) in `Library/unitygraph/`.
- **Threading.** The listener thread answers `/status` from a snapshot the main thread refreshes
  every editor update, so status works while the editor is busy importing. Every other request
  is queued to the main thread (`EditorApplication.update`) and answered from there.
- **State across reloads.** The console buffer, the compile counter and the port go into
  `SessionState` before a reload and come back after it. The last compile's messages are also
  written to `Library/unitygraph/compile.json`, so they survive a reload and a crash, and the
  client can read them without the bridge.
- **Idle cost.** A blocked listener thread and one log callback appending to a ring buffer; no
  polling, no timers.

### Endpoints

| Request | Answer |
| --- | --- |
| `GET /status` | `state` (`idle`, `compiling`, `updating`, `playing`, `paused`), `unity`, `bridge`, `compile` (counter of finished compiles), `errors` and `warnings` of the last compile, `console` (id of the newest entry). |
| `GET /refresh` | Runs `AssetDatabase.Refresh()`, then answers whether a compile started and the counter before it (see Compile flow). |
| `GET /compile` | The last compile: counter, start and end (UTC), and each message: assembly, file (project-relative), line, column, severity, code (`CS0103`), text. |
| `GET /console?after=<id>&limit=<n>&types=<error,warning,log>` | Entries newer than `after`, oldest first: id, type, text, the stack trace's first own frame (`file:line`), repeat count for consecutive duplicates. Ring buffer of 2,000 entries since the editor started (entries before the bridge first loaded are not seen). |
| `GET /tests?platform=EditMode&filter=<name>` | Starts a run through `TestRunnerApi`; answers a run id. |
| `GET /tests/result?id=<id>` | `running`, or totals, duration and each failed test with message and first own frame. |
| `GET /run?target=<Type.Method or menu path>` | Runs a static parameterless method of a project assembly (not `UnityEngine`/`UnityEditor`/`System`), or a menu item through `EditorApplication.ExecuteMenuItem` when the target contains `/`. Answers the result's `ToString()` (cut at 2 KB), or the exception type, message and first own frame, and the time taken. Bridge 0.5.0 replaces it: arguments, the editor-ops answer shape, see Running code. |

"Own frame": the first stack frame whose file is under `Assets/` or an embedded package.

### Compile flow

`ug compile` (and the compile gate) ask for a refresh and wait for Unity, then answer:

1. `GET /refresh` returns `{compiling, compile}`. The bridge checks `EditorApplication.isCompiling`
   right after the refresh and again on the next two editor updates before answering, since
   Unity can flag the compile a frame late.
2. If nothing compiles, the answer is the last compile's messages (still true for the code on
   disk).
3. Otherwise the client polls `/status` every 250 ms. While the domain reloads the connection is
   refused; the client keeps polling while the `pid` in `bridge.json` is alive and re-reads
   `bridge.json` for a new port. Done when `compile` has moved past the counter from step 1 and
   `state` is `idle`. Then `GET /compile`.
4. A deadline (default 60 s, `--timeout`) ends the wait with what is known and says so; nothing
   blocks longer (fail open).

A failed compile does not reload the domain, so the bridge stays up and answers at once.

## The client

`BridgeClient.Connect(projectRoot)` reads `bridge.json`, checks the `pid` is alive, and returns
a client or the reason there is none: `no bridge.json` (Unity closed or the bridge not
installed), `stale bridge.json` (the process is gone), `refused` (reloading or starting),
`bridge 0.1.0 is older than ug 0.2.0` (asks for `ug bridge install` again). `HttpClient` to
`127.0.0.1` only, 2 s per request unless a wait needs longer; no proxy. The connect timeout is
500 ms and counts as refused: the bridge's listener thread accepts at once even while the editor
is busy, but Windows retries a refused loopback connect for about 2 s, which would slow every
poll during a domain reload.

### `Editor.log` fallback

When no bridge answers, `ug compile` reads `Editor.log` (Windows
`%LOCALAPPDATA%\Unity\Editor\Editor.log`, macOS `~/Library/Logs/Unity/Editor.log`):

- It belongs to this project when its command line (`-projectpath`) names the project root;
  otherwise the answer says the log is another project's and gives no errors.
- Compile messages are the `<path>(<line>,<col>): error|warning CSxxxx: <text>` lines of the
  last finished compile. Markers, recorded from BridgeHost batch runs on 2022.3.62f1 and
  6000.3.14f1 and the same in both: a compile ends with `*** Tundra build success` or
  `*** Tundra build failed` (`*** Tundra build requires additional run` is not an end); its
  messages come before that line, and after a failure Unity repeats them under
  `## Script Compilation Error for: Csc <assembly>.dll`, `## CmdLine:` and `## Output:`, with
  blank lines between the copies. The whole repeat block belongs to the failed build, and a
  build that succeeded keeps no errors. Repeats are shown once.
- The project comes from `-projectPath <dir>` in the command line (Hub writes `-projectpath`
  and backslashes), else from `Successfully changed project path to: <dir>`.
- The answer starts `Unity not reachable (<reason>); from Editor.log of <time>:` so a stale log
  is never taken for a fresh compile.

The fallback cannot trigger a compile; `compile.json` is preferred over the log when it is newer.

## Commands

All take `--project <dir>`; plain text; exit 0 for any answer, 1 only for no Unity project or an
invalid config. `editor-link` off in config: `editor-link is off in .claude/unitygraph.json`.

```
$ ug status
Unity 6000.3.14f1  idle  bridge 0.1.0
  last compile  0 errors, 2 warnings  14:02:11 (3.4 s)

$ ug compile
compiled in 4.1 s: 2 errors, 1 warning
  Assets/Scripts/Lab/Turntable.cs:17:9  CS0103  The name 'spd' does not exist in the current context
  Assets/Scripts/Lab/LabStage.cs:30:13  CS1061  'Turntable' does not contain a definition for 'Spin'

$ ug compile
no script changes: 0 errors

$ ug run Game.EditorTools.LabTools.OpenLab
ran Game.EditorTools.LabTools.OpenLab in 12 ms
```

- `ug compile` lists errors only, at most 20 lines (`(+N more errors)` after), warnings as a
  count; `--warnings` lists them too. Sorted by file, line, column; duplicates across assemblies
  once.
- `ug bridge install` prints the two ways to add the package: Unity's Package Manager "Add
  package from disk" on `unity/com.unitygraph.bridge/package.json` in the plugin folder (Unity
  edits its own manifest; we never touch `Packages/`), or `ug bridge install --copy`, which
  copies the package into `Assets/Plugins/UnityGraphBridge/` (a vendored folder, so the graph
  ignores it). `ug bridge remove` deletes that folder only. The `unity` skill tells Claude to
  ask the user before either, as both write into the project (SPEC Boundaries).

## Running code (R1, X1; bridge 0.5.0)

Requested 2026-10-10 (user): `ug run` takes arguments and `ug eval` runs any C#, closing MCP for
Unity's `execute_custom_tool` and the execute-code calls behind 16.6% of purge-squad's context
(SPEC Evidence). Both answer in one shape, work in edit and play mode, and are `ug batch` steps.

### `ug run <Type.Method> [args...]`

`GET /run?target=&n=&a0=&a1=...&timeout=` (and `list=`, `menu=` for `--list`) answers in the
editor-ops shape (`{"lines":[...]}` or `{"error":...}`, SPEC-editor-ops Endpoints). The request
is refused while compiling or importing (editor-ops rule 6), menu items too.

```
$ ug run Tools.Spawn 3 0,1,0
ran Game.Tools.Spawn(int count, Vector3 at) in 12 ms
  result: GameObject[3] [Level/Enemy#1, Level/Enemy#2, Level/Enemy#3]
  log: Spawned 3 enemies  Assets/Editor/Tools.cs:14
  changed: Main.unity modified, not saved

$ ug run Tools.Spawn many
did not run Tools.Spawn: count: not a whole number: many
  static GameObject[] Spawn(int count, Vector3 at = default)

$ ug run --list Tools.
Game.Tools  3 static methods
  static GameObject[] Enemies()
  static GameObject[] Spawn(int count, Vector3 at = default)
  static IEnumerator Wave(int size)
```

- **Target.** `Namespace.Type.Method` or `Type.Method`: a static method of any accessibility
  (editor tools are often `private static`) in the project's own assemblies, those loaded from a
  file under the project folder (`Library/ScriptAssemblies` for Assets, embedded, registry and git
  packages; DLL plugins under `Assets/` or an embedded package, not a registry package's precompiled
  DLLs: Unity Version Control alone has thousands of types), minus Unity's (`UnityEngine*`, `UnityEditor*`, `Unity.*`),
  .NET's, test frameworks' and the bridge's. A name that only Unity or .NET has is refused with the
  call to use instead (`UnityEditor.AssetDatabase is Unity's, not the project's: ug eval
  "UnityEditor.AssetDatabase.Refresh()" calls it`). Instance methods, generic method definitions
  and constructors: `ug eval`. A target with a `/` is a menu item, run as before with the File/Quit
  and Build refusals; a menu item takes no arguments.
- **Arguments** are read with `ug set`'s grammar. The text rules are one set of functions in
  `Values` (numbers, booleans, enum names, vectors, colours, bounds, curves, gradients, hashes, layer
  masks, references, `[a, b]` lists, `{a: 1}` fields) that `/set` applies to a `SerializedProperty`
  and `/run` to a parameter's `System.Type`:

  | Parameter type | Text |
  |---|---|
  | integers, `float`, `double`, `decimal` | invariant numbers; integers range-checked |
  | `bool` | `true`, `false`, `1`, `0` |
  | `string`, `char` | the argument as given; one character |
  | enum | a name (case-insensitive) or its number; `[Flags]` names joined by `,` or `\|` |
  | `Vector2/3/4`, `Vector2Int/3Int`, `Rect`, `RectInt`, `Quaternion` | numbers `1,2,3` (a quaternion is euler angles, as `ug inspect` prints it) |
  | `Color`, `Color32` | `#ff8800`, `#ff8800cc`, `1,0.5,0,1` or `1,0.5,0` |
  | `Bounds`, `AnimationCurve`, `Gradient`, `Hash128`, `LayerMask` | as `ug set` |
  | `GameObject`, a component type, any `UnityEngine.Object` | object address, component address (`Canvas:UiHandler`), asset path (`Assets/X.mat`, `Assets/X.png:Sub`) or `null`; a game object address gives its component of the parameter's type |
  | arrays, `List<T>`, `IList<T>`, `IReadOnlyList<T>`, `IEnumerable<T>`, `ICollection<T>` | `[a, b, c]`, `a, b, c` or `empty`, items by the element type; a `params` array also takes the remaining arguments one by one |
  | `T?` | `null` or a `T` |
  | `System.Type` | a type name: the project's types first, then Unity's, then .NET's (as `ug api` resolves) |
  | a type with a static `Parse(string)`: `Guid`, `DateTime`, `TimeSpan`, `Version`, Newtonsoft `JObject`/`JToken` (what MCP for Unity custom tools take) | that `Parse`, the `(string, IFormatProvider)` overload with the invariant culture when there is one |
  | other structs and classes with a parameterless constructor | `{count: 3, offset: 1,2,3}` or `count: 3`: public fields and settable properties by name |
  | `object` | the text as a string |

- **Overloads.** Candidates are the methods with that name. One fits when the argument count fits
  (optional parameters may be left out and take their defaults, `out` parameters take no argument,
  `params` takes any number) and every argument parses. Exactly one fit runs. Several fits: the one
  whose arguments read most exactly wins. Per argument, exact is a whole number for an integer
  parameter, a number for a floating one, an enum name, `true`/`false`, the right count of numbers for
  a vector, an address or `null` for an object; a whole number for a floating parameter is one step
  looser; `string`, `object` and `Parse` are the loosest. A tie answers `ambiguous call` and the tied
  overloads, one per line. No fit: with one candidate its first problem and its signature, with
  several every overload (at most 10, `+N more`).
- **Named arguments.** `name=value` sets the parameter `name` (so optional ones before it can be
  skipped), after the positional ones. A positional text that starts with a parameter name and `=`
  is written with a leading `\` (`\count=3` is the text `count=3`).
- **`out` and `ref`.** After the call each `out` and `ref` value prints as `  out name: value`.
- **Awaitables and `IEnumerator`.** A returned awaitable (anything with `GetAwaiter()`: `Task`,
  `ValueTask`, Unity's `Awaitable`, UniTask) is awaited, a returned `IEnumerator` is stepped
  like an editor coroutine, both across editor updates, so the editor keeps running: one `MoveNext`
  per update; a yielded `IEnumerator` runs nested; `AsyncOperation` waits for `isDone`,
  `CustomYieldInstruction` for `keepWaiting`, `WaitForSeconds` and `WaitForSecondsRealtime` for
  their time in real seconds (the first through its internal `m_Seconds` field: one update when the
  field is missing); anything else waits one update. A `Task<T>` prints its result (a plain `Task`
  none), an `IEnumerator` `finished after N steps` (N yields, nested ones counted). `--timeout`
  (default 60 s) bounds the wait: past it the answer is `<what> still running after 60 s; it goes
  on in the editor`. Neither can be stopped from outside.
- **Synchronous code** runs on the main thread inside the request: the editor waits for it. A
  method that never returns freezes the editor; ug gives up after `--timeout` plus 5 s and says
  the method may still be running. Nothing stops it from outside: `Thread.Abort` on Unity's main
  thread would leave the editor in an unknown state.
- **Undo.** The call runs in one undo group named `ug run <Type.Method>`: what it records through
  `Undo` (`Undo.RecordObject`, `Undo.AddComponent`, ...) is reverted by one `ug undo`. Changes made
  without `Undo` calls are not recorded, Unity's rule for every editor script. Only the synchronous
  part is in the group: what an awaited or stepped call records on later updates lands in the
  editor's own groups as it comes (collapsing them later could swallow other commands' changes).
  Unity names a group after its records, so once the code records, `ug undo` says the code's own
  name (`undid spawn` for `Undo.RegisterCreatedObjectUndo(go, "spawn")`); naming the group again
  after the call does not change that (tried). Nothing is saved; a loaded scene that was clean before the call and modified after is named on a `changed:`
  line (`changed: Main.unity modified, not saved`; edit mode only: play-mode changes are lost on
  stop anyway).
- **Console.** What the call logged follows the result, at most 5 entries in order (`log:`,
  `warning:`, `error:`, first line and own frame), then `(+N more: ug console --after <id>)`.
- **`ug run --list [filter]`**: the static methods of the project's assemblies whose `Type.Method`
  contains `filter` (case-insensitive; a trailing `.` means the type's own), grouped by type with
  signatures as `ug api` prints them and defaults shown, at most 40 lines, then `(+N more: give a
  longer filter)`; without a filter, the types that have static methods with their counts.
  `ug run --list --menu [filter]`: menu items (`TypeCache` `[MenuItem]` methods of every loaded
  assembly, Unity's too) as `path  Type.Method`. Offline, `ug find` finds methods in the graph.
- A bridge older than 0.5.0: `bridge 0.4.0 is too old for ug run: run ug bridge install again`.

### Answer shape (shared by `ug run` and `ug eval`)

- First line `ran <what> in <ms> ms`, or `<what> threw after <ms> ms: <ExceptionType>: <message>`
  and `  at <file:line>`, the first frame in the project's own files (`StackFrames`). A
  `TargetInvocationException` or `AggregateException` is unwrapped to the exception inside. The
  message is its first line, cut at 300 characters. A throw is an error answer (`{"error":...}`), so
  `ug batch` stops at it.
- `  result: <value>` through one formatter, `Values.Show`:

  | Value | Printed |
  |---|---|
  | `null` | `null`; a destroyed Unity object `missing (destroyed <Type>)` |
  | numbers | invariant; `float` 7 and `double` 15 significant digits |
  | `bool`, enums | `true`, `false`; enum names (`A, B` for flags) |
  | `string`, `char` | quoted, `\n` for line breaks, cut at 200 characters |
  | vectors, quaternions, rects, colours, bounds, curves, gradients | as `ug inspect` prints them (`1, 2, 3`; euler angles; `r, g, b, a`; `Color32` as `#rrggbbaa`) |
  | tuples, `KeyValuePair` | `(1, "a")`; `key: value` |
  | `GameObject` | its address (`Level/Enemy#2`, `Assets/X.prefab:Child`) |
  | component | `address:Component`, the form `ug set` takes for references |
  | other `UnityEngine.Object` | asset path, `path:sub`, `builtin <name>` or `<name> (not saved)` |
  | dictionaries | `Dictionary<string, int>[3] {a: 1, b: 2, c: 3}` |
  | other collections | `List<int>[25] [1, 2, 3, ... +15 more]`, the first 10 items (an array `GameObject[3] [...]`); a collection that overrides `ToString` (a Newtonsoft `JObject`) prints that text on one line |
  | `Type`, delegates | the type's full name; `delegate Type.Method` |
  | `IFormattable` and `IConvertible` values (`DateTime`, `Guid`, `TimeSpan`, Newtonsoft `JValue`) | `ToString` with the invariant culture |
  | other objects | `TypeName {field: value, prop: value}`: public instance fields and properties one level deep, at most 20; deeper values print as their type name, or their `ToString` when the type overrides it; a getter that throws prints `<threw X>` |

- When anything was cut, the whole value (strings whole, collections up to 1,000 items, two levels
  deep) goes to `Library/unitygraph/result.txt` and the result line ends `(+more:
  Library/unitygraph/result.txt)`: running the code again would repeat its side effects, reading
  the file does not.
- Then the `out`, console and `changed:` lines.

## Performance targets

| Operation | Target |
| --- | --- |
| `ug status`, fresh process, Unity idle | ≤ 300 ms |
| `/status` round trip in process | ≤ 20 ms |
| `ug compile` with no script changes | ≤ 1.5 s |
| Client overhead on a compile (time beyond Unity's own compile and reload) | ≤ 1 s |
| Bridge start after a domain reload | ≤ 20 ms on the main thread |

## Testing

- **Client** (`tests/UnityGraph.EditorLink.Tests`): a fake bridge (an in-test `HttpListener`
  on loopback replaying recorded answers): discovery (missing, stale pid, wrong token, older
  bridge), the compile flow including a refused connection during a reload and a new port after
  it, the deadline, error sorting, dedup and the 20-line cap.
- **Editor.log:** golden answers on logs recorded from `fixtures/BridgeHost` batch runs (2022.3
  format added when recorded), another project's log, a log with no compile.
- **Bridge:** EditMode tests in `fixtures/BridgeHost` run in batch mode
  (`Unity -batchmode -projectPath fixtures/BridgeHost -runTests -testPlatform EditMode`): JSON
  writer, token and `Origin` checks, console ring buffer and dedup, own-frame extraction,
  compile message conversion, `run` resolution and refusals; for R1 argument parsing per type,
  overload choice (count, exactness, ties), optional, named, `params`, `out`, `Task` and
  `IEnumerator` waits, the formatter per row of its table, the exception shape, logs, `changed:`
  and the undo group.
- **End to end** (env-gated on `UG_UNITY`, the editor executable): start BridgeHost in batch mode
  without `-quit`, wait for `bridge.json`, then `ug status`, a clean `ug compile`, add a file
  with an error and `ug compile` again, `ug run` on a fixture method, stop the editor. Times go
  in Measured.
- Coverage ≥ 80% lines on EditorLink.

## Boundaries

- Always: loopback only with a token; fail open with a reason; the bridge writes only in
  `Library/unitygraph/`.
- Ask first: installing the bridge into a project (each project); a new endpoint that changes
  the project (play mode, asset edits); any non-loopback address.
- Never: edit `Packages/` or the user's manifest; send anything off the machine; read a file a
  request names. (Until 2026-10-10 also: run a method that takes arguments; R1 and X1 lifted
  that at the user's request: `ug run` and `ug eval` run what the user asks, unrestricted.)

## Measured

E2 smoke run (2026-09-29), BridgeHost in batch mode on Unity 6000.3.14f1 (`-batchmode
-nographics -ugBridge`, no `-quit`), requests with curl over loopback:

- `EditorApplication.update` ticks in batch mode, so queued requests are answered: `/refresh`
  5–45 ms, `/compile`, `/console`, `/run`, `/tests` 1–40 ms; `/status` 2–24 ms.
- A file with an error: `/refresh` said `compiling`, `/status` reached `compile: 1` in 1.3 s,
  and `/compile` gave `Assets/Editor/Broken.cs`, line 1, column 78, `CS0103` and its text.
- Fixing it: the successful compile reloaded the domain; the bridge came back on the same port
  and token within 2.1 s of the refresh, with the console history and compile counter kept.
- `/run` on a fixture method, on one that throws (frame `Assets/Editor/FixtureMethods.cs:12`),
  on a menu item, and on `UnityEditor.EditorApplication.Exit` (refused); `/tests` with a filter
  ran one EditMode test and `/tests/result` gave its totals.
- Killing the editor leaves `bridge.json` behind: the client's stale check (pid) matters.
- Mono's traces name frames without symbols `in <41d29b35...>:0`; the first EditMode run caught
  `Path.IsPathRooted` throwing on them, fixed in `StackFrames.Relative`.

E4 end-to-end test (2026-09-30, `UG_UNITY` set), a copy of BridgeHost with its Library opened
in batch mode on Unity 6000.3.14f1, commands run in process, three runs:

| Step | Time |
| --- | --- |
| Editor start to bridge `idle` | 7.3–8.3 s |
| `ug compile`, no script changes | 25–56 ms |
| `ug compile`, a new file with an error | 0.8–1.2 s |
| `ug compile` after the fix, with the domain reload | 2.1–2.2 s |
| `ug status`, fresh `ug.exe` process, median of 5 | 164–173 ms |

The compile times match the E2 curl run, so the client adds no measurable wait. Killing the
editor gave `stale bridge.json: Unity (pid N) is not running` for `status`, and `compile` fell
back to the project's Editor.log. That fallback first reported the fixed error again: the
repeat block after a failed build (above) had ended at its `## CmdLine:` line, so the last copy
of the error was counted in the next, successful build. Fixed in `EditorLog` with a regression
test.

E5 on purge-squad (2026-09-30), the user's open editor, Unity 6000.3.14f1, bridge added with
`ug bridge install --copy` at the user's request:

- The copy sat unnoticed until the Unity window got focus: an editor in the background does not
  import new files, and with no bridge running yet nothing outside can ask it to. The bridge
  answered 78 s after the copy, when the user clicked the editor. `ug bridge install --copy` now
  says to click it once.
- With the Claude app in the foreground and Unity in the background: `ug status`, fresh
  process, 138–175 ms over 7 runs; `ug compile` with no script changes 159–223 ms over 5 runs.
  The background throttle does not hold a refresh back noticeably.

R1 on purge-squad (2026-10-10), the editor in batch mode with `-ugBridge`, bridge 0.5.0 copied in
with `ug bridge install --copy`, fresh `ug.exe` process per call:

| Call | Answer | Time |
| --- | --- | --- |
| `ug run --list` (782 types with static methods) | 42 lines, 2,056 bytes | 286 ms |
| `ug run --list SaveMenu.` | 4 lines, 99 bytes | 246 ms |
| `ug run --list Ads` (7 types) | 24 lines, 2,010 bytes | 250 ms |
| `ug run --list --menu GunLab` | 3 lines, 122 bytes | 183 ms |
| `ug run SaveMenu.CanDelete` (private static) | 50 bytes, ran in 7.9 ms | 289 ms |
| `ug run AdsrEnvelop.Map 5 0 10 0 100`, and with `name=value` | 141 bytes, ran in 1.4–1.7 ms | 236–238 ms |
| `ug run Ads.Raise main Bogus` (bad enum) | 184 bytes, the names and signature | 240 ms |
| `ug run Ads.Nope` | 178 bytes, the type's static methods | 235 ms |

- The first `--list` gave 3,580 types: Unity Version Control's precompiled DLLs in
  `Library/PackageCache` counted as the project's. Registry packages' DLLs are no longer own.
- Before R1 a method with arguments took a throwaway editor script: write it, `ug compile` (a
  domain reload: 10.9 s on purge-squad), `ug run`, delete it, compile again: 5 calls and two
  reloads where `ug run` takes one call and no reload.
- The MCP calls it replaces, from the Evidence snapshot: 577 execute-code calls (444 `eval`, 129
  `run_script`, 4 `eval_file`), code 519 bytes on average, answers 584 bytes on average (median
  197, p90 1,049). Only 2 were a single static call that `ug run` takes as is; the rest is `ug
  eval`'s (X1).

## Open points

1. Whether PlayMode test runs (which reload the domain) need more than `TestRunnerApi`'s own
   callbacks re-registered after the reload.
