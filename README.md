<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/logo-dark.svg">
    <img src="assets/logo.svg" width="140" alt="unitygraph logo: a cube drawn as a graph, with a node at its centre">
  </picture>
</p>

<h1 align="center">unitygraph</h1>

<p align="center">
  <em>Grep reads text. <code>ug</code> reads the wiring.</em>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/version-0.9.0%20beta-100f0e?style=flat-square" alt="Version 0.9.0 beta">
  <img src="https://img.shields.io/github/actions/workflow/status/UmarAbdullaev/unitygraph/ci.yml?branch=main&style=flat-square&label=CI&color=100f0e" alt="CI">
  <img src="https://img.shields.io/badge/Claude%20Code-plugin-100f0e?style=flat-square" alt="Claude Code plugin">
  <img src="https://img.shields.io/badge/Unity-2022.3%2B%20%C2%B7%206-100f0e?style=flat-square" alt="Unity 2022.3 or newer, Unity 6">
  <img src="https://img.shields.io/badge/runs%20on-Windows%20%C2%B7%20macOS%20%C2%B7%20Linux-100f0e?style=flat-square" alt="Windows, macOS, Linux">
  <img src="https://img.shields.io/badge/license-MIT-100f0e?style=flat-square" alt="MIT license">
</p>

<p align="center">
  <img src="assets/hero.png" width="880" alt="unitygraph, beta 0.9. Grep reads text, ug reads the wiring. Against Unity's own MCP on the same editor tasks: 43% fewer calls, 45% fewer tokens, 67% less per session. And 80 tokens is all ug adds to a conversation; Unity's MCP adds 1,509.">
</p>

<p align="center">
  <strong>Your Unity project's wiring, one <code>ug</code> call away.</strong><br>
  <strong>−43% calls · −45% tokens · −67% per session</strong> against Unity's own MCP.<br>
  <strong>And 80 tokens</strong> in every session. Unity's MCP: 1,509.<br>
  <sub>A Claude Code plugin. 10 tasks in a live Unity 6 editor, against Unity's own MCP and MCP for Unity. <a href="#numbers">Details</a>.</sub>
</p>

---

You know the bug. Someone renames a serialized field, the code compiles, and three prefabs quietly lose their values. Nobody notices until the boss spawns with 0 HP.

unitygraph puts your project's wiring inside Claude: code, scenes, prefabs and the GUIDs between them, in one graph. And it drives the open editor.

## Numbers

<p align="center">
  <img src="assets/editor-chores.png" width="880" alt="Benchmark, 10 tasks in a live Unity 6 editor. Tool calls: MCP for Unity 44, Unity's own MCP 44, unitygraph 25, 43% fewer. Tokens: 5,430, 3,524 and 1,939, 45% fewer. Whole session, setup included: 11,576, 7,960 and 2,619, 67% fewer.">
</p>

