# Spec: unitygraph

A Claude Code plugin that makes work on Unity projects cheaper and faster.

Status: **capability map approved** (2026-09-28); **open questions decided** (2026-09-29, see
Decisions). Module specs: `graph-core` written; the others follow their build step.
This file holds the objective, the evidence, the shared rules and the capability map.
Each module gets its own `specs/SPEC-<module-id>.md`; the step plan lives in `tasks/plan.md`.

## Objective

Claude Code spends most of its tokens re-reading its own context: every tool call starts a
new turn, and every turn re-reads everything said so far. On Unity projects a large share of
those turns goes to work that is mechanical (compile, wait, read the console), noisy (logs,
test output, scene dumps, screenshots) or blind (finding code and inspector wiring by grepping
C# and GUID-linked YAML).

unitygraph cuts that cost without changing how the user works. It:

- answers code **and** asset-wiring questions in one call from a local graph;
- keeps multi-step Unity chores out of the main conversation (a worker subagent);
- makes the compile loop automatic, and silent when the build is clean;
- keeps Unity output compact;
- keeps the always-loaded project map small, generated and true;
- stops mistakes that cost a rework round (lost serialized data, broken `.meta` files, edits
  to vendored code).

**Users:** Unity developers who use Claude Code (CLI or desktop app), solo to small teams.

**Goal:** −30% main-thread context tokens (stretch −40%) and −15% turns on real Unity work,
with equal or better correctness. Measured by the plugin's own `stats` command and a benchmark.

**Place in the ecosystem:** replaces codegraph and Graphify in Unity projects. Works alongside
Unity's official Claude Code plugin and Unity MCP servers (scene editing), without needing them.

## Evidence

Measured on purge-squad: 34 Claude Code sessions, ~2,900 model turns, ~760M context tokens,
21 active hours. Method and scripts: `research/transcript-analysis/`.

- **Cost is turns × context.** Average context per turn: 264k tokens. Tool output itself is only
  ~16% of context. A session's first turn already carries ~71k (system prompt, tools, CLAUDE.md,
  project map).
- **Where the turns go** (context re-read by the turns that consumed each kind of result):

  | Kind | Share of all context tokens |
  | --- | --- |
  | Unity `eval` / `run_script` | 16.6% |
  | Shell search and reads | 15.9% |
  | Writing `.cs` | 10.2% |
  | git | 6.5% |
  | Scene/prefab YAML reads and greps | 5.4% |
  | Compile / refresh / status | 5.3% |
  | Play mode and screenshots | 4.0% |
  | Console | 3.7% |
  | Tests | 2.2% |
  | Reading `.cs` | 2.1% |

- **Compile loop:** 407 calls, about 4 per edit cycle; 1.5 h of the 21 h.
- **Simulated savings**, levers applied together to the real sessions, no double counting:

  | Levers | Context tokens | Turns |
  | --- | --- | --- |
  | Graph only (a navigation streak takes 2 calls / 1 call) | −14% / −21% | −10% / −16% |
  | Graph + compile gate + compact output + generated map | −21% / −33% | −12% / −25% |
  | + worker subagent for Unity/navigation streaks | −36% to −41% | n/a |

- **Ruled out by measurement:** a compact language for editor scripts (everything Claude writes
  is ~7% of cost, so ~1% saving) and a re-read guard (0.1%).

Simulation assumptions: the worker starts at ~15k context and returns a verdict of at most 800
tokens; compacted outputs are half their size; "realistic" means a navigation streak takes 2
calls. Module `bench` replaces these estimates with measurements.

## Design rules

1. **Every returned token is paid ~100 times.** Output stays in context for the rest of the
   session. Answers are capped (default ~1,200 tokens) and end with an explicit "more" handle.
2. **One call per question.** An answer carries the definition, callers, inspector wiring,
   serialized data and Unity entry points together. Several queries can share one call.
3. **Zero idle cost.** No MCP server: a CLI, one on-demand skill, one subagent, and hooks that
   say nothing unless they have something to say. The plugin adds at most 300 tokens to a
   session's baseline.
