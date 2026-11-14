using System;
using System.Collections.Generic;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Forge.EditorTools
{
    /// <summary>
    /// Chunk 2 setup (orders 200-299): generated materials/meshes, StagingMat start pose,
    /// mat outline + arrow, four PickupSpots with labels, and the checkpoint default highlight.
    /// Idempotent: assets are load-or-create and updated in place; scene objects are find-or-create by name.
    /// Materials are .mat assets referenced by the scene, so the URP Unlit shader and its transparent
    /// variant survive Android/IL2CPP builds (no runtime Shader.Find).
    /// </summary>
    public static class Setup_PickupSpots
    {
        const string GenFolder = "Assets/ForgeXR/Quest/Generated";
        const string SpotMatPath = GenFolder + "/ForgeSpot.mat";
        const string MatMatPath = GenFolder + "/ForgeMat.mat";
        const string RingMeshPath = GenFolder + "/ForgeRing.asset";
        const string DiscMeshPath = GenFolder + "/ForgeDisc.asset";
        const string StemMeshPath = GenFolder + "/ForgeStem.asset";
        const string FrameMeshPath = GenFolder + "/ForgeMatFrame.asset";
        const string FillMeshPath = GenFolder + "/ForgeMatFill.asset";
        const string ArrowMeshPath = GenFolder + "/ForgeArrow.asset";

        const string FontPath = "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset";
        const string FontOutlineMatPath = "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF - Outline.mat";

        // Dimensions in metres (mat-local frame, see ForgeScene).
        const float RingOuter = 0.018f, RingInner = 0.0145f;
        const int RingSegments = 64;
        const float RingY = 0.002f, GlowY = 0.0015f, StemBottom = 0.004f;
        const float StemWidth = 0.0015f;
        const float LabelLow = 0.08f, LabelHigh = 0.105f; // staggered by index parity to avoid overlap
        const float LabelHalfHeight = 0.012f;              // stem stops just below the text
        const float LabelFontSize = 0.15f;                 // TMP 3D: ~1.5 cm em
        const float MatWidth = 0.32f, MatDepth = 0.16f, FrameLine = 0.003f;
        const float ArrowTipZ = -0.072f, ArrowBaseZ = -0.052f, ArrowHalfWidth = 0.015f;

        static readonly Dictionary<string, string> Labels = new()
        {
            { PartIds.Legs, "Legs" },
            { PartIds.Torso, "Torso" },
            { PartIds.Head, "Head" },
            { PartIds.Hair, "Hairpiece" },
        };

        [ForgeSetupStep(200)]
        public static void CreateAssets()
        {
            EnsureFolder(GenFolder);

            GetOrCreateTransparentMaterial(SpotMatPath, new Color(1f, 1f, 1f, 0.6f), (int)RenderQueue.Transparent + 10);
            GetOrCreateTransparentMaterial(MatMatPath, new Color(1f, 1f, 1f, 0.3f), (int)RenderQueue.Transparent - 10);

            GetOrCreateMesh(RingMeshPath, m => BuildRing(m, RingInner, RingOuter, RingSegments));
            GetOrCreateMesh(DiscMeshPath, m => BuildRing(m, 0f, RingInner, RingSegments));
            GetOrCreateMesh(StemMeshPath, m => BuildBox(m, StemWidth, 1f, StemWidth));
            GetOrCreateMesh(FrameMeshPath, m => BuildFrame(m, MatWidth, MatDepth, FrameLine));
            GetOrCreateMesh(FillMeshPath, m => BuildFrame(m, MatWidth, MatDepth, 0f));
            GetOrCreateMesh(ArrowMeshPath, BuildArrow);

            AssetDatabase.SaveAssets();
        }

        [ForgeSetupStep(210)]
        public static void PlaceStagingMat()
        {
            var mat = ForgeSetupUtil.GetOrCreateStagingMat();

            // Fixed start pose relative to the XR Origin (which sits off-centre and yawed in SampleScene).
            // The origin is floor level (runtime default Stage space), so y = 0.75 m is table height.
            // MatStartPlacer re-places it in front of the head at runtime; chunk 3 then aligns it.
            var origin = UnityEngine.Object.FindAnyObjectByType<XROrigin>(FindObjectsInactive.Include);
            var originT = origin != null ? (origin.Origin != null ? origin.Origin.transform : origin.transform) : null;
            if (originT != null)
            {
                var fwd = Vector3.ProjectOnPlane(originT.forward, Vector3.up);
                if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
                fwd.Normalize();
                var pos = originT.position + fwd * 0.5f + Vector3.up * 0.75f;
                mat.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(fwd, Vector3.up));
            }
            else
            {
                Debug.LogWarning("[ForgeSetup] No XROrigin found; placing StagingMat at world (0, 0.75, 0.5)");
                mat.transform.SetPositionAndRotation(new Vector3(0f, 0.75f, 0.5f), Quaternion.identity);
            }
            mat.transform.localScale = Vector3.one;

            var matMaterial = AssetDatabase.LoadAssetAtPath<Material>(MatMatPath);
            var fill = MeshChild(mat.transform, "MatFill", FillMeshPath, matMaterial, new Vector3(0f, 0f, 0f));
            var frame = MeshChild(mat.transform, "MatFrame", FrameMeshPath, matMaterial, new Vector3(0f, 0.0005f, 0f));
            var arrow = MeshChild(mat.transform, "Arrow", ArrowMeshPath, matMaterial, new Vector3(0f, 0.0005f, 0f));

            ForgeSetupUtil.GetOrAdd<MatStartPlacer>(mat);

            var visuals = ForgeSetupUtil.GetOrAdd<StagingMatVisuals>(mat);
            var so = new SerializedObject(visuals);
            so.FindProperty("frame").objectReferenceValue = frame;
            so.FindProperty("fill").objectReferenceValue = fill;
            so.FindProperty("arrow").objectReferenceValue = arrow;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        [ForgeSetupStep(220)]
        public static void CreateSpots()
        {
            var mat = ForgeSetupUtil.GetOrCreateStagingMat().transform;
            var spotMaterial = AssetDatabase.LoadAssetAtPath<Material>(SpotMatPath);
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
            if (font == null) font = TMP_Settings.defaultFontAsset;
            var fontMat = AssetDatabase.LoadAssetAtPath<Material>(FontOutlineMatPath);

            var ids = PartIds.DriverStaging;
            for (var i = 0; i < ids.Length; i++)
            {
                var id = ids[i];
                var label = Labels.TryGetValue(id, out var l) ? l : id;
                var spotGo = ForgeSetupUtil.GetOrCreateChild(mat, ForgeScene.SpotPrefix + id);
                spotGo.transform.localPosition = ForgeScene.DefaultSpotLocalPosition(i, ids.Length);
                spotGo.transform.localRotation = Quaternion.identity;
                spotGo.transform.localScale = Vector3.one;

                var labelHeight = i % 2 == 0 ? LabelLow : LabelHigh;

                var ring = MeshChild(spotGo.transform, "Ring", RingMeshPath, spotMaterial, new Vector3(0f, RingY, 0f));
                var glow = MeshChild(spotGo.transform, "Glow", DiscMeshPath, spotMaterial, new Vector3(0f, GlowY, 0f));
                var stem = MeshChild(spotGo.transform, "Stem", StemMeshPath, spotMaterial, new Vector3(0f, StemBottom, 0f));
                stem.transform.localScale = new Vector3(1f, labelHeight - LabelHalfHeight - StemBottom, 1f);
                glow.enabled = false;

                var labelGo = ForgeSetupUtil.GetOrCreateChild(spotGo.transform, "Label");
                var tmp = ForgeSetupUtil.GetOrAdd<TextMeshPro>(labelGo);
                labelGo.transform.localPosition = new Vector3(0f, labelHeight, 0f);
                labelGo.transform.localRotation = Quaternion.identity;
                labelGo.transform.localScale = Vector3.one;
                if (font != null) tmp.font = font;
                if (fontMat != null) tmp.fontSharedMaterial = fontMat;
                tmp.text = label;
                tmp.fontSize = LabelFontSize;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.textWrappingMode = TextWrappingModes.NoWrap;
                tmp.color = Color.white;
                tmp.rectTransform.sizeDelta = new Vector2(0.14f, 0.03f);
                var labelRenderer = labelGo.GetComponent<MeshRenderer>();
                if (labelRenderer != null) NoShadows(labelRenderer);

                var spot = ForgeSetupUtil.GetOrAdd<PickupSpot>(spotGo);
                spot.Configure(id, label);
                var so = new SerializedObject(spot);
                so.FindProperty("ring").objectReferenceValue = ring;
                so.FindProperty("glow").objectReferenceValue = glow;
                so.FindProperty("stem").objectReferenceValue = stem;
                so.FindProperty("labelRoot").objectReferenceValue = labelGo.transform;
                so.FindProperty("labelText").objectReferenceValue = tmp;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(spot);
            }
        }

        [ForgeSetupStep(230)]
        public static void AddDefaultHighlight()
        {
            var rig = ForgeSetupUtil.GetOrCreateRoot(ForgeScene.RigRoot);
            ForgeSetupUtil.GetOrAdd<DefaultHighlight>(rig);
        }

        // ---------- scene helpers ----------

        static MeshRenderer MeshChild(Transform parent, string name, string meshPath, Material material, Vector3 localPos)
        {
            var go = ForgeSetupUtil.GetOrCreateChild(parent, name);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            ForgeSetupUtil.GetOrAdd<MeshFilter>(go).sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            var r = ForgeSetupUtil.GetOrAdd<MeshRenderer>(go);
            r.sharedMaterial = material;
            r.enabled = true;
            NoShadows(r);
            // No colliders: spots and mat must never block hand poke / pinch-grab.
            foreach (var c in go.GetComponents<Collider>()) UnityEngine.Object.DestroyImmediate(c);
            return r;
        }

        static void NoShadows(Renderer r)
        {
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        }

        // ---------- asset helpers ----------

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var slash = path.LastIndexOf('/');
            var parent = path.Substring(0, slash);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, path.Substring(slash + 1));
        }

        static Material GetOrCreateTransparentMaterial(string path, Color color, int renderQueue)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) throw new InvalidOperationException("URP Unlit shader not found");

            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            var created = mat == null;
            if (created) mat = new Material(shader);
            else if (mat.shader != shader) mat.shader = shader;

            mat.SetFloat("_Surface", 1f); // Transparent
            mat.SetFloat("_Blend", 0f);   // Alpha
            mat.SetFloat("_Cull", 0f);    // Off: flat geometry seen from either side
            mat.SetFloat("_AlphaClip", 0f);
            mat.SetColor("_BaseColor", color);
            BaseShaderGUI.SetupMaterialBlendMode(mat); // keywords, blend factors, RenderType
            mat.SetFloat("_ZWrite", 0f);
            mat.renderQueue = renderQueue;
            mat.enableInstancing = false;

            if (created) AssetDatabase.CreateAsset(mat, path);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Mesh GetOrCreateMesh(string path, Action<Mesh> build)
        {
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            var created = mesh == null;
            if (created) mesh = new Mesh { name = System.IO.Path.GetFileNameWithoutExtension(path) };
            else mesh.Clear();
            build(mesh);
            mesh.RecalculateBounds();
            if (created) AssetDatabase.CreateAsset(mesh, path);
            EditorUtility.SetDirty(mesh);
            return mesh;
        }

        // ---------- mesh builders (XZ plane, +Y up; materials are Cull Off) ----------

        /// <summary>Flat annulus; inner = 0 gives a disc.</summary>
        static void BuildRing(Mesh m, float inner, float outer, int segments)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            for (var i = 0; i <= segments; i++)
            {
                var a = i * Mathf.PI * 2f / segments;
                var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                verts.Add(d * inner);
                verts.Add(d * outer);
            }
            for (var i = 0; i < segments; i++)
            {
                var b = i * 2;
                tris.AddRange(new[] { b, b + 2, b + 1, b + 1, b + 2, b + 3 });
            }
            Finish(m, verts, tris);
        }

        /// <summary>Box from y = 0 to y = height, centred in XZ (scale Y to set the length).</summary>
        static void BuildBox(Mesh m, float w, float height, float d)
        {
            float x = w * 0.5f, z = d * 0.5f;
            var verts = new List<Vector3>
            {
                new(-x, 0, -z), new(x, 0, -z), new(x, 0, z), new(-x, 0, z),
                new(-x, height, -z), new(x, height, -z), new(x, height, z), new(-x, height, z),
            };
            var tris = new List<int>
            {
                0, 4, 5, 0, 5, 1, // -Z
                1, 5, 6, 1, 6, 2, // +X
                2, 6, 7, 2, 7, 3, // +Z
                3, 7, 4, 3, 4, 0, // -X
                4, 7, 6, 4, 6, 5, // top
            };
            Finish(m, verts, tris);
        }

        /// <summary>Rectangle outline of the given line width; line = 0 gives a filled rectangle.</summary>
        static void BuildFrame(Mesh m, float width, float depth, float line)
        {
            float x = width * 0.5f, z = depth * 0.5f;
            if (line <= 0f)
            {
                Finish(m, new List<Vector3> { new(-x, 0, -z), new(-x, 0, z), new(x, 0, z), new(x, 0, -z) },
                    new List<int> { 0, 1, 2, 0, 2, 3 });
                return;
            }
            float ix = x - line, iz = z - line;
            var verts = new List<Vector3>
            {
                new(-x, 0, -z), new(-x, 0, z), new(x, 0, z), new(x, 0, -z),       // outer 0-3
                new(-ix, 0, -iz), new(-ix, 0, iz), new(ix, 0, iz), new(ix, 0, -iz), // inner 4-7
            };
            var tris = new List<int>();
            for (var i = 0; i < 4; i++)
            {
                int o0 = i, o1 = (i + 1) % 4, i0 = i + 4, i1 = (i + 1) % 4 + 4;
                tris.AddRange(new[] { o0, o1, i1, o0, i1, i0 });
            }
            Finish(m, verts, tris);
        }

        /// <summary>Chevron near the mat's -Z edge pointing -Z, toward the builder.</summary>
        static void BuildArrow(Mesh m)
        {
            const float thickness = 0.006f;
            var verts = new List<Vector3>
            {
                new(0, 0, ArrowTipZ),                               // 0 tip (outer)
                new(-ArrowHalfWidth, 0, ArrowBaseZ),                // 1 left outer
                new(-ArrowHalfWidth, 0, ArrowBaseZ + thickness),    // 2 left inner
                new(0, 0, ArrowTipZ + thickness * 1.4f),            // 3 tip (inner)
                new(ArrowHalfWidth, 0, ArrowBaseZ + thickness),     // 4 right inner
                new(ArrowHalfWidth, 0, ArrowBaseZ),                 // 5 right outer
            };
            var tris = new List<int> { 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5 };
            Finish(m, verts, tris);
        }

        static void Finish(Mesh m, List<Vector3> verts, List<int> tris)
        {
            m.SetVertices(verts);
            m.SetTriangles(tris, 0);
            var normals = new Vector3[verts.Count];
            for (var i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
            m.SetNormals(normals);
        }
    }
}
