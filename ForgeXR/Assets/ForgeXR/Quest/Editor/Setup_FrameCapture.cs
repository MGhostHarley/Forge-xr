using UnityEditor;
using UnityEngine;

namespace Forge.EditorTools
{
    /// <summary>
    /// "Save frame" pinch button under the floating cue (layout from Harley's quest-pinch-capture branch) wired to
    /// ForgeRig/FrameCapture, which saves clean camera frames whatever classifier runs. Set <see cref="Enabled"/> =
    /// false and rerun setup to remove it for the demo.
    /// </summary>
    public static class Setup_FrameCapture
    {
        const bool Enabled = false; // demo build: no "Save frame" button (data collection is done)
        public const string Name = "FrameCapture";
        public const string ButtonName = "SaveFrameButton";

        [ForgeSetupStep(535)]
        public static void InstallFrameCapture()
        {
            var rig = ForgeSetupUtil.GetOrCreateRoot(Forge.ForgeScene.RigRoot);
            var cue = Setup_NextInput.GetCue();
            var existingButton = cue != null ? cue.transform.Find(ButtonName) : null;
            var existing = rig.transform.Find(Name);
            if (!Enabled)
            {
                if (existing != null) Object.DestroyImmediate(existing.gameObject);
                if (existingButton != null) Object.DestroyImmediate(existingButton.gameObject);
                return;
            }
            if (cue == null) { Debug.LogWarning("[ForgeSetup] No FloatingCue; Save frame button skipped"); return; }

            var save = Setup_NextInput.CreateButton(cue.transform, ButtonName, "Save frame",
                new Vector2(0.09f, 0.035f), new Vector3(0f, -0.052f, 0f));
            var cueSo = new SerializedObject(cue);
            cueSo.FindProperty("saveButton").objectReferenceValue = save;
            cueSo.ApplyModifiedPropertiesWithoutUndo();

            var go = ForgeSetupUtil.GetOrCreateChild(rig.transform, Name);
            var capture = ForgeSetupUtil.GetOrAdd<Forge.FrameCapture>(go);
            var source = Object.FindAnyObjectByType<Forge.CameraFrameSource>(FindObjectsInactive.Include);
            capture.Configure(source, save);
            EditorUtility.SetDirty(capture);
        }
    }
}
