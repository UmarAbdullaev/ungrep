# unitygraph

**Who calls it, who listens, which prefab holds its data, what breaks if it changes: one `ug` call, with `file:line`.**

A Claude Code plugin for Unity projects. It indexes your C# and your scenes, prefabs and ScriptableObjects into one local graph, so Claude answers code *and* inspector-wiring questions without grepping YAML. A small editor bridge lets Claude compile, test, play and edit the open editor, with every edit an Undo step.

![version](https://img.shields.io/badge/version-0.9.0%20beta-orange)
![license](https://img.shields.io/badge/license-MIT-blue)
![Unity](https://img.shields.io/badge/Unity-2022.3%20LTS%20%7C%206-black?logo=unity)
![.NET](https://img.shields.io/badge/.NET-8%2B%20runtime-512BD4?logo=dotnet)
![platforms](https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey)

| −56% tool calls | −43% tokens | 19/19 correct |
| :---: | :---: | :---: |
| against grep on 19 tasks | of answers read | same tasks, same project |

<sub>Scripted benchmark on one Unity 6 game, against grep and a Unity MCP server; how to read these numbers is under [Benchmarks](#benchmarks).</sub>

---

## One call instead of five

Ask Claude *"what breaks if I rename `Turntable._speed`?"* With grep it searches the C#, then the `.meta` files for the script's GUID, then every scene and prefab for that GUID, then the overrides. With unitygraph:

```console
$ ug impact Turntable._speed
Game.Lab.Turntable._speed  field  Assets/Scripts/Lab/Turntable.cs:11  serialized  [exact]
  affects        3 places in 1 file: 1 read, 2 writes; 1 asset may hold data (1 prefab); 2 overrides
  readers        1
    Game.Lab.Turntable.Rotate  Assets/Scripts/Lab/Turntable.cs:20
  writers        2
    Game.Lab.Turntable.Rotate  Assets/Scripts/Lab/Turntable.cs:19
    Game.Lab.Turntable.Stop    Assets/Scripts/Lab/Turntable.cs:23
  serialized     1 component of Game.Lab.Turntable in 1 asset  [heuristic]
    1 Turntable  Assets/Prefabs/Turntable.prefab:33
  overridden in  2
    Lab Turntable: _speed = 3      Assets/Scenes/Lab.unity:133
    Turntable Variant: _speed = 5  Assets/Prefabs/Turntable Variant.prefab:19
```

Events, with each handler and where it unsubscribes:

```console
$ ug refs Turntable.Rotated
Game.Lab.Turntable.Rotated  event  Assets/Scripts/Lab/Turntable.cs:15  [exact]
  raised by        Game.Lab.Turntable.Rotate  Assets/Scripts/Lab/Turntable.cs:20
  subscribed by    Game.Lab.Spectator.Watch   Assets/Scripts/Lab/Turntable.cs:37  handler Game.Lab.Spectator.OnRotated
  subscribed by    Game.Lab.Spectator.Watch   Assets/Scripts/Lab/Turntable.cs:38  handler lambda
  unsubscribed by  Game.Lab.Spectator.Leave   Assets/Scripts/Lab/Turntable.cs:44  handler Game.Lab.Spectator.OnRotated
4 references
```

Both examples run on the test project in [`fixtures/CodeProject`](https://github.com/UmarAbdullaev/unitygraph/tree/main/fixtures/CodeProject).

## Install

You need:

- Claude Code (CLI or desktop app)
- the [.NET 8 runtime or newer](https://dotnet.microsoft.com/download/dotnet/8.0)
- Unity 2022.3 LTS or newer, with Asset Serialization set to Force Text (Unity's default)

Unity's bundled .NET is too old for `ug`; the launcher finds an installed runtime by itself.

In Claude Code:

```
/plugin marketplace add UmarAbdullaev/unitygraph
/plugin install unitygraph@unitygraph
```

Or from a shell:

```bash
claude plugin marketplace add UmarAbdullaev/unitygraph && claude plugin install unitygraph@unitygraph
```

Then start Claude Code in your Unity project's folder (the one with `Assets/` and `ProjectSettings/`). The first question builds the graph in a few seconds; after that, edits are reindexed as Claude makes them. Run `ug doctor` to check the setup.

<details>
<summary>The editor bridge (optional: compile, tests, play mode, live edits)</summary>

The graph needs no editor. To let Claude work in the open editor, add the bridge package to your project. It is editor-only and listens on `127.0.0.1` only, with a token:

```bash
ug bridge install --copy
```

This copies it to `Assets/Plugins/UnityGraphBridge/`. Unity imports it when its window next has focus; then `ug status` shows the editor. After a plugin update, run the same command again to refresh the copy (`ug doctor` says when it is behind). `ug bridge remove` takes it out.

Package Manager's *Install package from disk* also works, but it links the plugin's versioned folder, which the next plugin update deletes; the copy survives updates.

</details>

<details>
<summary>Updating and removing</summary>

```bash
claude plugin marketplace update unitygraph
claude plugin update unitygraph@unitygraph
claude plugin uninstall unitygraph@unitygraph
```

The graph lives in your project's `Library/unitygraph/`, which Unity already keeps out of version control. Delete that folder to drop it.

</details>

## What you get

**The graph** (no editor needed): code from Roslyn, assets from the YAML and `.meta` GUIDs.

| Command | Answers |
| --- | --- |
| `ug refs <symbol>` | Everything that points at it: callers, readers and writers, subscribers with their handlers, implementers (through derived interfaces too), overrides, the scenes and prefabs that use it |
| `ug impact <symbol>` | What a change breaks, grouped by kind, and the assets that hold its serialized data or override it |
| `ug explain <symbol>` | What it is: location, Unity facts, members, users, where it sits in scenes and prefabs |
| `ug find <text>` | Types, members, assets and game objects by name |
| `ug outline <file>` | A `.cs` file's members, or a scene's or prefab's object tree, with line numbers and no bodies |
| `ug view <symbol>` | An interactive HTML graph of the neighbourhood, for you; Claude only sees the file path |

A `<symbol>` is a type, `Type.Member`, an asset path, name or GUID, a game object, or a component like `Arena.unity > Run > RunDirector`. Answers stay within a token budget and end with a `more` line when cut. Matches found by name only are marked `[heuristic]`.

**The editor** (through the bridge):

- **loop:** `ug compile` (refresh, wait, list errors), `ug test`, `ug console`, `ug log`, `ug run <Type.Method>`, `ug eval "<C#>"`
- **read:** `ug scene`, `ug tree`, `ug inspect`, `ug ui`, `ug camera`, `ug perf`, `ug shot` (game view, scene view or angles around an object, saved as a picture)
- **edit:** `ug object`, `ug component`, `ug set`, `ug event`, `ug prefab`, `ug asset`, `ug package`, `ug undo`/`ug redo`; nothing is saved until `ug save`
- **play-test:** `ug play --for 5`, `ug click`, `ug nav`, `ug press`, `ug stick`, `ug tap`, `ug swipe`, `ug wait`, each followed by a one-line digest of what changed
- **look up:** `ug api <Type>` from the loaded assemblies, `ug docs <page>` from the local Unity docs

`ug batch "a" "b" ...` runs several commands in one call. `ug <command> --help` lists every option.

**Hooks**, silent unless they have something to say:

- **Guards** warn before a serialized field is renamed or removed (naming how many assets hold its data), before a `[SerializeReference]` type moves, when a shell `mv`/`cp`/`rm` would orphan a `.meta`, and on edits to vendored folders.
- **Compile gate:** when Claude finishes, the hook waits for Unity's compile and hands back any errors, so a broken build is never the last word.
- **Reindex on edit** keeps the graph current as files change.

**`ug stats`** shows where your Claude Code tokens went on this project, read from your local transcripts:

```console
$ ug stats --since 2026-09-01
sessions 76 | turns 3,961 | context 954.5M | avg/turn 241.0k
context charged to tool calls 891.0M (93.4%)

category                            calls  context  share
shell: search/read                    790   167.3M  17.5%
unity: eval/run_script                663   138.3M  14.5%
write: .cs                            558    90.3M   9.5%
shell: git                            243    59.2M   6.2%
read/search: unity yaml               257    54.7M   5.7%
```

Run it before and after you install to see what changed for you.

## Benchmarks

`ug bench` runs a suite of questions and editor chores against several arms on one project, and checks every answer against a key. These numbers come from a mid-sized Unity 6 game, 19 tasks: 5 code navigation, 5 asset wiring, 9 editor chores.

| Arm | Tool calls | Tokens read | Correct |
| --- | ---: | ---: | :---: |
| grep and Read, plus a Unity MCP server for the chores | 71 | 7,394 | 19/19 |
| [codegraph](https://github.com/colbymchenry/codegraph), with grep and the MCP server | 69 | 46,537 | 19/19 |
| [Graphify](https://github.com/Graphify-Labs/graphify), with grep and the MCP server | 73 | 16,472 | 19/19 |
| **unitygraph** | **31** | **4,195** | **19/19** |

How to read them:

- **Calls matter more than tokens.** Every tool call starts a turn, and every turn re-reads the whole conversation, so a call saved is worth far more than the bytes of its answer.
- **These are scripted calls,** written by someone who knew where to look. That flatters grep: its numbers are its floor.
- **In real sessions Claude chooses for itself.** In 24 runs where Claude picked its own tools (`--mode agent`), it used `ug` for 11, mostly the structural questions: what a change breaks, serialized data, implementers through interfaces. For questions one grep pattern answers, it used Grep, at about the same cost. Every answer was right either way.
- **Always-on cost:** about 160 tokens a session (the skill's listing line and a one-line note at session start). A Unity MCP server's tool list costs about 1,500.
- **Not measured yet:** savings over whole real sessions. `ug stats` measures them on your project.

The suite, keys and every result are in [`bench/`](https://github.com/UmarAbdullaev/unitygraph/tree/main/bench), the method in [`specs/SPEC-bench.md`](https://github.com/UmarAbdullaev/unitygraph/blob/main/specs/SPEC-bench.md).

## How it works

```
 Assets/**/*.cs ──Roslyn──┐
                          ├──> Library/unitygraph/graph.db (SQLite) ──> ug refs / impact / explain
 *.unity *.prefab *.asset ┘        types, members, calls (read / write / subscribe / raise),
 *.meta (GUIDs) ──YAML────┘        assets, objects, components, field values, overrides

 ug compile / test / play / set ──HTTP 127.0.0.1──> bridge package in the open editor (Undo for every edit)
```

- **Code:** Roslyn reads the `.csproj` files Unity generates and binds every symbol, with no build and no editor. A reference records how a member is used: called, read, written, subscribed to with which handler, raised.
- **Assets:** scenes, prefabs (variants and nested instances with their overrides), ScriptableObjects, materials and Addressables, joined to scripts through `.meta` GUIDs.
- **Freshness:** the PostToolUse hook reindexes the files Claude edits; anything else changed is picked up at the next query.
- **Bridge:** a UPM package, editor-only. Without it, `ug compile` falls back to reading `Editor.log`.

## Privacy

Everything runs on your machine. There is no telemetry and no account. `ug stats` reads the transcripts Claude Code already keeps in `~/.claude`. The one network call: when `ug api` or `ug docs` find nothing locally, they look the name up on docs.unity3d.com; set `"docs": { "online": false }` in `.claude/unitygraph.json` to turn that off.

## Configuration

Optional, in the Unity project at `.claude/unitygraph.json`. Only what differs from the defaults:

```jsonc
{
  "answerBudget": 1200,           // tokens per query answer, 200 to 8000
  "modules": {                    // all on by default
    "asset-index": true, "code-index": true, "editor-link": true, "guards": true,
    "play-input": true, "query": true, "unity-loop": true, "viewer": true
  }
}
```

`ug doctor` names any setting it doesn't know.

## Troubleshooting

**`unitygraph: needs the .NET 8 runtime or newer`.** Install it from the link above. If `DOTNET_ROOT` points at Unity's bundled runtime (6.0), the launcher skips that and looks in the default install folders.

**Claude greps instead of using `ug`.** Name the command: *"use ug refs to find who subscribes to Health.Died"*. Or load the skill with `/unitygraph:unity`.

**`Unity not reachable`.** The editor isn't open, or the bridge isn't installed (`ug bridge install --copy`). An editor in the background imports a new bridge only once its window gets focus.

**The Unity project is in a subfolder of the repository.** Start Claude Code in the Unity project's folder, or pass `--project <dir>` to `ug`.

**Something else.** `ug doctor` checks the runtime, the project, the config, the graph and the bridge. Hook failures never block Claude; they are logged in `Library/unitygraph/hook-errors.log`.

## Status

0.9.0 is a beta. It is developed and tested with Unity 2022.3 and Unity 6 on Windows. On macOS and Linux, CI runs the full .NET test suite and the launcher; the bridge has not been run in a macOS or Linux editor yet. Binary-serialized assets are reported, not indexed. Issues and reports are welcome.

## Building from source

The source is on the [`main`](https://github.com/UmarAbdullaev/unitygraph/tree/main) branch; the default branch holds the built plugin. You need the .NET 10 SDK (pinned in `global.json`; it builds for .NET 8):

```bash
dotnet test --solution unitygraph.sln
dotnet publish src/UnityGraph.Cli -c Release -o dist/ug
claude --plugin-dir .
```

`build/package.sh` builds the release payload. The design is in [`SPEC.md`](https://github.com/UmarAbdullaev/unitygraph/blob/main/SPEC.md) and [`specs/`](https://github.com/UmarAbdullaev/unitygraph/tree/main/specs).

## License

[MIT](LICENSE). Third-party components are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