4. **Answer where Claude already goes.** When Claude greps a symbol or opens a big scene or
   prefab, a hook answers with the graph result in that same step (a PreToolUse hook denies the
   call and returns the graph answer as the reason; spike S3 confirms it, see Decisions).
5. **Never stale.** Edited files are reindexed on edit; assets Unity saved are picked up before
   the next query. A stale answer is worse than grep.
6. **Exact over guessed, and say which.** Every edge is `exact` (Roslyn, GUID) or `heuristic`
   (string dispatch, name match).
7. **Chores stay out of the main context.** Multi-step Unity work runs in the worker; the main
   thread gets a verdict.
8. **Cache-safe.** Never rewrite earlier context; only append.
9. **Fail open.** If Unity, the index or the plugin itself fails, the session carries on with a
   one-line notice.
10. **Measured, not claimed.** `stats` shows each user their own before and after.

## Overhead budget

The plugin costs tokens too. These limits keep its own cost far below what it saves. `stats`
measures each one, and `bench` fails a release that breaks one.

| Overhead | Budget | Why it stays small |
| --- | --- | --- |
| Skill and agent descriptions, loaded every session | ≤ 300 tokens | ~0.1% of context on purge-squad's sessions |
| `unity` skill body, loaded when first used | ≤ 1,500 tokens | once per session, not per turn |
| Generated per-folder map | ≤ 600 tokens per folder, loaded only when a file there is read | replaces an always-loaded monolithic map |
| Hook answers (interception) | only symbol-shaped greps and YAML reads over 32 KB (configurable); within the answer budget | replaces output Claude would have received anyway |
| Compile gate | 0 tokens when clean; ≤ 20 lines on errors | replaces 3–4 turns of recompile, wait and console |
| Worker delegation | only chores of 3 or more steps; verdict ≤ 800 tokens | a 1-step chore costs more delegated than done inline |
| Index builds and updates | 0 tokens: deterministic, no LLM | unlike Graphify's LLM extraction |

Ways the plugin could end up costing more, and the check for each:

- A wrong or stale answer makes Claude redo the work: reindex on every edit, confidence tags;
  `bench` measures the redo rate.
- A worker verdict missing a detail makes the main thread repeat the chore: verdicts carry
  evidence anchors (`file:line`, object paths); redo rate measured.
- One-step chores delegated anyway: the 3-step rule; `stats` counts short delegations.
- Hooks firing on the wrong calls: narrow triggers, replay tests on recorded transcripts,
  false-trigger count in `stats`.

Every module has its own on/off switch, and the step-3 checkpoint stops the build if savings are
under 10%.

## Decisions (2026-09-29)

1. **Unity 2022.3 LTS or newer** (tested on 2022.3 and Unity 6). Asset Serialization is Force
   Text (Unity's default); binary assets are reported, not indexed.
2. **CLI and indexers in C# on .NET 8 with Roslyn.** They run on an installed .NET runtime or on
   the one bundled with the Unity Editor (spike S1), and fall back to syntax-only C# (every code
   edge `heuristic`) when neither is usable.

   **Spike S1 result (2026-09-29): the bundled Unity runtime is not usable.** Every editor
   checked ships .NET **6.0.21** in `<Editor>/Data/NetCoreRuntime` (runtime and host only, no
   SDK). A `net8.0` app started through Unity's `dotnet.exe` fails unless a global .NET 8 install
   exists, in which case the host silently uses that one (confirmed with `COREHOST_TRACE`).

   | Unity | OS | Bundled runtime | `net8.0` app on it |
   | --- | --- | --- | --- |
   | 2022.3.62f1, 2022.3.62f3 | Windows 11 | 6.0.21 | no (falls through to a global 8.x if installed) |
   | 6000.3.9f1, 6000.3.11f1, 6000.3.14f1 | Windows 11 | 6.0.21 | no (same) |
   | 2022.3 / Unity 6 | macOS | not tested (no Mac available) | expected the same |

   Roslyn on Unity's generated `.csproj` works **without MSBuild**: they are legacy (non-SDK)
   projects targeting `v4.7.1` with every `Compile` item, `HintPath` (relative ones resolve
   against the project root), `ProjectReference`, `DefineConstants` and `LangVersion` spelled
   out, so reading the XML and building a `CSharpCompilation` directly gives a full semantic
   model. On purge-squad: `Assembly-CSharp` 864 files, 344 references, 0 errors, 1.6 s;
   `GunLab.Runtime` 105 files, 0 errors; `Assembly-CSharp-Editor` 92 files, 0 errors (project
   references taken from `Library/ScriptAssemblies`; `code-index` should compile them from
   source instead). `MSBuildWorkspace` is not used: it would need the .NET SDK or Visual
   Studio's MSBuild for these `net471` projects.

   Launcher strategy: **pending decision** (the S1 result reopens this decision; see
   `tasks/todo.md` checkpoint).
