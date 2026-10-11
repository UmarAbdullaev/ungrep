# Spec: release

Part of unitygraph (see `../SPEC.md`). Status: **draft** (2026-10-11). Requested by the user on
2026-10-11: "finish this plugin and package it first", version 0.9.0 (beta), Windows, macOS and
Linux, the B1f agent check before the release, a README in the style of popular plugins.

## Objective

A Claude Code user installs unitygraph from GitHub in two commands and gets a working `ug` on
Windows, macOS or Linux, with nothing to build:

```
/plugin marketplace add UmarAbdullaev/unitygraph
/plugin install unitygraph@unitygraph
```

## Branches

- `main`: the source, specs, tests, bench and the dev marketplace (`.claude-plugin/marketplace.json`,
  name `unitygraph-dev`, plugin source `./`). `dist/` is not committed. Day-to-day work and the
  user's own dogfood (a directory marketplace on the checkout) stay here.
- `release`: an orphan branch holding only the payload, one commit per version
  ("unitygraph 0.9.0" + "Built from main <sha>."). It is the repository's default branch, so
  `marketplace add owner/repo` reads its marketplace file. Never merged into `main` or back.

## Payload (`build/package.sh`)

From `HEAD` by `git archive`, so an uncommitted edit can't slip in:
`.claude-plugin/plugin.json`, `bin/`, `hooks/`, `skills/`, `unity/` (the bridge package),
`README.md`, `CHANGELOG.md`, `LICENSE`, `THIRD_PARTY_NOTICES.md`. Added by the script:

- `dist/ug/`: `dotnet publish src/UnityGraph.Cli -c Release`, framework-dependent (.NET 8 runtime
  or newer, any OS), no apphost, no API docs, English resources only, SQLite natives for win, osx
  and linux on x64 and arm64 (`KeepDesktopSqliteNatives`). About 23 MB. `bin/ug` (POSIX sh) and
  `bin/ug.cmd` start `ug.dll` on `dotnet`.
- `licenses/`: the viewer scripts' licence files (`src/UnityGraph.Viewer/vendor/*.LICENSE`).
- `.claude-plugin/marketplace.json`: name `unitygraph`, plugin `unitygraph`, source `./`, the
  version.
- `.gitattributes` `* -text`: the release branch stores bytes as built.

The script refuses when `src/` or the build files have uncommitted changes
(`UG_PACKAGE_DIRTY=1` for a local try only) or when `plugin.json`'s version differs from
`Directory.Build.props`. `--commit` commits the payload to `release` in a worktree at
`artifacts/release` and marks `bin/ug` executable; it never pushes.

## Versions

- One version for the plugin and `ug`: `.claude-plugin/plugin.json` and `Directory.Build.props`
  (`ug --version`, `VersionTests`). Claude Code keys its cache on `plugin.json`'s version, so every
  release bumps it, or users keep the old build.
- The bridge package (`unity/com.unitygraph.bridge/package.json`) keeps its own version, bumped
  when the bridge changes (SPEC-editor-link).
- 0.x while beta. A breaking change to a command's output or the config is called out in
  `CHANGELOG.md`.

## Updates and the bridge

An installed plugin lives in `~/.claude/plugins/cache/<marketplace>/<plugin>/<version>/`; an
update moves it to a new folder and deletes the old one later. So:

- Nothing project-side may point into the plugin folder. `ug bridge install --copy` (vendored
  copy in `Assets/Plugins/UnityGraphBridge/`) is the recommended way to add the bridge; Package
  Manager's "Add package from disk" is listed second with the warning (SPEC-editor-link).
- `ug doctor`'s `bridge` line fails on a `file:` link to a missing folder, warns on a link into
  `/plugins/cache/` and on a copy whose version differs from the plugin's bundled bridge.
- The graph and logs live in the project's `Library/unitygraph/`, so an update keeps them; a
  changed index rule rebuilds the graph on first use (SPEC-graph-core).

## Platforms

Windows, macOS and Linux (SPEC.md, 2026-10-11). Development and the Unity tests run on Windows.
CI (`.github/workflows/ci.yml`) runs on `windows-latest`, `macos-latest` and `ubuntu-latest`:
build, format, the .NET tests, then the launcher on the published build (`bin/ug --version`, the
SessionStart hint, `ug refs Turntable.Rotated` on a copy of `fixtures/CodeProject`). The bridge
and the Unity tests are not run on macOS or Linux; the README says so.

## Release checklist

1. `CHANGELOG.md` has the version; `plugin.json` and `Directory.Build.props` match.
2. `dotnet test --solution unitygraph.sln` and `dotnet format --verify-no-changes` pass; CI is
   green on all three systems.
3. `ug bench budgets` within the overhead budgets (SPEC-bench).
4. Commit on `main`, then `build/package.sh --commit`.
5. `claude plugin validate artifacts/package/unitygraph --strict` passes.
6. Clean install: a fresh Claude Code config dir, the marketplace added from the release branch,
   `/plugin install`, `ug doctor` in a Unity project, one question answered with `ug`.
7. On the user's yes: `git push origin main release`, and a GitHub release tagged `v<version>` on
   the `release` commit.

## Boundaries

- Ask first: creating the GitHub repository, pushing, tagging, changing the default branch, and
  any change to the user's own Claude Code settings or marketplaces.
- Never: a release built from uncommitted sources; a payload with tests, specs, bench data or
  fixtures; a `Co-Authored-By` trailer in the release commits.

## Open points

1. A self-contained build per OS (no .NET runtime needed) instead of one framework-dependent build:
   each carries its own runtime, so the payload grows several times, and the launcher must pick
   the right binary. Not in
   0.9.0; reconsider if the runtime requirement turns out to stop people.
2. The name: Unity's trademark guidelines restrict "Unity" in product names; the user decides.
