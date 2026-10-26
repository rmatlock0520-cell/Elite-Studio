#!/usr/bin/env bash
# Pictures of Elite Studio's window, for the GitHub check machine (.github/workflows/linux.yml runs this inside
# xvfb-run, on a 1600x1000 screen with no monitor). Studio starts on practice folders made from tests/Fixtures,
# each studio's tab is clicked in turn and the whole screen is pictured after each, then Studio is asked to quit.
# The shell page (src/web/index.html) has no address or key that picks a tab, so the tabs are clicked where the
# page draws them.
# Usage: screenshots.sh <unpacked elite-studio folder> <repository folder> <folder for the pictures>
set -euo pipefail
app=$1 repo=$2 out=$3
port=47830
work=$(mktemp -d)
mkdir -p "$out"
# Studio's log goes with the pictures; the practice folders go when the run ends, pass or fail.
trap 'cp "$work/data/logs/studio.log" "$out/" 2>/dev/null || true; rm -rf "$work"' EXIT
fail() { echo "::error::$1"; import -window root "$out/at-failure.png" || true; exit 1; }

# The practice folders CLAUDE.md describes: the recorded journals with the game's status files, the key
# bindings, and a game folder with the HUD mod's colour files and an empty EliteDangerous64.exe.
fx=$repo/tests/Fixtures
mkdir -p "$work/journal" "$work/bindings" "$work/game"
cp "$fx"/journals/* "$fx"/gamefiles/{Status,Cargo,Market,NavRoute}.json "$work/journal/"
cp "$fx"/gamefiles/{Custom.4.2.binds,StartPreset.4.start} "$work/bindings/"
cp -r "$fx"/ui/game/. "$work/game/"
: > "$work/game/EliteDangerous64.exe"

"$app/elite-studio" --offline --port $port --data-dir "$work/data" --journal-dir "$work/journal" \
  --bindings-dir "$work/bindings" --game-dir "$work/game" &
studio=$!

# Tries once a second for a minute; gives up at once when Studio has stopped.
until_ready() {
  for _ in $(seq 60); do
    "$@" && return 0
    kill -0 "$studio" 2>/dev/null || return 1
    sleep 1
  done
  return 1
}
find_window() { wid=$(xdotool search --onlyvisible --name '^Elite Studio' 2>/dev/null | head -n 1 || true); [ -n "$wid" ]; }

until_ready curl -fsS --max-time 2 -o "$out/ping.json" "http://127.0.0.1:$port/api/ping" || fail "Studio's server did not answer."
grep -Eq '"system": *"linux"' "$out/ping.json" || fail "/api/ping does not say that Studio runs on Linux."
until_ready find_window || fail "Studio's window did not open."
eval "$(xdotool getwindowgeometry --shell "$wid")"   # WIDTH and HEIGHT, in screen pixels
echo "Studio's window is ${WIDTH}x${HEIGHT}."
sleep 8   # the page draws itself and reads the game state

# Where the page draws its tabs (src/web/index.html), at the window's zoom (its width / 1400, at most 1). On a page
# taller than 860 the bar on top has three rows, and the tabs fill the middle one (30 to 76 down), left and right
# of the Profile and Help emblem (340 wide, 270 at 1180 or narrower). The right side holds Nav and Loadouts, and
# Flight too where it is not hidden, so those two are clicked where they are either way.
read -r ui market nav loadouts help tabs_y help_y folded < <(awk -v w="$WIDTH" -v h="$HEIGHT" 'BEGIN {
  z = int(w / 14 + 0.5) / 100; if (z > 1) z = 1; if (z < 0.6) z = 0.6
  em = w / z > 1180 ? 340 : 270; side = (w / z - em) / 2; right = side + em
  printf "%d %d %d %d %d %d %d %d\n", z * side / 4, z * side * 3 / 4, z * (right + side / 6), z * (right + side * 7 / 12),
    z * (side + em * 3 / 4), z * 53, z * 64, (h / z <= 860) }')
[ "$folded" = 0 ] || echo "::warning::The window is ${WIDTH}x${HEIGHT}: its bar folds into one row there, so the clicks may miss the tabs."

# Clicks there, gives the page a moment, then pictures the whole screen.
shot() {
  xdotool mousemove --window "$wid" "$2" "$3" click 1
  sleep 5
  import -window root "$out/$1.png"
}
shot 1-ui "$ui" "$tabs_y"
shot 2-market "$market" "$tabs_y"
shot 3-nav "$nav" "$tabs_y"
shot 4-loadouts "$loadouts" "$tabs_y"
shot 5-help "$help" "$help_y"   # last, so its guide never has to be closed

# Quit the way the page does: POST /api/app/quit, with the token the server writes into the page.
token=$(curl -fsS "http://127.0.0.1:$port/" | sed -n 's/.*name="studio-token" content="\([0-9a-f]*\)".*/\1/p')
curl -fsS -X POST -H "X-Studio-Token: $token" -H "Content-Type: application/json" -d '{}' \
  "http://127.0.0.1:$port/api/app/quit" > /dev/null || fail "Studio refused the request to quit."
for _ in $(seq 20); do kill -0 "$studio" 2>/dev/null || break; sleep 1; done
if kill -0 "$studio" 2>/dev/null; then kill "$studio"; fail "Studio did not quit when asked."; fi
code=0; wait "$studio" || code=$?
echo "Studio quit when asked (exit code $code). Pictures: $(cd "$out" && echo *.png)"