3. **Own editor bridge.** Live Unity access goes through a small editor-only package the plugin
   ships (`com.unitygraph.bridge`), independent of which Unity MCP server the user runs.
   `Editor.log` is the fallback when Unity is unreachable. An adapter for Unity's official CLI may
   follow after v1.
4. **Hook interception by PreToolUse deny.** A symbol-shaped grep or a big-YAML read is denied
   and the graph answer is returned as the reason. Spike S3 measures, on replayed purge-squad
   calls, how often Claude retries the original call anyway; above ~10%, interception is off by
   default and the skill alone points Claude at `ug`.
5. **Graph viewer for people.** `ug view <symbol|asset> [--depth N]` writes a self-contained,
   offline HTML page (Cytoscape.js bundled) to `Library/unitygraph/view.html` and opens it. Claude
   sees one line (the path): 0 tokens of graph. Built after the step-3 checkpoint.
6. **Worker subagent defaults to Sonnet** (configurable). `bench` measures the redo rate with
   Haiku; if it stays under ~5%, the default moves to Haiku.
7. **Generated maps go in per-folder `CLAUDE.md` files**, only between `<!-- ug:begin -->` and
   `<!-- ug:end -->` markers, so hand-written text survives. Written only after the user agrees
   once per project.
8. Name `unitygraph`, CLI `ug`. Name availability (GitHub, plugin directory) is checked before
   release.
9. **MIT licence.** Distributed from its own GitHub marketplace first, the official directory
   later.
10. **Benchmark projects:** purge-squad, Boss Room, the URP 3D Sample and Chop Chop (Unity Open
    Project). Each licence is checked before any file is copied into `bench/`.

Standing assumptions:

- Local only: no telemetry, no network calls. Transcripts are read on the user's machine.
- Windows and macOS first; Linux after v1.
- Development dogfoods on purge-squad; each module is measured there before it's called done.

## Capability map

| Module id | Responsibility | Depends on |
| --- | --- | --- |
| `stats` | Reads local Claude Code transcripts; attributes context tokens by category; before/after report. Also the measuring tool for `bench`. | — |
| `plugin-shell` | Plugin manifest, `ug` launcher and .NET runtime resolution, config file, hook dispatcher (`ug hook <event>`), the on-demand `unity` skill. | — |
| `graph-core` | SQLite store: node and edge schema with confidence, file hashes, incremental updates, freshness checks. | — |
| `code-index` | Roslyn over Unity's generated `.csproj` files: types, members, serialized fields (by Unity's rules), calls, overrides, implementations, attributes, engine entry points, string dispatch (heuristic). | `graph-core` |
| `asset-index` | `.meta` GUID table and Unity YAML (scenes, prefabs with nesting, variants and overrides, ScriptableObjects, materials, animators, animations, input actions, asmdefs, Addressables): objects, components, references, UnityEvent calls. | `graph-core` |
| `query` | `ug find / explain / refs / impact / outline / batch`; answer budgets, `file:line` anchors, confidence tags; reindex on edit; hooks that answer symbol greps and big-YAML reads. | `code-index`, `asset-index`, `plugin-shell` |
| `editor-link` | Bridge package and client: status, refresh and compile, compile errors, console, tests, run a static method; `Editor.log` fallback when Unity is unreachable. | `plugin-shell` |
| `unity-loop` | Compile gate (edit tracking + Stop hook, silent when clean); compact `ug console`, `ug test`, `ug log` (digest of the last play session). | `editor-link` |
| `guards` | Hooks: serialized field renamed or removed (with the number of assets holding data), `[SerializeReference]` types moved, `.meta`-safe `mv`/`cp`/`rm`, edits to vendored folders, configurable runtime-lookup lint. | `code-index`, `asset-index`, `plugin-shell` |
| `worker-agent` | Unity worker subagent, delegation rules in the skill, verdict contract. | `query`, `unity-loop`, `editor-link` |
| `project-map` | Generated sections of per-folder `CLAUDE.md` files and a small root index; staleness check. | `query` |
| `viewer` | `ug view`: self-contained HTML graph for people; nodes coloured by kind, `exact` edges solid and `heuristic` dashed, filter and search. 0 tokens to Claude. | `query` |
| `bench` | Question suites on purge-squad and 2–3 open Unity projects; arms: grep baseline, codegraph, Graphify, unitygraph; scores calls, tokens and correctness. | `stats`, all feature modules |

