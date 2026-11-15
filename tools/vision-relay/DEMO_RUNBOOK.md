# Forge XR demo runbook (Quest + laptop relay)

The Quest app sends camera frames over the USB cable to the laptop relay, which detects the LEGO parts
(local YOLO, ~0.1 s), judges step completion, and serves live tuning. Everything below runs on the laptop.

## 1. Before the demo (2 min)

Quick path: `tools/vision-relay/start_demo.sh` (port forward + relay with both models + app), then put the
headset on, look at the table and run `tools/vision-relay/preflight.sh` until it prints READY.
Default models: `~/forge-models/pieces-e12.pt,~/forge-models/pieces-e5.pt` (ensemble: e12 is best in the dim
room, e5 generalises better to bright spaces). Override with `FORGE_WEIGHTS=...`.

Manual path:

```bash
cd Forge-xr
# a) USB + port forward (re-run after any replug / adb restart)
adb devices                      # Quest listed as "device" (accept the USB prompt in the headset)
adb reverse tcp:8787 tcp:8787

# b) relay with the local detector (no rate limit). Model file lives outside the repo.
~/.venvs/forge-detector/bin/python -I tools/vision-relay/relay.py --host 127.0.0.1 \
    --detector local --weights ~/forge-models/pieces.pt --no-log

# c) launch the app (or from the headset's app library: Unknown Sources > ForgeXR)
adb shell monkey -p com.forge.forgexr -c android.intent.category.LAUNCHER 1
```

Check: `curl -s 127.0.0.1:8787/health` and open http://127.0.0.1:8787/review (live annotated frames).
Close any other camera app first (e.g. Harley's capture app `com.forge.forgexr.capture`), it holds the camera.

## 2. Detector modes (restart the relay to switch)

| Flag | When |
|---|---|
| `--detector local` (default for the demo) | always works, 5 answers/s, no internet needed |
| `--detector hybrid` | unseen/bright space and local misses pieces: adds a Nemotron check every 4 s (needs `~/nvapi.env`, internet; falls back to local if throttled) |
| `--detector nvidia` | cloud only, ~2 s per answer, rate limited, last resort |
| `--weights ~/forge-models/pieces-e5.pt` | roll back to the older model |

Without `--no-log` the relay saves frames to `--log-dir` (default `~/forge-relay-log`). Never commit those.

## 3. During the demo

Demo order: **Driver first** (all 4 parts detected, auto step-complete with chime + confetti), then **Wheel** as
a short second act. For Wheel only the tire is detected; rim / axle highlights are missing and steps 2-3 need
**Next** (or `/step/force`). Stay on `--detector local` for Wheel (Nemotron mislabels the tire as "rim").
Keep wheel parts off the table during Driver (a stray "tire" can auto-switch the walkthrough), or lock the
walkthrough with left-controller **Y**.


- Instruction floats above the highlighted piece ("1/3: ..."). Pinch **Next** / **Reset** buttons.
- Walkthrough Driver/Wheel is auto-picked from the parts on the table; force it with left-controller **Y**
  (or **W** in the editor).
- Step completion: when the step's pieces are joined (~1 s), the relay confirms (Nemotron yes/no if available,
  else ~3 s geometry hold) and the headset plays a chime + confetti and advances.
- Operator buttons from the laptop:
  - mark the current step done: `curl -X POST 127.0.0.1:8787/step/force`
  - clear the judge: `curl -X POST 127.0.0.1:8787/step/reset`
  - judge status: `curl 127.0.0.1:8787/step`
  - turn auto-complete off: set `"enabled": false` in `tools/vision-relay/step_config.json` (live)

## 4. Live tuning without a rebuild

Edit `tools/vision-relay/quest_config.json`; the headset applies it within ~3 s (`CONFIG applied` in logcat).

| Key | Meaning |
|---|---|
| `CloudVisionClassifier.minInterval` | seconds between frames sent (0.2 = 5/s) |
| `PieceTracker.maxMissedAnswers` / `handMissedAnswers` | answers a piece may be missed before its box hides (table / in hand) |
| `ColorPieceTracker.minMatch` | colour tracking strictness between answers |
| `PickupSpot.activeLineWidth` | box line width |

Step thresholds: `tools/vision-relay/step_config.json` (re-read on change).

## 5. Troubleshooting

| Symptom | Fix |
|---|---|
| no boxes, relay log silent | `adb reverse tcp:8787 tcp:8787`; app in front? relaunch (1c) |
| relay log shows frames but image is black | headset asleep/off-face or cameras covered; put it on |
| boxes freeze / lag | another heavy job on the laptop (training)? stop it. Check `/review` latency |
| a piece is never found | try `--detector hybrid`; brighter light helps; `/review` shows what the camera sees |
| confetti/advance at the wrong time | `curl -X POST .../step/reset`, raise `hold_seconds` or set `enabled: false` |
| no chime | the app was launched while the headset was asleep (Horizon mutes it): wear the headset, rerun `start_demo.sh` (or relaunch from the headset's app library); you should hear a test chime ~2 s after launch |
| headset app is not the demo build (someone installed another APK) | `adb install -r ~/forge-models/ForgeXR-demo-freeze.apk` (frozen build, git tag `demo-freeze`) |
| need to finish a step anyway | pinch Next, or `curl -X POST 127.0.0.1:8787/step/force` |

Diagnostics on the headset: `adb logcat -s Unity | grep ForgeDiag` (TRACKS, ANSWER, STEP, CONFIG lines).
