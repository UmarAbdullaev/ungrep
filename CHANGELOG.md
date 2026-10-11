# Changelog

## 0.9.0 (beta), 2026-10-11

The first public build.

- **Graph:** `ug index`, `find`, `refs`, `explain`, `outline`, `impact` over C# (Roslyn) and Unity
  assets (scenes, prefabs and variants, nested instances and overrides, ScriptableObjects,
  materials, Addressables) joined through `.meta` GUIDs. References say how a member is used:
  called, read, written, subscribed to with its handler, unsubscribed, raised.
- **Editor bridge** (UPM package, editor-only, `127.0.0.1`): status, compile, console, tests,
  `run` and `eval`, scenes, play mode, live hierarchy and component values, object, component,
  field, UnityEvent and prefab edits through Undo, assets, packages, project settings, cameras
  and Cinemachine, screenshots, profiler, API and docs lookup.
- **Play-testing:** `ug ui`, `click`, `nav`, `press`, `stick`, `tap`, `swipe`, `wait`, with a
  digest of what changed after each.
- **Hooks:** guards for serialized-field renames and removals, `[SerializeReference]` moves,
  `.meta`-safe shell moves and deletes, vendored folders; a compile gate when Claude stops;
  reindex on edit.
- **`ug stats`:** where a project's Claude Code context tokens went, by tool-call category.
- **`ug view`:** an interactive HTML graph of a symbol's neighbourhood.
- **`ug bench`:** the benchmark suites and the overhead budgets.
- **`ug doctor`:** checks the runtime, the project, the config, the graph and where the bridge
  comes from (a Package Manager link into an old plugin folder, or a copy behind the plugin).
- Runs on Windows, macOS and Linux on an installed .NET 8 or newer runtime.
