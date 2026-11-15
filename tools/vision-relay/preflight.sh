#!/usr/bin/env bash
# Pre-flight right before going on stage (relay must be running, headset on your head looking at the table).
#   tools/vision-relay/preflight.sh
ok() { echo "  OK   $*"; }
bad() { echo "  FAIL $*"; FAILS=$((FAILS + 1)); }
FAILS=0
R=127.0.0.1:8787

echo "Forge XR pre-flight"
adb get-state >/dev/null 2>&1 && ok "headset on adb" || bad "headset not on adb (USB, accept prompt)"
lvl=$(adb shell dumpsys battery 2>/dev/null | awk '/level/{print $2}')
[ -n "$lvl" ] && [ "$lvl" -ge 40 ] && ok "battery ${lvl}%" || bad "battery ${lvl:-?}% (charge it)"
adb reverse --list 2>/dev/null | grep -q tcp:8787 && ok "port forward" || bad "no port forward: adb reverse tcp:8787 tcp:8787"
curl -s $R/health | grep -q '"ok": true' && ok "relay: $(curl -s $R/health)" || bad "relay not running (start_demo.sh)"
adb shell pidof com.forge.forgexr >/dev/null && ok "app running" || bad "app not running"
pid=$(adb shell pidof com.forge.forgexr 2>/dev/null | tr -d '\r')
if [ -n "$pid" ]; then
  port=$(adb logcat -d 2>/dev/null | grep "creator_pid=$pid" | sed -n 's/.*port=\([0-9]*\).*/\1/p' | tail -1)
  if [ -n "$port" ] && adb logcat -d 2>/dev/null | grep -q "\[$port\] posted source mute event"; then
    bad "app audio muted by the headset (launched while asleep): run start_demo.sh again while wearing it"
  else
    ok "app audio not muted"
  fi
fi
adb shell pidof com.forge.forgexr.capture >/dev/null && bad "capture app running (holds the camera): adb shell am force-stop com.forge.forgexr.capture" || ok "no other camera app"
stats=$(curl -s $R/stats)
echo "$stats" | grep -q "no answers" && bad "no frames in the last 5 min (headset asleep / app not in front?)" || ok "frames: $stats"
med=$(echo "$stats" | sed -n 's/.*median \([0-9.]*\)s.*/\1/p')
[ -n "$med" ] && awk -v m="$med" 'BEGIN{exit !(m > 1.0)}' && bad "answers slow (median ${med}s): close other heavy jobs on the laptop"
grep -q '"enabled": true' "$(dirname "$0")/step_config.json" && ok "step-complete enabled" || echo "  --   step-complete disabled"
echo "  ..   step judge: $(curl -s $R/step | head -c 160)"
[ $FAILS -eq 0 ] && echo "READY" || echo "$FAILS problem(s) above"
