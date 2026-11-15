using UnityEditor;
using UnityEngine;

namespace Forge.EditorTools
{
    /// <summary>
    /// Generated walkthroughs (from an instruction-booklet page, tools/vision-relay/generate_walkthrough.py): adds a
    /// disabled GeneratedWalkthrough and a WalkthroughLoader next to Harley's walkthroughs on ForgeRig/Walkthrough and
    /// wires them into QuestWalkthroughDriver. The flow is operator-selected only (left Y cycles Driver/Wheel/Generated,
    /// or POST /walkthrough?id=...&amp;select=1 on the laptop), so the Driver/Wheel demo behaves exactly as before.
    /// </summary>
    public static class Setup_GeneratedWalkthrough
    {
        [ForgeSetupStep(615)]
        public static void InstallGeneratedWalkthrough()
        {
            var rig = ForgeSetupUtil.GetOrCreateRoot(ForgeScene.RigRoot);
            var go = rig.transform.Find(Setup_DriverWalkthrough.WalkthroughName)?.gameObject;
            if (go == null)
            {
                Debug.LogWarning("[ForgeSetup] ForgeRig/Walkthrough not found; generated walkthrough skipped");
                return;
            }

            var generated = ForgeSetupUtil.GetOrAdd<GeneratedWalkthrough>(go);
            generated.enabled = false; // keep the desktop IMGUI panel off, like Harley's components
            EditorUtility.SetDirty(generated);

            var loader = ForgeSetupUtil.GetOrAdd<WalkthroughLoader>(go);
            var cloud = Object.FindAnyObjectByType<CloudVisionClassifier>(FindObjectsInactive.Include);
            loader.Configure(generated, cloud);
            EditorUtility.SetDirty(loader);

            var driver = go.GetComponent<QuestWalkthroughDriver>();
            if (driver == null)
            {
                Debug.LogWarning("[ForgeSetup] No QuestWalkthroughDriver on ForgeRig/Walkthrough; it finds the loader at runtime");
                return;
            }
            driver.ConfigureGenerated(generated, loader);
            EditorUtility.SetDirty(driver);
        }
    }
}
