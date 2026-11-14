# LEGO driver desktop walkthrough

Open this folder in Unity Hub with Unity 6000.6.9f1. In the Project window, open `Assets/ForgeXR/DriverWalkthrough/DriverDesktop.unity` and enter Play Mode. Click the Game view to focus it, then use Space or the on-screen button to advance. Reset restarts the sequence after completion.

The scene displays three assembly stills in order. The first two JPEGs have already been rotated upright. The desktop view identifies the part to pick up by name.

For Quest integration, use the walkthrough's public `Advance()` action for the controller. Use the current step to drive the Quest UI and Phil's tabletop highlights. The desktop scene runs separately from the Quest scene.

## Chassis and wheel walkthrough

Open `Assets/ForgeXR/WheelWalkthrough/WheelDesktop.unity` and enter Play Mode.
Stage one loose tire, one rim, and the chassis with the same orientation as
`WheelWalkthrough/Reference/staging-and-target-axle.jpg`. The walkthrough shows
pickup, tire assembly, axle alignment, and the mounted-wheel check. Space or
Next advances. Done completes the walkthrough, and Reset returns to pickup.
