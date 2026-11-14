using System;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using MetaFeatures = UnityEngine.XR.OpenXR.Features.Meta;
using AndroidXRFeatures = UnityEngine.XR.OpenXR.Features.Android;

namespace Forge.EditorTools
{
    /// <summary>
    /// Chunk 1 (setup orders 100-199): passthrough, project settings and template cleanup.
    /// Every step is idempotent and safe to run twice.
    /// </summary>
    public static class SetupPassthrough
    {
        /// <summary>Chunk 1's headset proof. Set to false and re-run setup to remove the cube.</summary>
        const bool ShowDebugCube = false;
        const string DebugCubeName = "ForgeDebugCube";

        const string ArSessionName = "AR Session";

        /// <summary>
        /// Template scene roots that have no place in the AR app. "Floor" (grey plane + teleport area)
        /// and "HelloWorld" come from HelloWorld.unity; the rest only exist in the old VR-template scene.
        /// Keeps Directional Light, EventSystem and XR Interaction Manager.
        /// </summary>
        static readonly string[] TemplateRootsToDisable =
        {
            "Floor", "HelloWorld", "Environment", "UI", "Interactables", "Teleport Area Setup",
        };

        /// <summary>Template objects inside the XR Origin to disable (searched recursively).</summary>
        static readonly string[] RigChildrenToDisable =
        {
            "Locomotion",                 // turn, move, teleport, climb, gravity, jump, grab move
            "Affordance Callouts Left",   // controller tutorial labels
            "Affordance Callouts Right",
        };

        /// <summary>HandVisualizer fields that reference the per-platform hand mesh GameObjects.</summary>
        static readonly string[] HandMeshFields =
        {
            "m_MetaQuestLeftHandMesh", "m_MetaQuestRightHandMesh",
            "m_AndroidXRLeftHandMesh", "m_AndroidXRRightHandMesh",
        };

        // ------------------------------------------------------------------ 100

        [ForgeSetupStep(100)]
        static void EnableOpenXRFeatures()
        {
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android)
                ?? throw new InvalidOperationException("No OpenXR settings for Android");

            // Meta Quest variants: on. Planes and Anchors are used by chunk 3.
            SetFeature<MetaFeatures.ARSessionFeature>(settings, true);
            SetFeature<MetaFeatures.ARCameraFeature>(settings, true);
            SetFeature<MetaFeatures.ARPlaneFeature>(settings, true);
            SetFeature<MetaFeatures.ARRaycastFeature>(settings, true);
            SetFeature<MetaFeatures.ARAnchorFeature>(settings, true);

            // Android XR variants of the same subsystems: off, so they never compete with Meta's.
            SetFeature<AndroidXRFeatures.ARSessionFeature>(settings, false, optional: true);
            SetFeature<AndroidXRFeatures.ARCameraFeature>(settings, false, optional: true);
            SetFeature<AndroidXRFeatures.ARPlaneFeature>(settings, false, optional: true);
            SetFeature<AndroidXRFeatures.ARRaycastFeature>(settings, false, optional: true);
            SetFeature<AndroidXRFeatures.ARAnchorFeature>(settings, false, optional: true);

            // com.oculus.permission.USE_SCENE is injected by meta-openxr's ModifyAndroidManifestMeta
            // whenever Planes or Raycast is enabled, so no manifest edit is needed here.
        }

        static void SetFeature<T>(OpenXRSettings settings, bool enabled, bool optional = false) where T : OpenXRFeature
        {
            var feature = settings.GetFeature<T>();
            if (feature == null)
            {
                if (optional) return;
                throw new InvalidOperationException($"OpenXR feature {typeof(T).FullName} not found for Android");
            }
            if (feature.enabled != enabled)
            {
                feature.enabled = enabled;
                EditorUtility.SetDirty(feature);
            }
            if (feature.enabled != enabled)
                throw new InvalidOperationException($"Could not set {typeof(T).FullName}.enabled = {enabled}");
        }

        // ------------------------------------------------------------------ 110

        [ForgeSetupStep(110)]
        static void ConfigureUrp()
        {
            var assets = new HashSet<UniversalRenderPipelineAsset>();
            if (GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset def) assets.Add(def);
            for (var i = 0; i < QualitySettings.names.Length; i++)
                if (QualitySettings.GetRenderPipelineAssetAt(i) is UniversalRenderPipelineAsset q) assets.Add(q);

            if (assets.Count == 0)
                throw new InvalidOperationException("No URP asset found in Graphics or Quality settings");

            foreach (var asset in assets)
            {
                asset.supportsHDR = false;
                EditorUtility.SetDirty(asset);

                foreach (var data in asset.rendererDataList)
                {
                    if (data is not UniversalRendererData urd) continue;
                    urd.postProcessData = null; // = Post-processing "Enabled" unchecked
                    urd.intermediateTextureMode = IntermediateTextureMode.Auto;
                    EditorUtility.SetDirty(urd);
                }
            }
        }

