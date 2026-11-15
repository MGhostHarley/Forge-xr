#!/usr/bin/env bash
# One command for the demo: port forward (kept alive), relay, app launch, health check.
#   tools/vision-relay/start_demo.sh [local|hybrid|nvidia]      (default: local)
# Stop with Ctrl+C (stops the relay and the port-forward keeper). Model files live outside the repo.
set -u
MODE="${1:-local}"
HERE="$(cd "$(dirname "$0")" && pwd)"
PY="${FORGE_PY:-$HOME/.venvs/forge-detector/bin/python}"
WEIGHTS="${FORGE_WEIGHTS:-$HOME/forge-models/pieces-e12.pt,$HOME/forge-models/pieces-e5.pt}"
LOG_DIR="${FORGE_LOG_DIR:-$HOME/forge-relay-log}"   # outside the repo; never commit

command -v adb >/dev/null || { echo "adb not found"; exit 1; }
[ -x "$PY" ] || { echo "python venv not found: $PY"; exit 1; }

echo "== headset"
adb get-state >/dev/null 2>&1 || { echo "No Quest on adb. Plug in USB and accept the prompt in the headset."; exit 1; }
adb shell dumpsys battery | grep -E "level" | sed 's/^ */battery /'

# Keep the reverse port forward alive across replugs.
( while true; do
    if adb get-state >/dev/null 2>&1; then
      adb reverse --list 2>/dev/null | grep -q "tcp:8787" || adb reverse tcp:8787 tcp:8787 >/dev/null 2>&1
    fi
    sleep 3
  done ) &
KEEPER=$!

echo "== relay ($MODE)"
"$PY" -I "$HERE/relay.py" --host 127.0.0.1 --detector "$MODE" --weights "$WEIGHTS" \
      --log-every 20 --log-dir "$LOG_DIR" &
RELAY=$!
trap 'kill $RELAY $KEEPER 2>/dev/null; echo stopped' EXIT INT TERM

for _ in $(seq 1 60); do curl -s 127.0.0.1:8787/health >/dev/null && break; sleep 0.5; done
curl -s 127.0.0.1:8787/health; echo

echo "== app"
# Launch only while the headset is awake (on a head): Horizon mutes the audio of an app launched while asleep.
until adb shell dumpsys power 2>/dev/null | grep -q "mWakefulness=Awake"; do
  echo "   put the headset on (it is asleep)..."; sleep 2
done
adb shell am force-stop com.forge.forgexr.capture >/dev/null 2>&1   # it holds the camera
adb shell am force-stop com.forge.forgexr >/dev/null 2>&1           # fresh start = unmuted audio
adb shell monkey -p com.forge.forgexr -c android.intent.category.LAUNCHER 1 >/dev/null 2>&1
echo "   you should hear a test chime ~2 s after the app appears"
echo "launched. Live view: http://127.0.0.1:8787/review   Pre-flight: $HERE/preflight.sh"
wait $RELAY
