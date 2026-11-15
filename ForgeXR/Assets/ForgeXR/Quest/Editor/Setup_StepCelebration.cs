using UnityEditor;
using UnityEngine;

namespace Forge.EditorTools
{
    /// <summary>
    /// Automatic step completion (order 620, after the walkthrough at 600): ForgeRig/StepCelebration with
    /// StepCelebration + AudioSource and a child "Confetti" ParticleSystem. The relay decides when a step is done
    /// (tools/vision-relay/step_check.py); this plays the chime + confetti and presses Next.
    /// Confetti material: the project's VRTemplate ConfettiParticles (URP Particles/Unlit) if present, else
    /// Assets/ForgeXR/Quest/Materials/Confetti.mat (created once). Find-or-create by name, safe to run twice.
    /// </summary>
    public static class Setup_StepCelebration
    {
        public const string Name = "StepCelebration";
        public const string ConfettiName = "Confetti";
        const string ExistingMaterialPath = "Assets/VRTemplateAssets/Materials/Particles/ConfettiParticles.mat";
        const string MaterialFolder = "Assets/ForgeXR/Quest/Materials";
        const string OwnMaterialPath = MaterialFolder + "/Confetti.mat";
        const string ParticleShader = "Universal Render Pipeline/Particles/Unlit";

        [ForgeSetupStep(620)]
        public static void InstallStepCelebration()
        {
            var rig = ForgeSetupUtil.GetOrCreateRoot(ForgeScene.RigRoot);
            var go = ForgeSetupUtil.GetOrCreateChild(rig.transform, Name);
            var celebration = ForgeSetupUtil.GetOrAdd<StepCelebration>(go);

            var audio = ForgeSetupUtil.GetOrAdd<AudioSource>(go);
            audio.playOnAwake = false;
            audio.loop = false;
            audio.spatialBlend = 0.6f; // mostly from the piece, still clearly audible
            audio.minDistance = 0.5f;
            audio.maxDistance = 10f;
            audio.rolloffMode = AudioRolloffMode.Linear;
            EditorUtility.SetDirty(audio);

            var confettiGo = ForgeSetupUtil.GetOrCreateChild(go.transform, ConfettiName);
            var ps = ForgeSetupUtil.GetOrAdd<ParticleSystem>(confettiGo);
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var renderer = confettiGo.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                var mat = ConfettiMaterial();
                if (mat != null) renderer.sharedMaterial = mat;
                else Debug.LogWarning("[ForgeSetup] No confetti material (URP Particles/Unlit shader not found)");
                EditorUtility.SetDirty(renderer);
            }
            StepCelebration.ConfigureConfetti(ps);
            EditorUtility.SetDirty(ps);

            var cloud = rig.transform.Find(Setup_CloudVision.Name)?.GetComponent<CloudVisionClassifier>();
            if (cloud == null) Debug.LogWarning("[ForgeSetup] ForgeRig/CloudVision not found; StepCelebration will search at runtime");
            var input = rig.transform.Find(Setup_NextInput.NextInputName)?.GetComponent<NextInput>();
            if (input == null) Debug.LogWarning("[ForgeSetup] ForgeRig/NextInput not found; StepCelebration will search at runtime");
            var tracker = Object.FindAnyObjectByType<PieceTracker>(FindObjectsInactive.Include);

            celebration.Configure(cloud, input, tracker, audio, ps);
            EditorUtility.SetDirty(celebration);
        }

        static Material ConfettiMaterial()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(ExistingMaterialPath);
            if (existing != null && existing.shader != null && existing.shader.name == ParticleShader) return existing;

            var own = AssetDatabase.LoadAssetAtPath<Material>(OwnMaterialPath);
            if (own != null) return own;

            var shader = Shader.Find(ParticleShader);
            if (shader == null) return null;
            if (!AssetDatabase.IsValidFolder(MaterialFolder))
                AssetDatabase.CreateFolder("Assets/ForgeXR/Quest", "Materials");
            own = new Material(shader) { name = "Confetti" };
            AssetDatabase.CreateAsset(own, OwnMaterialPath);
            Debug.Log($"[ForgeSetup] Created {OwnMaterialPath}");
            return own;
        }
    }
}
