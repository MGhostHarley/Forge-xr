using System.IO;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace Forge.EditorTools
{
    /// <summary>
    /// Former chunk 3 (orders 300-399). Manual mat alignment is gone; what remains:
    ///   300 TablePlane layer, 310 invisible detected-plane prefab (collider only, used by TableSurface),
    ///   320 ARPlaneManager/ARAnchorManager on the XR Origin,
    ///   340 cleanup of the old mat grab, Lock/Realign button and TableAligner from earlier scenes.
    /// ScenePermission now lives on ForgeRig/Permissions (Setup_CameraPlacement, 530).
    /// All idempotent.
    /// </summary>
    public static class Setup_TableAlign
    {
        public const string TablePlaneLayer = "TablePlane";
        const string GeneratedDir = "Assets/ForgeXR/Quest/Generated/TableAlign";
        const string PlanePrefabPath = GeneratedDir + "/ForgeTablePlane.prefab";

        [ForgeSetupStep(300)]
        public static void EnsureTablePlaneLayer()
        {
            GetOrAddLayer(TablePlaneLayer);
        }

        [ForgeSetupStep(310)]
        public static void CreateAlignAssets()
        {
            EnsureFolder(GeneratedDir);
            var layer = GetOrAddLayer(TablePlaneLayer);

            // Detected-plane prefab: no renderer at all (AR-first), MeshCollider kept up to date by ARPlaneMeshVisualizer.
            var go = new GameObject("ForgeTablePlane");
            try
            {
                go.layer = layer;
                go.AddComponent<ARPlane>();
                go.AddComponent<MeshFilter>();
                go.AddComponent<MeshCollider>();
                go.AddComponent<ARPlaneMeshVisualizer>();
                PrefabUtility.SaveAsPrefabAsset(go, PlanePrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [ForgeSetupStep(320)]
        public static void AddARManagersToOrigin()
        {
            var origin = Object.FindAnyObjectByType<XROrigin>(FindObjectsInactive.Include);
            if (origin == null)
                throw new System.InvalidOperationException("No XROrigin in the scene");
            var originGo = origin.gameObject;

            var planes = ForgeSetupUtil.GetOrAdd<ARPlaneManager>(originGo);
            planes.planePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlanePrefabPath);
            planes.requestedDetectionMode = PlaneDetectionMode.Horizontal;
            planes.enabled = false; // ScenePermission enables it once USE_SCENE is granted
            EditorUtility.SetDirty(planes);

            var anchors = ForgeSetupUtil.GetOrAdd<ARAnchorManager>(originGo);
            anchors.enabled = true;
            EditorUtility.SetDirty(anchors);
        }

        /// <summary>
        /// Removes the retired mat alignment: GrabSurface/AlignOnly, the Lock/Realign PokeButton, the mat's
        /// XRGrabInteractable + Rigidbody + (deleted) MatPlanarGrabTransformer, and ForgeRig/TableAligner.
        /// Interactable colliders near the table also stopped XRI's far ray (NearFarInteractor near-cast), so none may remain.
        /// </summary>
        [ForgeSetupStep(340)]
        public static void RemoveMatAlignment()
        {
            var mat = ForgeSetupUtil.GetOrCreateStagingMat();
            var t = mat.transform;

            DestroyChild(t, "AlignOnly");    // contains GrabSurface (BoxCollider) and any stale Outline
            DestroyChild(t, "AlignButton");  // Lock/Realign PokeButton

            // XRGrabInteractable requires Rigidbody, so remove it first.
            foreach (var grab in mat.GetComponents<XRGrabInteractable>()) Object.DestroyImmediate(grab, true);
            foreach (var rb in mat.GetComponents<Rigidbody>()) Object.DestroyImmediate(rb, true);
            foreach (var c in mat.GetComponents<Collider>()) Object.DestroyImmediate(c, true);
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(mat);

            var rig = ForgeSetupUtil.GetOrCreateRoot(ForgeScene.RigRoot);
            DestroyChild(rig.transform, "TableAligner"); // held TableAligner + the old ScenePermission
            foreach (var a in Object.FindObjectsByType<TableAligner>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Object.DestroyImmediate(a, true);

            // No interactable colliders may be left on the mat or the spots (CueAnchor's buttons belong to ps/pinch-ui).
            foreach (var spot in mat.GetComponentsInChildren<PickupSpot>(true))
                foreach (var c in spot.GetComponentsInChildren<Collider>(true))
                    Object.DestroyImmediate(c, true);
        }

        static void DestroyChild(Transform parent, string name)
        {
            var c = parent.Find(name);
            if (c != null) Object.DestroyImmediate(c.gameObject);
        }

        // ---- helpers ----

        /// <summary>Returns the index of the named user layer, adding it to the first free slot (8-31) if missing.</summary>
        static int GetOrAddLayer(string name)
        {
            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layers = tagManager.FindProperty("layers");
            var free = -1;
            for (var i = 8; i < 32 && i < layers.arraySize; i++)
            {
                var value = layers.GetArrayElementAtIndex(i).stringValue;
                if (value == name) return i;
                if (free < 0 && string.IsNullOrEmpty(value)) free = i;
            }
            if (free < 0)
                throw new System.InvalidOperationException($"No free user layer for '{name}'");
            layers.GetArrayElementAtIndex(free).stringValue = name;
            tagManager.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
            Debug.Log($"[ForgeSetup] Added layer '{name}' at index {free}");
            return free;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
