# Forge XR

Meta Quest 3 passthrough AR that guides LEGO 40650 assembly (the driver minifigure, then a wheel): it boxes the
parts on the real table, shows each step, and detects when a step is done (chime + confetti).

## Layout

- `ForgeXR/`: Unity 6 (6000.6.9f1) Quest app. Build: **Forge → Build Quest APK**, or batchmode
  `-executeMethod ForgeBuild.BuildQuest -forgeSetup`.
- `tools/vision-relay/`: laptop relay the headset streams camera frames to (part detection, step completion, live
  tuning). Demo: `tools/vision-relay/DEMO_RUNBOOK.md`.
- `AssemblyWalkthroughs/`, `Driver Walkthrough/`: walkthrough content.

## Requirements

- Unity 6000.6.9f1 with Android Build Support, Android SDK & NDK Tools, and OpenJDK.
- Quest in Developer Mode, connected over USB with USB debugging allowed.
- Relay: Python venv with `ultralytics` (`~/.venvs/forge-detector`), detector weights in `~/forge-models/`
  (not in the repo), NVIDIA API key in `~/nvapi.env` for hybrid mode and step confirmation.

## License

MIT, see `LICENSE`. Third-party Unity packages and samples under `ForgeXR/` keep their own licenses. LEGO is a
trademark of the LEGO Group; this project is not affiliated with or endorsed by it.
