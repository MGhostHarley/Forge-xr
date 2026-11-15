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
        // Request pacing (written to the scene each setup run, so changing these takes effect on the next build).
        const float MinIntervalSeconds = 0.5f;
        const int MaxInFlight = 3;
        const int RequestTimeoutSeconds = 8;
        const float CameraFramesPerSecond = 10f;

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
            var cv = ForgeSetupUtil.GetOrAdd<Forge.CloudVisionClassifier>(go);
            var so = new SerializedObject(cv);
            so.FindProperty("minInterval").floatValue = MinIntervalSeconds;
            so.FindProperty("requestTimeoutSeconds").intValue = RequestTimeoutSeconds;
            so.FindProperty("maxInFlight").intValue = MaxInFlight;
            so.ApplyModifiedPropertiesWithoutUndo();

            // Colour tracking between slow answers, next to PieceTracker/PieceLocator on ForgeRig/CameraPlacement.
            var placement = rig.transform.Find(Setup_CameraPlacement.PlacementName);
            if (placement != null)
            {
                ForgeSetupUtil.GetOrAdd<Forge.ColorPieceTracker>(placement.gameObject);
                var source = placement.GetComponent<Forge.CameraFrameSource>();
                if (source != null)
                {
                    var fso = new SerializedObject(source);
                    fso.FindProperty("framesPerSecond").floatValue = CameraFramesPerSecond; // colour tracking rate
                    fso.ApplyModifiedPropertiesWithoutUndo();
                }
            }

            // The relay is plain http:// on the laptop (or 127.0.0.1 via adb reverse).
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
            PlayerSettings.Android.forceInternetPermission = true;
        }
    }
}
