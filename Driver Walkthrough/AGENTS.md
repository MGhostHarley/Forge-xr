# LEGO driver walkthrough

Use this package to build the first Forge XR headset demo. A builder assembles the LEGO 40650 driver in three steps while Forge shows the current instruction, a reference photo, and a highlight over the corresponding pickup spot.

## Import the assets

1. If the current branch has no Unity project, check the root README for the team's Unity branch. At handoff, `ps/unity-hello-world` contains the project under `ForgeXR/`. Open it from a separate checkout or integration branch and use the editor version in `ForgeXR/ProjectSettings/ProjectVersion.txt`.
2. Copy `Assets/ForgeXR/DriverWalkthrough` from this package into the project's `Assets/ForgeXR` directory. Keep the part photos in `Reference` available for physical staging.
3. For a Unity UI Image, import the three JPEGs as **Sprite (2D and UI)**. Preserve their aspect ratios. The first two stills show the figure sideways. Rotate those two displays 90 degrees clockwise and leave the finished-driver display upright. Verify all three in Play Mode.
4. Keep the filenames stable so each still can be traced back to the step and source photo in [README.md](README.md). When integrating the assets into a Unity project, commit the Unity-generated `.meta` files alongside them.

## Build the walkthrough

Use a separate desktop test scene while Phil works on the Quest scene and project settings. Reuse existing step, input, and highlight components where available.

| Step | Instruction | Still | Highlight |
| --- | --- | --- | --- |
| 1 | Press the torso onto the legs with the shirt print facing the toes. | `01-attach-torso-to-legs.jpg` | Tan legs and blue torso |
| 2 | Press the head onto the neck with the face pointing forward. | `02-attach-head.jpg` | Yellow head |
| 3 | Press the hairpiece onto the head. | `03-add-hair-finished-driver.jpg` | Hairpiece |

Show step 1 on start. Use Space to call the same advance action that Phil will connect to the Quest controller. On steps 1 and 2, show **Next**. On step 3, show **Done**. An advance updates the instruction, photo, and highlights together and clears the previous highlights. On completion, clear the remaining highlights and show that the build is complete. Reset returns to step 1.

## Integrate and verify on Quest

1. Agree with Phil on the four highlight targets and the controller action. Stage the loose parts left to right in this order from the builder's view: legs, torso, head, hairpiece. Mark the pickup spots so the same layout can be restored between runs.
2. Place the pickup highlights relative to Phil's calibrated tabletop reference. Keep them over the pickup spots as the builder handles the pieces. Use the photos to show placement and orientation.
3. In Play Mode, verify that each Space press advances exactly one step, that the stills are upright, and that completion and reset work.
4. On Quest, check that the torso highlight stays aligned while the wearer leans in. Complete step 1 and press Next once. Confirm that the head highlight appears with its matching instruction and still.
5. Have a first-time builder complete all three steps without coaching. Restage the pieces and repeat the walkthrough once to confirm reset.

Harley owns the walkthrough content and desktop scene. Phil owns Quest deployment, passthrough, tabletop alignment, and controller input. Pair on the first headset build. Add voice guidance after the controller walkthrough works end to end.
