#!/usr/bin/env bash
# Record what the wearer sees (passthrough + Forge overlays, both eyes) on the headset itself.
#   record_headset.sh start            # records back-to-back 3-minute segments on the headset until stopped
#   record_headset.sh stop             # stops recording
#   record_headset.sh pull [DIR]       # copies the segments to DIR (default ~/forge-videos) and deletes them on the headset
#   record_headset.sh pull DIR --left  # same, plus one left-eye-only video joined from all segments (needs ffmpeg)
# Videos show your room: keep them out of git.
set -u
DEV=/sdcard/forge-rec
case "${1:-}" in
  start)
    adb shell mkdir -p $DEV
    # While the headset sleeps screenrecord fails at once: drop the empty file and wait instead of spinning.
    adb shell "nohup sh -c 'while [ ! -f $DEV/stop ]; do f=$DEV/seg_\$(date +%H%M%S).mp4; \
      screenrecord --time-limit 180 --size 1920x1008 --bit-rate 10000000 \$f; \
      if [ \$(stat -c %s \$f 2>/dev/null || echo 0) -lt 100000 ]; then rm -f \$f; sleep 5; fi; done; rm -f $DEV/stop' \
      >/dev/null 2>&1 &"
    echo "recording on the headset (3-min segments in $DEV); stop with: $0 stop"
    ;;
  stop)
    adb shell touch $DEV/stop
    adb shell pkill -INT screenrecord   # finishes the current segment cleanly
    echo "stopped"
    ;;
  pull)
    OUT="${2:-$HOME/forge-videos}"
    mkdir -p "$OUT"
    adb pull $DEV/. "$OUT/" && adb shell rm -f "$DEV/seg_*.mp4"
    ls -la "$OUT"
    if [ "${3:-}" = "--left" ] && command -v ffmpeg >/dev/null; then
      ls "$OUT"/seg_*.mp4 | sed "s/.*/file '&'/" > "$OUT/list.txt"
      ffmpeg -loglevel error -f concat -safe 0 -i "$OUT/list.txt" -vf "crop=960:1008:0:0" -c:v libx264 -crf 23 "$OUT/forge-left-eye.mp4" \
        && echo "joined: $OUT/forge-left-eye.mp4"
    fi
    ;;
  *) sed -n '2,7p' "$0"; exit 1 ;;
esac