**Build order** (measure first, biggest lever first, each step dogfooded on purge-squad):

1. Spikes S1–S3, then `stats`, `plugin-shell`, `graph-core`
2. `code-index`, `asset-index`, `editor-link`
3. `query`, `unity-loop`
4. `worker-agent`, `guards`
5. `project-map`, `viewer`
6. `bench`, then release

**Checkpoint after step 3:** if real-session savings are under 10%, stop and re-plan before
building further.

## Tech stack

- C# 12 on .NET 8: Roslyn (`Microsoft.CodeAnalysis.CSharp.Workspaces`), `Microsoft.Data.Sqlite`,
  `System.CommandLine`.
- Unity YAML: a hand-written streaming reader for Unity's subset (`--- !u!1 &123` document
  headers, stripped objects, multi-document files). General YAML libraries are slow on it or
  reject it.
- Unity bridge: UPM package, editor-only asmdef, localhost HTTP on a random port recorded in
  `Library/unitygraph/bridge.json`.
- Viewer: Cytoscape.js, vendored into the published CLI and inlined into the generated page (no
  network at view time).
- Tests: xUnit; Verify for golden CLI output; Unity Test Framework for the bridge.
- Claude Code plugin format: `.claude-plugin/plugin.json`, `skills/`, `agents/`, `hooks/hooks.json`.

## Commands

```
Build         dotnet build unitygraph.sln -c Release
Test          dotnet test unitygraph.sln
Format        dotnet format unitygraph.sln --verify-no-changes
Publish CLI   dotnet publish src/UnityGraph.Cli -c Release -r win-x64 -o dist/win-x64
Bridge tests  <Unity> -batchmode -projectPath fixtures/BridgeHost -runTests -testPlatform EditMode -testResults artifacts/bridge.xml
Dogfood       claude plugin marketplace add D:\Projects\unitygraph
              claude plugin install unitygraph@unitygraph-dev --scope local
One session   claude --plugin-dir D:\Projects\unitygraph
Validate      claude plugin validate .claude-plugin/marketplace.json
Stats         ug stats --project D:\Projects\Unity\purge-squad --since 2026-09-01
Bench         ug bench run bench/suites/purge-squad.yaml --arm unitygraph
```

`<Unity>` is the installed editor's executable.

Plugin install, confirmed by spike S2 (2026-09-29, Claude Code 2.1.258, Windows 11):

- The in-session forms are the same: `/plugin marketplace add <path>`, `/plugin install
  unitygraph@unitygraph-dev`. `--scope` is `user` (default), `project` or `local` (written to
  `<cwd>/.claude/settings.local.json`); dogfood uses `local` so other projects are untouched.
