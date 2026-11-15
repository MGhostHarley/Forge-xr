# Forge XR

Hackathon project for Meta Quest 3/3S.

## Branches

- `ps/unity-hello-world`: Unity 6 (6000.6.9f1) VR hello world. Open `ForgeXR/` in Unity Hub; use the **Forge → Build Quest APK** menu to build.
- `ps/hello-world`: earlier WebXR (browser) prototype.

## Requirements (Unity)

- Unity 6000.6.9f1 with Android Build Support, Android SDK & NDK Tools, and OpenJDK.
- Quest in Developer Mode, connected over USB with USB debugging allowed.

## Quest capture build

Open `ForgeXR/` and run **Forge → Setup + Build Quest APK** to generate the scene and build `ForgeXR/Builds/ForgeXR-Capture.apk`. Its package ID is `com.forge.forgexr.capture`. Install with `adb install -r ForgeXR/Builds/ForgeXR-Capture.apk`.

Open **ForgeXR Capture**, grant camera access, and pinch **Save frame** below the instructions to save one clean 640 × 640 passthrough PNG. The button reports the number saved or prompts you to retry. Fully close and relaunch the app for a new session folder. Capture **Driver** and **Chassis and Wheel** in separate sessions.

Export the sessions with `adb pull /sdcard/Android/data/com.forge.forgexr.capture/files/forge_frames ./forge_frames`.
