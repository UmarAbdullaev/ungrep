#!/usr/bin/env bash
# Renders the README cards to assets/*.png at 2x with headless Chrome or Edge.
# Each page names its size in <meta name="card-size" content="WxH">.
# Usage: build/readme-art/render.sh [page.html ...]   (default: every page here)
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
out="$(cd "$here/../../assets" && pwd)"

browser="${CHROME:-}"
if [ -z "$browser" ]; then
  for candidate in \
    "/c/Program Files/Google/Chrome/Application/chrome.exe" \
    "/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe" \
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" \
    "$(command -v google-chrome || true)" "$(command -v chromium || true)"; do
    if [ -n "$candidate" ] && [ -x "$candidate" ]; then browser="$candidate"; break; fi
  done
fi
[ -n "$browser" ] || { echo "render.sh: no Chrome or Edge found; set CHROME" >&2; exit 1; }

url() { if command -v cygpath >/dev/null; then echo "file:///$(cygpath -m "$1")"; else echo "file://$1"; fi; }
native() { if command -v cygpath >/dev/null; then cygpath -w "$1"; else echo "$1"; fi; }

pages=("$@")
[ ${#pages[@]} -gt 0 ] || pages=("$here"/*.html)
for page in "${pages[@]}"; do
  page="$(cd "$(dirname "$page")" && pwd)/$(basename "$page")"
  size="$(sed -n 's/.*name="card-size" content="\([0-9]*\)x\([0-9]*\)".*/\1,\2/p' "$page")"
  [ -n "$size" ] || { echo "render.sh: $page has no card-size" >&2; exit 1; }
  png="$out/$(basename "$page" .html).png"
  "$browser" --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=2 \
    --default-background-color=00000000 --virtual-time-budget=3000 --run-all-compositor-stages-before-draw \
    --window-size="$size" --screenshot="$(native "$png")" "$(url "$page")" >/dev/null 2>&1
  echo "$png"
done
