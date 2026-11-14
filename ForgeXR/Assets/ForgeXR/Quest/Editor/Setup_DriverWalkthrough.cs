using UnityEditor;
using UnityEngine;

namespace Forge.EditorTools
{
    /// <summary>
    /// Walkthrough integration setup steps (600-699): installs Harley's DriverWalkthrough (disabled,
    /// so its desktop IMGUI never runs) with the Quest adapter, and retires the temporary
    /// MilestoneSequence harness. Find-or-create by name, safe to run twice.
    /// </summary>
    public static class Setup_DriverWalkthrough
    {
        public const string WalkthroughName = "Walkthrough";
        public const string FloatingCueName = "FloatingCue";
        // Own copies of names Setup_NextInput uses (that file belongs to ps/pinch-ui).
        public const string NextInputName = "NextInput";
        public const string MilestoneName = "MilestoneSequence";

        [ForgeSetupStep(600)]
        public static void InstallDriverWalkthrough()
        {
            var rig = ForgeSetupUtil.GetOrCreateRoot(ForgeScene.RigRoot);
            var go = ForgeSetupUtil.GetOrCreateChild(rig.transform, WalkthroughName);

            // Harley's components stay disabled: public API works, OnGUI does not run.
            // Stills stay unassigned (no photos on Quest).
            var walkthrough = ForgeSetupUtil.GetOrAdd<ForgeXR.DriverWalkthrough>(go);
            walkthrough.enabled = false;
            EditorUtility.SetDirty(walkthrough);
            var wheel = ForgeSetupUtil.GetOrAdd<ForgeXR.WheelWalkthrough>(go);
            wheel.enabled = false;
            EditorUtility.SetDirty(wheel);

            // Harley's Driver/Wheel selector (as in AssemblyWalkthroughs ForgeDemo.unity), disabled too.
            var demo = ForgeSetupUtil.GetOrAdd<ForgeXR.DemoWalkthrough>(go);
            demo.enabled = false;
            var demoSo = new SerializedObject(demo);
            demoSo.FindProperty("driver").objectReferenceValue = walkthrough;
            demoSo.FindProperty("wheel").objectReferenceValue = wheel;
            demoSo.ApplyModifiedPropertiesWithoutUndo();

            var tracker = Object.FindAnyObjectByType<PieceTracker>(FindObjectsInactive.Include);
            if (tracker == null) Debug.LogWarning("[ForgeSetup] No PieceTracker; walkthrough auto-select will search at runtime");

            var input = rig.transform.Find(NextInputName)?.GetComponent<NextInput>();
            if (input == null) Debug.LogWarning("[ForgeSetup] ForgeRig/NextInput not found; driver will search at runtime");

            var cue = rig.transform.Find(FloatingCueName)?.GetComponent<FloatingCue>();
            if (cue == null) Debug.LogWarning("[ForgeSetup] ForgeRig/FloatingCue not found; driver will search at runtime");

            var driver = ForgeSetupUtil.GetOrAdd<QuestWalkthroughDriver>(go);
            driver.Configure(demo, walkthrough, wheel, input, cue, tracker);
            EditorUtility.SetDirty(driver);
        }

        /// <summary>Replaces the temporary harness. Step 420 still creates it; this removes it again.</summary>
        [ForgeSetupStep(610)]
        public static void RetireMilestoneSequence()
        {
            var rig = ForgeSetupUtil.GetOrCreateRoot(ForgeScene.RigRoot);
            var old = rig.transform.Find(MilestoneName);
            if (old != null) Object.DestroyImmediate(old.gameObject);
        }
    }
}
