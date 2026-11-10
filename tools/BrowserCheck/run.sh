#!/usr/bin/env bash
# Opens the web version in a real (headless) Chrome, writes on it, waits for a reply from a fake Anthropic API, and checks what
# the page does. Needs dotnet 10, node 22 and Chrome or Chromium.
#   tools/BrowserCheck/run.sh [chrome command] [output folder for screenshots]
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
chrome=${1:-google-chrome}
out=${2:-$here/out}
mkdir -p "$out"
work=$(mktemp -d)
cleanup() {
  kill $(jobs -p) 2>/dev/null || true
  wait 2>/dev/null || true # Chrome keeps writing to its profile until it has exited
  rm -rf "$work" 2>/dev/null || true # a leftover temp folder is not a failure
}
trap cleanup EXIT

dotnet build "$root/Longhand.Web/Longhand.Web.csproj" -o "$work/app" -v q -nologo
node "$here/fake-anthropic.mjs" "$out" >"$work/fake.log" 2>&1 &
ANTHROPIC_BASE_URL=http://127.0.0.1:18080 ANTHROPIC_API_KEY=sk-test LONGHAND_DATA="$work/data" \
  LONGHAND_REGISTRY_URL=http://127.0.0.1:18081 LONGHAND_ALLOW_PRIVATE_APPS=1 \
  dotnet "$work/app/Longhand.Web.dll" --urls http://127.0.0.1:5099 >"$work/server.log" 2>&1 &
"$chrome" --headless=new --no-sandbox --disable-gpu --remote-debugging-port=9333 --user-data-dir="$work/profile" \
  --window-size=1000,700 about:blank >"$work/chrome.log" 2>&1 &

for _ in $(seq 1 60); do
  curl -sf http://127.0.0.1:5099/ >/dev/null && curl -sf http://127.0.0.1:9333/json/version >/dev/null && break
  sleep 1
done
status=0
node "$here/drive.mjs" "$out" http://127.0.0.1:5099/ 9333 || status=$?
if [ "$status" != 0 ]; then
  echo "--- server"; cat "$work/server.log"; echo "--- fake API"; cat "$work/fake.log"
fi
exit "$status"
