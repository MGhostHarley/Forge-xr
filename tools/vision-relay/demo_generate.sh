#!/usr/bin/env bash
# The pitch moment: instruction booklet page in, walkthrough in the headset. Relay must be running (start_demo.sh).
#   tools/vision-relay/demo_generate.sh BOOKLET.pdf PAGE [ID]       e.g.  ... 40650.pdf 4 my-driver
#   tools/vision-relay/demo_generate.sh PAGE_PHOTO.jpg - [ID]        (a phone photo of the page also works)
# Generates the walkthrough with the vision model (~1 API call per page), loads it on the relay and switches the
# headset to it (Next / Start over work as usual; left Y cycles back to Driver / Wheel).
set -eu
HERE="$(cd "$(dirname "$0")" && pwd)"
SRC="${1:?booklet PDF or page image}"
PAGE="${2:--}"
ID="${3:-booklet-$(date +%H%M%S)}"
RELAY="${FORGE_RELAY:-http://127.0.0.1:8787}"

if [ "$PAGE" = "-" ]; then
  python3 -I "$HERE/generate_walkthrough.py" "$SRC" --id "$ID"
else
  python3 -I "$HERE/generate_walkthrough.py" --pdf "$SRC" --pages "$PAGE" --id "$ID"
fi
curl -s -X POST "$RELAY/walkthrough?id=$ID&select=1" && echo
echo "Headset switches within ~4 s. Review: $RELAY/walkthroughs/review"
