---
name: unity
description: Unity project questions (scripts, scenes, prefabs, assets, serialized fields, compile and console). Use the ug CLI before grep or reading YAML.
---

# unitygraph

Run `ug` through the plugin launcher: `"${CLAUDE_PLUGIN_ROOT}/bin/ug" <command>`. Every command
takes `--project <dir>` when the shell isn't inside the Unity project.

Ask the graph first: one `ug` call replaces a grep-and-read streak. Answers carry `file:line`
anchors; read only the lines you need after that.

| Command | Answers |
| --- | --- |
| `ug explain <symbol>` | What it is: location, base types, Unity kind and script GUID, engine messages, entry points, serialized fields, who uses or calls it, which prefabs and scenes it sits on. For an asset: its objects, scripts, prefabs, what it uses, who uses it, UnityEvents, Addressables. `--members` lists members. |
| `ug refs <symbol>` | Everything pointing at it, with `file:line`: callers, string dispatch (`Invoke`, `SendMessage`), overrides, subclasses, implementations, type uses, components using a script, UnityEvent wiring, prefab instances, variants, serialized references and prefab overrides. |
| `ug find <text>` | Types, members, assets, game objects and prefab instances whose name starts with the words; `--kind type\|method\|field\|property\|event\|asset\|game_object\|prefab_instance`. |
| `ug index` | Build or update the code and asset graph (queries do this themselves). |
| `ug doctor` | Is the plugin working here: runtime, Unity project, config, graph. |
| `ug stats` | Where this project's context tokens went, by tool category. |

`<symbol>`: `LabStage`, `LabStage.Open`, `Game.Lab.LabStage.Open(int)`, an asset path
(`Assets/Prefabs/Turntable.prefab`), file name (`Turntable.prefab`) or GUID, or a `cs:`, `a:` or
`o:` key from an answer. Overloads without parameters are answered together.

Reading answers:
- `[exact]` came from the compiler; `[heuristic]` is a string match (`Invoke("Name")`); check it
  before relying on it.
- `ambiguous:` lists candidates and the command for the first; `not found:` lists near names.
- A `more` line means the answer was cut: run it only if you need the rest.
- Scenes, prefabs, ScriptableObjects and materials are in the graph: ask it before reading YAML.
- A `unity event` matched only by method name (no parameter types in the YAML) is `[heuristic]`.
- Calls into UnityEngine or packages are not in the graph; `refs MonoBehaviour` finds nothing.
