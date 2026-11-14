using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.EditorTools
{
    /// <summary>
    /// Chunk 4 setup steps (400-499): NextInput, the CueAnchor where Harley's instruction UI is
    /// parented, the virtual Next/Reset poke buttons, and the temporary MilestoneSequence harness.
    /// All steps are find-or-create by name and safe to run twice.
    ///
    /// CueAnchor frame (child of StagingMat): 12 cm behind the spot row (+Z), 10 cm above the table,
    /// tilted 30 degrees back so its front (-Z side) faces up toward the builder's eyes.
    /// </summary>
    public static class Setup_NextInput
    {
        public const string NextInputName = "NextInput";
        public const string CueAnchorName = "CueAnchor";
        public const string CuePlaceholderName = "CuePlaceholder";
        public const string NextButtonName = "NextButton";
        public const string ResetButtonName = "ResetButton";
        public const string MilestoneName = "MilestoneSequence";

        static readonly Vector3 CueAnchorPos = new(0f, 0.10f, 0.12f);
        static readonly Vector3 CueAnchorEuler = new(30f, 0f, 0f);

        // Placeholder panel, metres. Harley's UI replaces it.
        static readonly Vector2 PanelSize = new(0.24f, 0.10f);

        // Next: prominent, right of the panel. Reset: small, left of the panel and bottom-aligned.
        static readonly Vector2 NextSize = new(0.08f, 0.045f);
        static readonly Vector3 NextPos = new(PanelSize.x / 2f + 0.015f + NextSize.x / 2f, 0f, 0f);
        static readonly Vector2 ResetSize = new(0.04f, 0.025f);
        static readonly Vector3 ResetPos = new(-(PanelSize.x / 2f + 0.015f + ResetSize.x / 2f),
            -(PanelSize.y - ResetSize.y) / 2f, 0f);

        const float CanvasUnitsPerMetre = 1000f;

        [ForgeSetupStep(400)]
        public static void CreateNextInput()
        {
            var rig = ForgeSetupUtil.GetOrCreateRoot(ForgeScene.RigRoot);
            var go = ForgeSetupUtil.GetOrCreateChild(rig.transform, NextInputName);
            ForgeSetupUtil.GetOrAdd<NextInput>(go);
        }

        [ForgeSetupStep(410)]
        public static void CreateCueAnchor()
        {
            var anchor = GetCueAnchor();

            var panel = ForgeSetupUtil.GetOrCreateChild(anchor, CuePlaceholderName);
            if (!panel.TryGetComponent<RectTransform>(out var rt)) rt = panel.AddComponent<RectTransform>();
            var canvas = ForgeSetupUtil.GetOrAdd<Canvas>(panel);
            canvas.renderMode = RenderMode.WorldSpace;
            rt.localPosition = Vector3.zero;
            rt.localRotation = Quaternion.identity;
            rt.localScale = Vector3.one / CanvasUnitsPerMetre;
            rt.sizeDelta = PanelSize * CanvasUnitsPerMetre;

            var bg = ForgeSetupUtil.GetOrAdd<Image>(panel);
            bg.color = new Color(0.05f, 0.06f, 0.08f, 0.8f);
            bg.raycastTarget = false;

            var textGo = ForgeSetupUtil.GetOrCreateChild(panel.transform, "Text");
            if (!textGo.TryGetComponent<RectTransform>(out var trt)) trt = textGo.AddComponent<RectTransform>();
            var tmp = ForgeSetupUtil.GetOrAdd<TextMeshProUGUI>(textGo);
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;
            trt.localPosition = new Vector3(trt.localPosition.x, trt.localPosition.y, -1f);
            tmp.text = "Step 1 of 2\nPress the torso onto the legs with the shirt print facing the toes.";
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 8f;   // canvas units are mm: 8-14 mm em is ~0.8-1.3 deg at 0.6 m
            tmp.fontSizeMax = 14f;
            tmp.margin = new Vector4(8f, 6f, 8f, 6f);
            tmp.raycastTarget = false;
        }

        [ForgeSetupStep(415)]
        public static void CreateButtons()
        {
            var anchor = GetCueAnchor();

            var next = CreateButton(anchor, NextButtonName, "Next", NextSize, NextPos);
            SetColor(next, "faceColor", new Color(0.15f, 0.40f, 0.90f, 0.95f));
            SetColor(next, "hoverColor", new Color(0.35f, 0.60f, 1f, 1f));

            var reset = CreateButton(anchor, ResetButtonName, "Reset", ResetSize, ResetPos);

            var input = GetNextInput();
            var so = new SerializedObject(input);
            so.FindProperty("nextButton").objectReferenceValue = next;
            so.FindProperty("resetButton").objectReferenceValue = reset;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // TEMPORARY: delete with MilestoneSequence when Harley's walkthrough lands.
        [ForgeSetupStep(420)]
        public static void CreateMilestoneSequence()
        {
            var rig = ForgeSetupUtil.GetOrCreateRoot(ForgeScene.RigRoot);
            var go = ForgeSetupUtil.GetOrCreateChild(rig.transform, MilestoneName);
            var seq = ForgeSetupUtil.GetOrAdd<MilestoneSequence>(go);
            var text = GetCueAnchor().Find(CuePlaceholderName + "/Text")?.GetComponent<TMP_Text>();
            seq.Configure(GetNextInput(), text);
            EditorUtility.SetDirty(seq);
        }

        static Transform GetCueAnchor()
        {
            var mat = ForgeSetupUtil.GetOrCreateStagingMat();
            var anchor = ForgeSetupUtil.GetOrCreateChild(mat.transform, CueAnchorName).transform;
            anchor.localPosition = CueAnchorPos;
            anchor.localRotation = Quaternion.Euler(CueAnchorEuler);
            anchor.localScale = Vector3.one;
            return anchor;
        }

        static NextInput GetNextInput()
        {
            CreateNextInput();
            var rig = ForgeSetupUtil.GetOrCreateRoot(ForgeScene.RigRoot);
            return rig.transform.Find(NextInputName).GetComponent<NextInput>();
        }

        static PokeButton CreateButton(Transform parent, string name, string label, Vector2 size, Vector3 localPos)
        {
            var go = ForgeSetupUtil.GetOrCreateChild(parent, name);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            var button = ForgeSetupUtil.GetOrAdd<PokeButton>(go);
            button.Configure(label, size);
            EditorUtility.SetDirty(button);
            return button;
        }

        static void SetColor(Object target, string field, Color c)
        {
            var so = new SerializedObject(target);
            so.FindProperty(field).colorValue = c;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
