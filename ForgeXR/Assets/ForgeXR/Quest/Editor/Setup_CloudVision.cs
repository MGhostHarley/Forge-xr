using UnityEditor;
using UnityEngine;

namespace Forge.EditorTools
{
    /// <summary>
    /// Cloud vision classifier (order 515, before 520 which then removes the DEBUG stub):
    /// ForgeRig/CloudVision with CloudVisionClassifier, plus the player settings it needs
    /// (plain HTTP to the laptop relay, INTERNET permission). Set <see cref="Enabled"/> = false and rerun setup
    /// to go back to the touch-to-place DEBUG stub.
    /// </summary>
    public static class Setup_CloudVision
    {
        const bool Enabled = true;
        public const string Name = "CloudVision";

        [ForgeSetupStep(515)]
        public static void InstallCloudVision()
        {
            var rig = ForgeSetupUtil.GetOrCreateRoot(Forge.ForgeScene.RigRoot);
            var existing = rig.transform.Find(Name);
            if (!Enabled)
            {
                if (existing != null) Object.DestroyImmediate(existing.gameObject);
                return;
            }

            var go = ForgeSetupUtil.GetOrCreateChild(rig.transform, Name);
            ForgeSetupUtil.GetOrAdd<Forge.CloudVisionClassifier>(go);

            // Colour tracking between slow answers, next to PieceTracker/PieceLocator on ForgeRig/CameraPlacement.
            var placement = rig.transform.Find(Setup_CameraPlacement.PlacementName);
            if (placement != null) ForgeSetupUtil.GetOrAdd<Forge.ColorPieceTracker>(placement.gameObject);

            // The relay is plain http:// on the laptop (or 127.0.0.1 via adb reverse).
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
            PlayerSettings.Android.forceInternetPermission = true;
        }
    }
}
