using System;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.OpenXR;
using MetaFeatures = UnityEngine.XR.OpenXR.Features.Meta;

namespace Forge.EditorTools
{
    /// <summary>
    /// Camera placement chunk (orders 500-599): camera frames in, highlights on the real pieces out.
    ///   500 Camera Image Support on the Meta Quest Camera (Passthrough) feature (adds HEADSET_CAMERA etc. to the manifest).
    ///   510 ForgeRig/CameraPlacement: PassthroughCameraModel, CameraFrameSource, TableSurface, PieceTracker, PieceLocator.
    ///   520 DEBUG classifier stub, only while no real PieceClassifierBehaviour is in the scene.
    ///   530 ForgeRig/Permissions: ScenePermission (USE_SCENE + HEADSET_CAMERA).
    /// All idempotent.
    /// </summary>
    public static class Setup_CameraPlacement
    {
        public const string PlacementName = "CameraPlacement";
        public const string StubName = "DEBUG_ClassifierStub";
        public const string PermissionsName = "Permissions";

        [ForgeSetupStep(500)]
        public static void EnableCameraImageSupport()
        {
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android)
                ?? throw new InvalidOperationException("No OpenXR settings for Android");
            var feature = settings.GetFeature<MetaFeatures.ARCameraFeature>()
                ?? throw new InvalidOperationException("Meta ARCameraFeature not found for Android");
            if (!feature.enabled) throw new InvalidOperationException("Meta Camera (Passthrough) feature is off; step 100 should enable it");
            if (!feature.cameraImageSupportEnabled)
            {
                feature.cameraImageSupportEnabled = true;
                EditorUtility.SetDirty(feature);
            }
        }

        [ForgeSetupStep(510)]
        public static void CreateCameraPlacement()
        {
            var origin = UnityEngine.Object.FindAnyObjectByType<XROrigin>(FindObjectsInactive.Include)
                ?? throw new InvalidOperationException("No XROrigin in the scene");
            var cameraManager = origin.Camera != null ? origin.Camera.GetComponent<ARCameraManager>() : null;
            if (cameraManager == null) throw new InvalidOperationException("XR Origin camera has no ARCameraManager (step 120)");

            var rig = ForgeSetupUtil.GetOrCreateRoot(ForgeScene.RigRoot);
            var go = ForgeSetupUtil.GetOrCreateChild(rig.transform, PlacementName);
            var model = ForgeSetupUtil.GetOrAdd<PassthroughCameraModel>(go);
            var source = ForgeSetupUtil.GetOrAdd<CameraFrameSource>(go);
            var table = ForgeSetupUtil.GetOrAdd<TableSurface>(go);
            var tracker = ForgeSetupUtil.GetOrAdd<PieceTracker>(go);
            var locator = ForgeSetupUtil.GetOrAdd<PieceLocator>(go);

            Set(source, "cameraManager", cameraManager);
            Set(source, "cameraModel", model);

            var tso = new SerializedObject(table);
            tso.FindProperty("origin").objectReferenceValue = origin;
            var layer = LayerMask.NameToLayer(Setup_TableAlign.TablePlaneLayer);
            tso.FindProperty("planeMask").intValue = layer >= 0 ? 1 << layer : 0;
            tso.ApplyModifiedPropertiesWithoutUndo();

            var lso = new SerializedObject(locator);
            lso.FindProperty("frameSource").objectReferenceValue = source;
            lso.FindProperty("table").objectReferenceValue = table;
            lso.FindProperty("tracker").objectReferenceValue = tracker;
            lso.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(go);
        }

        /// <summary>
        /// Adds the DEBUG stub when the scene has no real classifier; removes it as soon as one exists.
        /// Wires PieceLocator.classifier to whichever is used (PieceLocator also prefers non-stubs at runtime).
        /// </summary>
        [ForgeSetupStep(520)]
        public static void ConfigureClassifier()
        {
            var rig = ForgeSetupUtil.GetOrCreateRoot(ForgeScene.RigRoot);
            PieceClassifierBehaviour real = null;
            foreach (var c in UnityEngine.Object.FindObjectsByType<PieceClassifierBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (c is not DebugClassifierStub) { real = c; break; }

            var stubT = rig.transform.Find(StubName);
            PieceClassifierBehaviour use;
            if (real != null)
            {
                if (stubT != null) UnityEngine.Object.DestroyImmediate(stubT.gameObject);
                use = real;
                Debug.Log($"[ForgeSetup] Using classifier {real.GetType().Name} on {real.name}; DEBUG stub removed");
            }
            else
            {
                var stubGo = ForgeSetupUtil.GetOrCreateChild(rig.transform, StubName);
                use = ForgeSetupUtil.GetOrAdd<DebugClassifierStub>(stubGo);
                Debug.LogWarning("[ForgeSetup] No real PieceClassifierBehaviour in the scene; added the DEBUG classifier stub (fake detections)");
            }

            var locator = rig.transform.Find(PlacementName)?.GetComponent<PieceLocator>();
            if (locator != null) Set(locator, "classifier", use);
        }

        [ForgeSetupStep(530)]
        public static void CreatePermissions()
        {
            var origin = UnityEngine.Object.FindAnyObjectByType<XROrigin>(FindObjectsInactive.Include);
            var rig = ForgeSetupUtil.GetOrCreateRoot(ForgeScene.RigRoot);

            // Exactly one ScenePermission, on ForgeRig/Permissions.
            var go = ForgeSetupUtil.GetOrCreateChild(rig.transform, PermissionsName);
            foreach (var p in UnityEngine.Object.FindObjectsByType<ScenePermission>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (p.gameObject != go) UnityEngine.Object.DestroyImmediate(p);
            var permission = ForgeSetupUtil.GetOrAdd<ScenePermission>(go);

            var so = new SerializedObject(permission);
            so.FindProperty("planeManager").objectReferenceValue = origin != null ? origin.GetComponent<ARPlaneManager>() : null;
            so.FindProperty("cameraManager").objectReferenceValue =
                origin != null && origin.Camera != null ? origin.Camera.GetComponent<ARCameraManager>() : null;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(permission);
        }

        static void Set(UnityEngine.Object target, string field, UnityEngine.Object value)
        {
            var so = new SerializedObject(target);
            var prop = so.FindProperty(field) ?? throw new InvalidOperationException($"{target.GetType().Name}.{field} not found");
            prop.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(target);
        }
    }
}
