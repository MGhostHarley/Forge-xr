# Walkthroughs generated from the instruction booklet

Hand Forge a page of the LEGO instruction booklet (the official PDF, or a phone photo of the page) and the vision
model writes the walkthrough: steps, parts, highlights and the automatic step checks. The headset then runs it
exactly like the hand-written Driver / Wheel walkthroughs. No authoring, no CAD.

## Results on set 40650 (official booklet PDF)

| Booklet page | Generated | vs hand-written (`compare_walkthroughs.py`) |
|---|---|---|
| p4 driver | 1. legs on the table, then light-blue torso onto the legs  2. head onto the torso  3. orange hairpiece onto the head | 3/3 joins, same order, 3/3 steps auto-checked (same as hand-written) |
| p45 wheel | 1. dark grey rim inside the black tire  2. wheel onto each axle (repeat 5x) | 2/2 joins, same order, both auto-checked (hand-written: 4 steps, 2 auto) |

A simulated phone photo of p4 (skewed, dimmer, blurred) gave the same driver walkthrough.

## Pieces

| File | What it does |
|---|---|
| `walkthrough_spec.py` | The walkthrough format (JSON) + validation + clean-up of the model's answer + step rules for the judge |
| `generate_walkthrough.py` | Booklet PDF pages or page images -> spec (Nemotron, ~1 call per page); keeps the raw answer (`<id>.raw.json`); `--raw` re-derives offline |
| `booklet_pages.py` | Numbered contact sheet of a booklet PDF, to pick the pages (no API) |
| `demo_generate.sh` | Pitch moment in one command: generate, load on the relay, switch the headset |
| `compare_walkthroughs.py` | Generated vs hand-written: joins covered, order, auto-checked steps |
| `walkthroughs/*.json` | Generated specs (`booklet-40650-*`) and the hand-written ones in the same format (`manual-*`) |
| `relay.py` | `GET /walkthrough` (active spec for the Quest), `GET /walkthroughs`, `GET /walkthroughs/review` (laptop page), `POST /walkthrough?id=X[&select=1]` |
| `step_check.py` | `register_flow("gen", ...)`: the judge auto-completes generated steps with the spec's rules |
| Quest `GeneratedWalkthrough.cs` | Harley's `DesktopWalkthrough` API from the downloaded spec (runs like Driver/Wheel) |
| Quest `WalkthroughLoader.cs` | Polls `/walkthrough`; reloads on change; switches the headset when the operator selects it |
| Quest `QuestWalkthroughDriver.cs` | Third flow `Generated`: cue "n/N: instruction" + "Check: ..." line, highlights, Next/Done/Start over, state `gen,step=N` |
| Quest `Setup_GeneratedWalkthrough.cs` | Setup step 615: adds and wires the two components |

## How the model's answer is made reliable

The model's raw answer varies from run to run; deterministic clean-up makes the result stable:
- Part ids and real colours come from the detector's part list in the prompt; parts it doesn't know become text-only.
- The progress bar at the bottom of each page is cropped off (its little figure icon caused invented steps).
- "Place X on the table" is folded into the next step; "Repeat ..." steps become a note; identical consecutive steps
  (one per wheel) collapse into one "(repeat Nx)" step, with "front left" etc. replaced by "each".
- Step checks reuse the hand-tuned rules for known part pairs (torso+legs, head+torso, hair+head, rim in tire, wheel
  on axle pin), never reuse a pair an earlier step joined (it would complete instantly), and never invent a rule
  between parts whose real joins are known; a step without a safe rule is manual (Next).
- "axle" in an instruction means the car's axle pin (a detectable part).

## Run it

```bash
# laptop tests (no network, no headset)
python3 -I tools/vision-relay/test_walkthroughs.py && python3 -I tools/vision-relay/test_step_check.py

# pick pages, generate, compare
python3 -I tools/vision-relay/booklet_pages.py ~/booklets/40650.pdf --out /tmp/pages.jpg
python3 -I tools/vision-relay/generate_walkthrough.py --pdf ~/booklets/40650.pdf --pages 4 --id booklet-40650-driver
python3 -I tools/vision-relay/compare_walkthroughs.py tools/vision-relay/walkthroughs/booklet-40650-driver.json \
        tools/vision-relay/walkthroughs/manual-driver.json

# demo (relay running via start_demo.sh; needs the APK built from this branch)
tools/vision-relay/demo_generate.sh ~/booklets/40650.pdf 4 my-driver   # or: demo_generate.sh photo.jpg - my-driver
open http://127.0.0.1:8787/walkthroughs/review                        # "Show on the headset" buttons
```

On the headset: the generated flow is operator-only (never auto-selected), so Driver/Wheel behave exactly as before.
Switch to it with `select=1` (above), the review page button, left controller **Y** (cycles Driver -> Wheel ->
Generated), or live config `"QuestWalkthroughDriver.forceFlow": 2`.

Booklet PDFs, page renders and photos are LEGO's / private: keep them out of the repo (only the generated JSON is
committed). The official 40650 PDF: https://www.lego.com/en-us/service/building-instructions/40650

## Status / limits

- Laptop side: done and tested (18 + 21 tests, real relay CLI, PDF and photo inputs).
- Quest side: compiles with 0 errors (checked against the project); not yet built into an APK or run on the headset
  (the demo build is frozen, tag `demo-freeze`). To try it: merge this branch, `start_demo.sh`-style build with
  `ForgeBuild.BuildQuest -forgeSetup`, install, then `demo_generate.sh`.
- Boxes and auto-checks only for parts the detector knows (the 7 parts of 40650); other parts get text-only guidance.
- One booklet page per walkthrough works best; multi-page (`--pages 44-45`) merges steps in page order.
