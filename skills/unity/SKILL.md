---
name: unity
description: Unity project questions (scripts, scenes, prefabs, assets, serialized fields, compile and console). Use the ug CLI before grep or reading YAML.
---

# unitygraph

Run `ug` through the plugin launcher: `"${CLAUDE_PLUGIN_ROOT}/bin/ug" <command>`.

| Command | Answers |
| --- | --- |
| `ug doctor` | Is the plugin working here: runtime, Unity project, config, graph. |
| `ug stats --project <path>` | Where this project's context tokens went, by tool category. |

Graph queries (`ug find`, `ug explain`, `ug refs`) are not built yet; search as usual until
`ug doctor` reports a graph.
