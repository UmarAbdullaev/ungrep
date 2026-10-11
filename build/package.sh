#!/bin/sh
# Builds the plugin payload users install (specs/SPEC-release.md): the committed plugin files from
# HEAD, a fresh framework-dependent ug build, licences, and the public marketplace file.
#
#   build/package.sh             payload in artifacts/package/unitygraph
#   build/package.sh --commit    also commits it to the `release` branch (worktree in artifacts/release)
#
# It never pushes. The build must come from committed sources: src/ and the build files may not
# have uncommitted changes (UG_PACKAGE_DIRTY=1 allows it for a local try, never for a release).

set -eu

repo=$(cd "$(dirname "$0")/.." && pwd)
out="$repo/artifacts/package/unitygraph"
commit=false
for arg in "$@"; do
    case $arg in
        --commit) commit=true ;;
        *) echo "usage: build/package.sh [--commit]" >&2; exit 2 ;;
    esac
done

cd "$repo"
if [ "${UG_PACKAGE_DIRTY:-}" != 1 ] && [ -n "$(git status --porcelain -- src Directory.Build.props Directory.Packages.props global.json)" ]; then
    echo "package: src/ or the build files have uncommitted changes; commit them first" >&2
    exit 1
fi

version=$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' .claude-plugin/plugin.json)
props=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' Directory.Build.props)
if [ "$version" != "$props" ]; then
    echo "package: plugin.json version $version differs from Directory.Build.props $props" >&2
    exit 1
fi

rm -rf "$out"
mkdir -p "$out"

# The plugin files as committed, so a local edit can't slip into a release.
git archive HEAD .claude-plugin/plugin.json assets bin hooks skills unity README.md CHANGELOG.md LICENSE THIRD_PARTY_NOTICES.md \
    | tar -x -C "$out"

mkdir -p "$out/licenses"
cp src/UnityGraph.Viewer/vendor/*.LICENSE "$out/licenses/"

cat > "$out/.claude-plugin/marketplace.json" <<EOF
{
  "name": "unitygraph",
  "description": "unitygraph: Unity project graph and editor bridge for Claude Code.",
  "owner": { "name": "Umar" },
  "plugins": [
    {
      "name": "unitygraph",
      "source": "./",
      "description": "Who calls, uses or breaks what in a Unity project, in one call with file:line; plus an editor bridge, guard hooks and token stats.",
      "version": "$version"
    }
  ]
}
EOF

# Stored byte for byte on the release branch: no line-ending conversion of the build or launchers.
printf '%s\n' '* -text' > "$out/.gitattributes"

dotnet publish src/UnityGraph.Cli -c Release -o "$out/dist/ug" -nologo -v quiet

"$out/bin/ug" --version >/dev/null
echo "package: unitygraph $version in ${out#"$repo"/} ($(du -sh "$out" | cut -f1))"

if ! $commit; then
    exit 0
fi

worktree="$repo/artifacts/release"
if [ ! -d "$worktree" ]; then
    if git show-ref --verify --quiet refs/heads/release; then
        git worktree add "$worktree" release
    else
        git worktree add --detach "$worktree"
        git -C "$worktree" checkout --orphan release
    fi
fi

# Replace everything but .git with the payload.
find "$worktree" -mindepth 1 -maxdepth 1 ! -name .git -exec rm -rf {} +
cp -R "$out"/. "$worktree"/
git -C "$worktree" add -A
git -C "$worktree" update-index --chmod=+x bin/ug
if git -C "$worktree" diff --cached --quiet; then
    echo "package: release branch already holds this payload"
    exit 0
fi

git -C "$worktree" commit -q -m "unitygraph $version" -m "Built from main $(git rev-parse --short HEAD)."
echo "package: committed unitygraph $version to the release branch ($(git -C "$worktree" rev-parse --short HEAD)); push it with: git push origin release"