        // ------------------------------------------------------------------ 120

        [ForgeSetupStep(120)]
        static void ConfigureMainCamera()
        {
            var camera = FindOrigin().Camera
                ?? throw new InvalidOperationException("XR Origin has no camera");

            ForgeSetupUtil.GetOrAdd<ARCameraManager>(camera.gameObject).enabled = true;

            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            camera.GetUniversalAdditionalCameraData().renderPostProcessing = false;

            EditorUtility.SetDirty(camera);
        }

        // ------------------------------------------------------------------ 130

        [ForgeSetupStep(130)]
        static void EnsureArSession()
        {
            var go = ForgeSetupUtil.GetOrCreateRoot(ArSessionName);
            go.SetActive(true);
            ForgeSetupUtil.GetOrAdd<ARSession>(go).enabled = true;
        }

        // ------------------------------------------------------------------ 140

        [ForgeSetupStep(140)]
        static void DisableTemplateRoots()
        {
            var roots = EditorSceneManager.GetActiveScene().GetRootGameObjects();
            foreach (var name in TemplateRootsToDisable)
            {
                var root = Array.Find(roots, r => r.name == name);
                if (root != null) root.SetActive(false);
                else Debug.Log($"[ForgeSetup] Root '{name}' not in scene, nothing to disable");
            }
        }

        // ------------------------------------------------------------------ 150

        [ForgeSetupStep(150)]
        static void DisableRigTemplateParts()
        {
            var origin = FindOrigin().transform;
            foreach (var name in RigChildrenToDisable)
            {
                var t = FindDeep(origin, name);
                if (t != null) t.gameObject.SetActive(false);
                else Debug.Log($"[ForgeSetup] '{name}' not found under XR Origin, nothing to disable");
            }
        }

        // ------------------------------------------------------------------ 160

        /// <summary>
        /// Hides the virtual hand meshes (real hands show in passthrough). Hand tracking,
        /// XRInputModalityManager, poke and near-far interactors are left untouched.
        /// HandVisualizer (XR Hands sample) is found by type name so this file does not depend on the sample assembly.
        /// </summary>
        [ForgeSetupStep(160)]
        static void HideHandMeshes()
        {
            var origin = FindOrigin();
            var found = false;
            foreach (var mb in origin.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null || mb.GetType().Name != "HandVisualizer") continue;
                found = true;

                var so = new SerializedObject(mb);
                var draw = so.FindProperty("m_DrawMeshes");
                if (draw != null) draw.boolValue = false;
                var joints = so.FindProperty("m_DebugDrawJoints");
                if (joints != null) joints.boolValue = false;
                so.ApplyModifiedPropertiesWithoutUndo();

                // Belt and braces: disable the renderers on the referenced hand mesh objects.
                foreach (var field in HandMeshFields)
                {
                    if (so.FindProperty(field)?.objectReferenceValue is not GameObject mesh) continue;
                    foreach (var r in mesh.GetComponentsInChildren<Renderer>(true))
                    {
                        r.enabled = false;
                        EditorUtility.SetDirty(r);
                    }
                }
            }
            if (!found)
                Debug.LogWarning("[ForgeSetup] No HandVisualizer under XR Origin; hand meshes not hidden");
        }

        // ------------------------------------------------------------------ 190

        [ForgeSetupStep(190)]
        static void DebugTestCube()
        {
            GameObject cube = null;
            foreach (var root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
                if (root.name == DebugCubeName) { cube = root; break; }

#pragma warning disable CS0162 // unreachable code, depending on the toggle
            if (!ShowDebugCube)
            {
                if (cube != null) UnityEngine.Object.DestroyImmediate(cube);
                return;
            }
#pragma warning restore CS0162

            if (cube == null)
            {
                cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = DebugCubeName;
                var collider = cube.GetComponent<Collider>();
                if (collider != null) UnityEngine.Object.DestroyImmediate(collider); // keep XRI from interacting with it
            }

            // Relative to the XR Origin: ~table height, 60 cm in front of the start pose.
            var origin = FindOrigin().transform;
            cube.transform.SetPositionAndRotation(
                origin.TransformPoint(new Vector3(0f, 1.0f, 0.6f)),
                origin.rotation);
            cube.transform.localScale = Vector3.one * 0.1f;
            cube.SetActive(true);
        }

        // ------------------------------------------------------------------ helpers

        static XROrigin FindOrigin()
        {
            return UnityEngine.Object.FindAnyObjectByType<XROrigin>()
                ?? throw new InvalidOperationException("No active XROrigin in the scene");
        }

        static Transform FindDeep(Transform parent, string name)
        {
            foreach (Transform child in parent)
            {
                if (child.name == name) return child;
                var hit = FindDeep(child, name);
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