Against grep and Read, on 19 code, asset and editor questions: 31 calls instead of 71, every answer right on both. Claude still greps when one pattern answers. Suite and results: [`bench/`](https://github.com/UmarAbdullaev/unitygraph/tree/main/bench).

**Calls cost more than tokens.** Every call is a turn, and every turn re-reads the conversation. An MCP server's tool list sits in every session, about 1,500 tokens. `ug` is one line.

**Not faster.** Wall time is mostly Unity entering play mode and compiling.

## Before / after

<p align="center">
  <img src="assets/before-after.png" width="880" alt="What breaks if I rename this field? By hand, five steps: search the code, open the .meta file for the script's ID, search every scene and prefab for it, open the prefab for the saved value, search again for overrides, then piece the answers together. With unitygraph, one step: ug impact Turntable._speed shows what reads it, what changes it, the prefab it's saved in, and the scene and prefab variant that override it, each with file and line.">
</p>

You ask what a rename breaks. Grep finds the field, then the GUID, then the prefabs, then the overrides, and Claude stitches five answers together. `ug impact` answers once, every line with `file:line`.

Events come with their handlers:

```console
$ ug refs Turntable.Rotated
Game.Lab.Turntable.Rotated  event  Assets/Scripts/Lab/Turntable.cs:15  [exact]
  raised by        Game.Lab.Turntable.Rotate  Assets/Scripts/Lab/Turntable.cs:20
  subscribed by    Game.Lab.Spectator.Watch   Assets/Scripts/Lab/Turntable.cs:37  handler Game.Lab.Spectator.OnRotated
  subscribed by    Game.Lab.Spectator.Watch   Assets/Scripts/Lab/Turntable.cs:38  handler lambda
  unsubscribed by  Game.Lab.Spectator.Leave   Assets/Scripts/Lab/Turntable.cs:44  handler Game.Lab.Spectator.OnRotated
4 references
```

## How it works

<p align="center">
  <img src="assets/how-it-works.png" width="880" alt="How it works: it reads your scripts without building them, and your scenes and prefabs down to every value and override, then connects the two into one graph on your machine. Claude asks it one question and gets one answer. If you want, it also works the open editor: compile, test, play and edit, all undoable.">
</p>

The graph reads files: no build, no open editor. It lives in `Library/`, stays on your machine, and reindexes what Claude edits.

The editor bridge is optional. Every edit is an Undo step, and nothing is saved until you say so.

Hooks stay quiet until they matter: a warning before a field rename orphans prefab data, and compile errors handed back before Claude calls it done.

## Install

**Claude Code**, as two separate prompts:

```
/plugin marketplace add UmarAbdullaev/unitygraph
```
```
/plugin install unitygraph@unitygraph
```

Start Claude Code in your Unity project and ask. You need the [.NET 8 runtime](https://dotnet.microsoft.com/download/dotnet/8.0) or newer and Unity 2022.3 or newer. `ug doctor` checks your setup.

**The editor bridge**, for compile, tests, play mode and edits:

```bash
ug bridge install --copy
```

Run it again after a plugin update. `ug bridge remove` takes it out.

Only install unitygraph from [`UmarAbdullaev/unitygraph`](https://github.com/UmarAbdullaev/unitygraph) on GitHub.

## Commands

| Command | What it does |
| --- | --- |
| `ug refs` | Everything that points at it: callers, readers, writers, handlers, implementers, the prefabs that use it |
| `ug impact` | What a change breaks, including the prefabs and scenes that hold its data |
| `ug explain` | What it is and where it's used |
| `ug find` · `ug outline` | Find by name or GUID; a script's members or a scene's tree |
| `ug compile` · `ug test` · `ug console` | Compile, run tests, read the console, in the open editor |
| `ug play` · `ug click` · `ug press` | Play-test, each step followed by what changed |
| `ug set` · `ug object` · `ug prefab` · `ug undo` | Edit scenes and prefabs, as Undo steps; `ug save` writes them |
| `ug shot` | A picture of the game or scene view |
| `ug stats` | Where your Claude Code tokens went on this project |

`ug <command> --help` has the rest.

## FAQ

**Why not just grep?**
Grep is great when one pattern answers. Not when the answer is a GUID, a fileID and an override three folders apart.

**Does Unity need to be open?**
Only to compile, test, play or edit. The graph reads files.

**Will it touch my project?**
Queries only read. Edits are Undo steps. Nothing is saved until `ug save`.

**Does it phone home?**
No telemetry, no account. `ug api` and `ug docs` fall back to docs.unity3d.com when nothing local matches; `"docs": { "online": false }` in `.claude/unitygraph.json` turns that off.

**Claude greps instead of using `ug`.**
Name it: *"use ug refs on Health.Died"*. Or load the skill with `/unitygraph:unity`.

**Is it a Unity MCP server?**
No. That's the point.

**Why `ug`?**
Two letters, typed hundreds of times a day. Also the sound you make when a prefab loses its references.

## Status

Beta. Tested with Unity 2022.3 and Unity 6 on Windows. macOS and Linux pass CI; the editor bridge is untested there. Issues welcome.

<details>
<summary>Building from source</summary>

The source is on [`main`](https://github.com/UmarAbdullaev/unitygraph/tree/main). You need the .NET 10 SDK:

```bash
dotnet test --solution unitygraph.sln
dotnet publish src/UnityGraph.Cli -c Release -o dist/ug
claude --plugin-dir .
```

</details>

## License

[MIT](LICENSE). Third-party notices in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

unitygraph is not affiliated with or endorsed by Unity Technologies. Unity is a trademark or registered trademark of Unity Technologies.