- Layout: `.claude-plugin/plugin.json` (`name`, `version`, `description`, `author`) and
  `.claude-plugin/marketplace.json` (`name: unitygraph-dev`, `owner`, `plugins: [{ name, source:
  "./" }]`); `skills/<name>/SKILL.md`, `hooks/hooks.json` are picked up by convention.
  `claude plugin details unitygraph@unitygraph-dev` prints the components and the always-on
  token cost (the hello skill: ~20 tokens; hooks: 0).
- A directory marketplace is registered as `"source": "directory"` in `~/.claude/settings.json`.
  `CLAUDE_PLUGIN_ROOT` points at the **source folder** (`D:/Projects/unitygraph`, forward
  slashes), not the install cache, so edits to hooks and `bin/` apply to the next session
  without reinstalling. `--plugin-dir` behaves the same.
- Install still copies the whole marketplace folder into `~/.claude/plugins/cache/`. Before
  release the plugin moves to a subfolder (or the marketplace `source` narrows) so `src/`,
  `research/` and `bench/` aren't shipped.
- Hooks: `"command": "\"${CLAUDE_PLUGIN_ROOT}/bin/ug\" hook <Event>"`. On Windows the command
  runs in Git Bash (`/bin/bash.exe`, MINGW64), so one POSIX `bin/ug` script serves Windows and
  macOS; `bin/ug.cmd` is only needed for users calling `ug` from cmd/PowerShell. The payload
  arrives as JSON on stdin (`session_id`, `transcript_path`, `cwd`, `hook_event_name`, ...);
  `CLAUDE_PROJECT_DIR` is set. `bin/` is not on `PATH` inside hooks.
- Desktop app: uses the same `~/.claude` settings, so a marketplace added from the CLI is
  visible there; the `/plugin` UI flow itself was not exercised.

## Project structure

```
SPEC.md                         objective, evidence, shared rules, capability map (this file)
specs/SPEC-<module-id>.md       one spec per module, written after the map is approved
tasks/plan.md, tasks/todo.md    plan and task list per phase
research/transcript-analysis/   the scripts behind the Evidence section (prototype of stats)
.claude-plugin/                 plugin.json; marketplace.json for local dev installs
skills/unity/                   on-demand skill: CLI usage, delegation rules, Unity traps
agents/unity-worker.md          the worker subagent
hooks/hooks.json                hook wiring, every hook calls `ug hook <event>`
bin/                            launchers that find a .NET runtime and start ug
src/UnityGraph.Cli              commands and output rendering
src/UnityGraph.Core             graph-core
src/UnityGraph.Code             code-index
src/UnityGraph.Assets           asset-index
src/UnityGraph.Query            query
src/UnityGraph.EditorLink       editor-link client
src/UnityGraph.Hooks            hook handlers (unity-loop, guards, query interception)
src/UnityGraph.Stats            stats
src/UnityGraph.Viewer           viewer (HTML page generation)
unity/com.unitygraph.bridge     the Unity editor package
tests/<Project>.Tests           unit and golden tests, one project per source project
fixtures/                       small text-serialized Unity projects used by tests
bench/                          suites, expected answers, scoring
```

## Code style

```csharp
namespace UnityGraph.Assets;

/// <summary>
/// Maps a .meta GUID to its asset path. Scenes and prefabs point at scripts and assets by
/// GUID only, so every asset edge resolves through this table.
/// </summary>
public sealed class GuidTable
{
    private readonly Dictionary<string, string> _pathByGuid = new(StringComparer.Ordinal);

    public int Count => _pathByGuid.Count;

    public void Add(string guid, string assetPath) => _pathByGuid[guid] = assetPath;

    public bool TryGetPath(string guid, [NotNullWhen(true)] out string? assetPath) =>
        _pathByGuid.TryGetValue(guid, out assetPath);
}
```

- .NET 8, C# 12, nullable enabled, warnings as errors, file-scoped namespaces.
- Private fields `_camelCase`; the public surface is properties and methods; classes `sealed`
  unless designed for inheritance.
- Every type gets an XML `<summary>`. Comments say why, not what.
- Deterministic everywhere: stable ordering, invariant culture, no timestamps in output. Golden
  tests and prompt caching both depend on it.
