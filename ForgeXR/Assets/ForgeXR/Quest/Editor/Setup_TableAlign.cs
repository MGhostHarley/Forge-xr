using System.IO;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Attachment;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

namespace Forge.EditorTools
{
    /// <summary>
    /// Chunk 3 (table alignment) setup steps, orders 300-399. All idempotent (find-or-create by name).
    /// </summary>
    public static class Setup_TableAlign
    {
        public const string TablePlaneLayer = "TablePlane";
        const string GeneratedDir = "Assets/ForgeXR/Quest/Generated/TableAlign";
        const string LineMaterialPath = GeneratedDir + "/ForgeAlignLine.mat";
        const string PlanePrefabPath = GeneratedDir + "/ForgeTablePlane.prefab";

        // Mat footprint in the mat frame (x = builder's left-right, z = depth). The four 6 cm spots span 18 cm in x.
        // Matches chunk 2's MatFrame (Setup_PickupSpots MatWidth x MatDepth), which is the only mat outline.
        static readonly Vector2 MatFootprint = new(0.32f, 0.16f);
        static readonly Color LineColor = new(0.2f, 0.85f, 1f, 1f);

        const string AlignOnlyName = "AlignOnly";
        const string GrabSurfaceName = "GrabSurface";
        const string OutlineName = "Outline";
        const string AlignButtonName = "AlignButton";
        const string AlignerName = "TableAligner";

        // Lock/Realign button just outside the mat's left edge, facing the wearer (who aligns from the
        // builder's -Z side), tilted like CueAnchor. Kept clear of the cue panel and Next/Reset buttons.
        // PokeButton's readable face is seen looking along its +Z; the poke goes toward +Z.
        static readonly Vector3 AlignButtonLocalPosition = new(-0.21f, 0.02f, 0f);
        static readonly Vector3 AlignButtonLocalEuler = new(30f, 0f, 0f);
        static readonly Vector2 AlignButtonSize = new(0.07f, 0.035f);

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
            var lineMat = GetOrCreateLineMaterial();

            // Detected-plane prefab: invisible fill, collider for the height probe, thin boundary line while aligning.
            var go = new GameObject("ForgeTablePlane");
            try
            {
                go.layer = layer;
                go.AddComponent<ARPlane>();
                go.AddComponent<MeshFilter>();
                go.AddComponent<MeshCollider>();
                var line = go.AddComponent<LineRenderer>();
                line.useWorldSpace = false;
                line.loop = true;
                line.widthMultiplier = 0.004f;
                line.positionCount = 0;
                line.sharedMaterial = lineMat;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
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

        [ForgeSetupStep(340)]
        public static void SetupMatAndAligner()
        {
            var lineMat = GetOrCreateLineMaterial();
            var matGo = ForgeSetupUtil.GetOrCreateStagingMat();
            var mat = matGo.transform;

            // Align-only visuals: grab surface + outline. Hidden by TableAligner once locked.
            var alignOnly = ForgeSetupUtil.GetOrCreateChild(mat, AlignOnlyName);
            alignOnly.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);

            var surface = ForgeSetupUtil.GetOrCreateChild(alignOnly.transform, GrabSurfaceName);
            surface.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            var box = ForgeSetupUtil.GetOrAdd<BoxCollider>(surface);
            box.size = new Vector3(MatFootprint.x, 0.02f, MatFootprint.y);
            box.center = new Vector3(0f, 0.01f, 0f);
            box.isTrigger = false;

            // No outline here: chunk 2's MatFrame/MatFill/Arrow (StagingMatVisuals) is the single mat outline
            // and already hides itself once TableAligner locks. Remove a stale one from earlier setup runs.
            var staleOutline = alignOnly.transform.Find(OutlineName);
            if (staleOutline != null) Object.DestroyImmediate(staleOutline.gameObject);

            // Grab: XRGrabInteractable on the mat itself, limited to the grab surface collider,
            // with only the planar transformer (horizontal slide + yaw, table height).
            var rb = ForgeSetupUtil.GetOrAdd<Rigidbody>(matGo);
            rb.isKinematic = true;
            rb.useGravity = false;

            var planar = ForgeSetupUtil.GetOrAdd<MatPlanarGrabTransformer>(matGo);
            var grab = ForgeSetupUtil.GetOrAdd<XRGrabInteractable>(matGo);
            grab.selectMode = InteractableSelectMode.Single;
            grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
            grab.trackScale = false;
            grab.throwOnDetach = false;
            grab.useDynamicAttach = true;
            grab.farAttachMode = InteractableFarAttachMode.Far; // far grab keeps its distance, no pull to hand
            grab.unparentTransformOnGrab = false;
            grab.retainTransformParent = true;
            grab.smoothPosition = false;
            grab.smoothRotation = false;
            grab.addDefaultGrabTransformers = false;
            grab.startingSingleGrabTransformers.Clear();
            grab.startingSingleGrabTransformers.Add(planar);
            grab.colliders.Clear(); // otherwise Awake gathers every child collider (spots, buttons)
            grab.colliders.Add(box);
            EditorUtility.SetDirty(grab);

            // Lock / Realign poke button (chunk 4 implements PokeButton).
            var buttonGo = ForgeSetupUtil.GetOrCreateChild(mat, AlignButtonName);
            buttonGo.transform.SetLocalPositionAndRotation(AlignButtonLocalPosition, Quaternion.Euler(AlignButtonLocalEuler));
            var button = ForgeSetupUtil.GetOrAdd<PokeButton>(buttonGo);
            button.Configure("Lock", AlignButtonSize);
            EditorUtility.SetDirty(button);

            // Aligner + permission under ForgeRig.
            var rig = ForgeSetupUtil.GetOrCreateRoot(ForgeScene.RigRoot);
            var alignerGo = ForgeSetupUtil.GetOrCreateChild(rig.transform, AlignerName);
            var aligner = ForgeSetupUtil.GetOrAdd<TableAligner>(alignerGo);
            var permission = ForgeSetupUtil.GetOrAdd<ScenePermission>(alignerGo);

            var origin = Object.FindAnyObjectByType<XROrigin>(FindObjectsInactive.Include);
            var anchorManager = origin != null ? origin.GetComponent<ARAnchorManager>() : null;
            var planeManager = origin != null ? origin.GetComponent<ARPlaneManager>() : null;

            aligner.SetMat(mat);
            var so = new SerializedObject(aligner);
            so.FindProperty("anchorManager").objectReferenceValue = anchorManager;
            so.FindProperty("matGrab").objectReferenceValue = grab;
            so.FindProperty("planarTransformer").objectReferenceValue = planar;
            so.FindProperty("alignButton").objectReferenceValue = button;
            so.FindProperty("alignOnlyVisuals").objectReferenceValue = alignOnly;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(aligner);

            var pso = new SerializedObject(permission);
            pso.FindProperty("planeManager").objectReferenceValue = planeManager;
            pso.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(permission);
        }

        // ---- helpers ----

        static Material GetOrCreateLineMaterial()
        {
            EnsureFolder(GeneratedDir);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(LineMaterialPath);
            if (mat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null)
                    throw new System.InvalidOperationException("URP Unlit shader not found");
                mat = new Material(shader) { name = "ForgeAlignLine" };
                AssetDatabase.CreateAsset(mat, LineMaterialPath);
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", LineColor);
            mat.color = LineColor;
            EditorUtility.SetDirty(mat);
            return mat;
        }

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
