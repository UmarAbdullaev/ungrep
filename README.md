<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/logo-dark.svg">
    <img src="assets/logo.svg" width="140" alt="ungrep logo: a cube drawn as a graph, with a node at its centre">
  </picture>
</p>

<h1 align="center">ungrep</h1>

<p align="center">
  <em>Grep reads text. <code>ug</code> reads the wiring.</em>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/version-0.9.0%20beta-100f0e?style=flat-square" alt="Version 0.9.0 beta">
  <img src="https://img.shields.io/badge/Claude%20Code-plugin-100f0e?style=flat-square" alt="Claude Code plugin">
  <img src="https://img.shields.io/badge/runs%20on-Windows%20%C2%B7%20macOS%20%C2%B7%20Linux-100f0e?style=flat-square" alt="Windows, macOS, Linux">
  <img src="https://img.shields.io/badge/license-MIT-100f0e?style=flat-square" alt="MIT license">
</p>

<p align="center">
  <img src="assets/hero.png" width="880" alt="ungrep, beta 0.9. Grep reads text, ug reads the wiring. Against Unity's own MCP on the same editor tasks: 43% fewer calls, 45% fewer tokens, 67% less per session. And 80 tokens is all ug adds to a conversation; Unity's MCP adds 1,509.">
</p>

<p align="center">
  <strong>ungrep keeps a map of your Unity project outside Claude.</strong><br>
  Your code, scenes and prefabs, and how they connect. Claude asks the map instead of digging through files.
</p>

---

## Numbers

<p align="center">
  <img src="assets/editor-chores.png" width="880" alt="Benchmark, 10 tasks in a live Unity 6 editor. Tool calls: MCP for Unity 44, Unity's own MCP 44, ungrep 25, 43% fewer. Tokens: 5,430, 3,524 and 1,939, 45% fewer. Whole session, setup included: 11,576, 7,960 and 2,619, 67% fewer.">
</p>

On 19 everyday questions about a project, Claude needed 71 searches and file reads on its own, and 31 with ungrep. Every answer right both ways. [How we measured](https://github.com/UmarAbdullaev/ungrep/tree/main/bench).

## Before / after

<p align="center">
  <img src="assets/before-after.png" width="880" alt="What breaks if I rename this field? By hand, five steps: search the code, open the .meta file for the script's ID, search every scene and prefab for it, open the prefab for the saved value, search again for overrides, then piece the answers together. With ungrep, one step: ug impact Turntable._speed shows what reads it, what changes it, the prefab it's saved in, and the scene and prefab variant that override it, each with file and line.">
</p>

## How it works

<p align="center">
  <img src="assets/how-it-works.png" width="880" alt="How it works: it reads your scripts without building them, and your scenes and prefabs down to every value and override, then connects the two into one graph on your machine. Claude asks it one question and gets one answer. If you want, it also works the open editor: compile, test, play and edit, all undoable.">
</p>

It also looks out for you without being asked: it warns Claude before a rename breaks a prefab, and catches compile errors before Claude says it's done.

## Install

**1. The plugin.** In Claude Code, run these one at a time:

```
/plugin marketplace add UmarAbdullaev/ungrep
```
```
/plugin install ungrep@ungrep
```

You need the [.NET 8 runtime](https://dotnet.microsoft.com/download/dotnet/8.0) or newer and Unity 2022.3 or newer. Start Claude Code in your Unity project and ask away.

**2. The Unity package** (optional). It lets Claude compile, run tests, play and edit in the open editor.

In Unity, open **Window → Package Manager**, click **+**, choose **Install package from git URL** and paste:

```
https://github.com/UmarAbdullaev/ungrep.git?path=/unity/com.ungrep.bridge#v0.9.0
```

When ungrep updates, change the version at the end and add it again.

Or ask Claude to run `ug bridge install --copy`, which puts the package in your project and tells you when it needs an update.

`ug doctor` checks your setup. Only install ungrep from [`UmarAbdullaev/ungrep`](https://github.com/UmarAbdullaev/ungrep).

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
Grep finds words. Unity connects scripts, prefabs and scenes through hidden IDs, and grep can't follow them.

**Does Unity need to be open?**
Only to compile, test, play or edit.

**Will it touch my project?**
Questions only read. Edits can be undone, and nothing is saved until you say so.

## Status

Beta. Tested with Unity 2022.3 and Unity 6 on Windows. On macOS and Linux the automated tests pass, but the Unity side isn't tested there yet. Issues welcome.

## License

[MIT](LICENSE). Third-party notices in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

ungrep is not affiliated with or endorsed by Unity Technologies. Unity is a trademark or registered trademark of Unity Technologies.