- No static mutable state. I/O at the edges; parsers are pure functions over text.
- CLI output is plain text written for Claude. JSON only with `--json`. Every answer stays
  within its budget.

CLI output (illustrative):

```
$ ug explain LabStage
LabStage  class  Assets/Scripts/Lab/LabStage.cs:14-152  [exact]
  called from   LabInteraction.OnOpened  Assets/Scripts/Lab/LabInteraction.cs:41
  lives on      Hub.unity > HubStation/LabStage  (prefab HubStation)
  serialized    _stageGun -> Prefabs/Weapons/Gun.prefab | _turntable -> LabStage/Turntable
  engine calls  Awake, OnEnable, OnDisable
  more          ug explain LabStage --members
```

## Testing strategy

- **Unit:** parsers and extractors on fixture files. Every supported asset type has at least one
  fixture, including nested prefabs, variants, stripped objects and multi-document files.
  Unity's serialization rules are table-tested: public fields, `[SerializeField]`,
  `[NonSerialized]`, `[SerializeReference]`, `[field: SerializeField]` properties, and the
  static/readonly/const exclusions.
- **Golden:** CLI output compared with Verify snapshots. Hook handlers are fed recorded hook
  JSON payloads.
- **Integration:** index the fixture projects end to end; freshness (an edit, then a query that
  sees it).
- **Bridge:** EditMode tests in `fixtures/BridgeHost`, run in batch mode.
- **Replay:** guards and interception hooks are replayed against the tool calls recorded in
  purge-squad's transcripts. They must not block anything that was legitimate.
- **Performance**, on a purge-squad-sized project (~530 KB of own C#, 185 YAML assets, the
  largest 740 KB): cold index ≤ 60 s; reindex of one edited file ≤ 1 s; query p95 ≤ 500 ms;
  compile gate ≤ 30 s including Unity's compile, with a timeout that fails open.
- **Coverage:** at least 80% of lines in Core, Code, Assets and Query.
- **Real use:** every module ships behind its own config switch and is measured with `stats` on
  purge-squad before it is marked done.

## Boundaries

- **Always:** tests green before a commit; deterministic output; answers within budget and
  tagged with confidence; fail open; update the relevant spec when a decision changes; measure
  before claiming a saving.
- **Ask first:** new dependencies; graph schema changes after v1; new hook events; anything that
  writes into a user's Unity project (installing the bridge package, generating `CLAUDE.md`
  files); any network access; widening the supported Unity versions.
- **Never:** send code, transcripts or usage data off the machine; edit vendored folders or
  `Packages/` in a user's project; change scenes or prefabs from a hook; add an MCP server
  without a measured reason; block the user's session because the plugin failed.

## Success criteria (v1)

1. On purge-squad's real sessions over at least 2 weeks, compared with the 34-session baseline
   (`ug stats`): main-thread context tokens −30% or more (stretch −40%), turns −15% or more, and
   no increase in compile errors or regressions reaching the user.
2. Benchmark against the grep/Read baseline: at least 2× fewer tool calls on navigation and
   wiring questions with correctness at least equal; asset-wiring questions answered in one call
   at least 90% of the time. Against codegraph on C# questions: no more calls, equal or better
   correctness. Against Graphify: fewer calls per question.
3. Idle cost at most 300 tokens per session.
4. Compile gate: a clean compile adds zero turns; errors arrive in at most 20 lines.
5. Guards: every fixture case caught; no block on the replayed purge-squad history that wasn't a
   real mistake.
6. From install to the first answer in at most 5 minutes on a fresh Unity project.

## Open questions

None blocking. Decided 2026-09-29 (see Decisions). Left for the spikes to settle:

- ~~S1: can `ug` run on the .NET runtime bundled with Unity 2022.3 and Unity 6?~~ No (bundled
  runtime is 6.0.21), see Decisions #2. Launcher strategy to decide.
- ~~S2: the exact plugin marketplace and install commands for a local dev install.~~ Answered
  2026-09-29, see Commands.
- S3: does PreToolUse deny + graph answer stop Claude from re-running the grep?
