using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.EditorTools
{
    /// <summary>
    /// Chunk 4 setup steps (400-499): NextInput, the FloatingCue (instruction line + pinch Next/Reset
    /// buttons that float above the highlighted piece).
    /// All steps are find-or-create by name and safe to run twice.
    ///
    /// FloatingCue local frame (FloatingCue positions and yaws it at runtime; the viewer is on its -Z side):
    ///   [Reset]  [ ------ instruction 22 x 4.5 cm ------ ]  [ Next ]
    /// It lives directly under ForgeRig (not under StagingMat, which is now an identity-pose container).
    /// </summary>
    public static class Setup_NextInput
    {
        public const string NextInputName = "NextInput";
        public const string FloatingCueName = "FloatingCue";
        public const string InstructionName = "Instruction";
        public const string NextButtonName = "NextButton";
        public const string ResetButtonName = "ResetButton";
        public const string MilestoneName = "MilestoneSequence";

        /// <summary>Old chunk 4 objects, removed by order 405.</summary>
        const string OldCueAnchorName = "CueAnchor";
        const string OldCuePlaceholderName = "CuePlaceholder";

        // Instruction panel, metres.
        static readonly Vector2 PanelSize = new(0.22f, 0.045f);
        const float Gap = 0.005f;

        // Next: prominent, right of the panel. Reset: small, left of the panel, slightly lower.
        static readonly Vector2 NextSize = new(0.07f, 0.035f);
        static readonly Vector3 NextPos = new(PanelSize.x / 2f + Gap + NextSize.x / 2f, 0f, 0f);
        static readonly Vector2 ResetSize = new(0.045f, 0.025f);
        static readonly Vector3 ResetPos = new(-(PanelSize.x / 2f + Gap + ResetSize.x / 2f), -0.01f, 0f);

        // Placeholder pose before FloatingCue places itself at runtime (~table height, 50 cm ahead).
        static readonly Vector3 DefaultCuePos = new(0f, 0.9f, 0.5f);

        const float CanvasUnitsPerMetre = 1000f;

        [ForgeSetupStep(400)]
        public static void CreateNextInput()
        {
            var rig = ForgeSetupUtil.GetOrCreateRoot(ForgeScene.RigRoot);
            var go = ForgeSetupUtil.GetOrCreateChild(rig.transform, NextInputName);
            ForgeSetupUtil.GetOrAdd<NextInput>(go);
        }

        /// <summary>Removes the old mat-mounted CueAnchor (with CuePlaceholder and the old Next/Reset buttons).</summary>
        [ForgeSetupStep(405)]
        public static void RemoveOldCue()
        {
            var rig = ForgeSetupUtil.GetOrCreateRoot(ForgeScene.RigRoot);
            var doomed = new List<GameObject>();
            foreach (var t in rig.GetComponentsInChildren<Transform>(true))
                if (t.name == OldCueAnchorName || t.name == OldCuePlaceholderName) doomed.Add(t.gameObject);
            foreach (var go in doomed)
                if (go != null) Object.DestroyImmediate(go);
            if (doomed.Count > 0) Debug.Log($"[ForgeSetup] Removed {doomed.Count} old cue object(s)");
        }

        [ForgeSetupStep(410)]
        public static void CreateFloatingCue()
        {
            var cueGo = GetCueRoot(out var created);
            if (created)
            {
                cueGo.transform.SetPositionAndRotation(DefaultCuePos, Quaternion.identity);
                cueGo.transform.localScale = Vector3.one;
            }
            var cue = ForgeSetupUtil.GetOrAdd<FloatingCue>(cueGo);

            var panel = ForgeSetupUtil.GetOrCreateChild(cueGo.transform, InstructionName);
            if (!panel.TryGetComponent<RectTransform>(out var rt)) rt = panel.AddComponent<RectTransform>();
            var canvas = ForgeSetupUtil.GetOrAdd<Canvas>(panel);
            canvas.renderMode = RenderMode.WorldSpace;
            rt.localPosition = Vector3.zero;
            rt.localRotation = Quaternion.identity;
            rt.localScale = Vector3.one / CanvasUnitsPerMetre;
            rt.sizeDelta = PanelSize * CanvasUnitsPerMetre;

            var bg = ForgeSetupUtil.GetOrAdd<Image>(panel);
            bg.color = new Color(0.05f, 0.06f, 0.08f, 0.75f);
            bg.raycastTarget = false;

            var textGo = ForgeSetupUtil.GetOrCreateChild(panel.transform, "Text");
            if (!textGo.TryGetComponent<RectTransform>(out var trt)) trt = textGo.AddComponent<RectTransform>();
            var tmp = ForgeSetupUtil.GetOrAdd<TextMeshProUGUI>(textGo);
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;
            trt.localPosition = new Vector3(trt.localPosition.x, trt.localPosition.y, -1f);
            tmp.text = "Press the torso onto the legs, print toward the toes.";
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 9f;   // canvas units are mm: 9-14 mm em is ~1-1.5 deg at 0.5 m
            tmp.fontSizeMax = 14f;
            tmp.margin = new Vector4(6f, 3f, 6f, 3f);
            tmp.raycastTarget = false;

            cue.Configure(tmp, cue.NextButton, cue.ResetButton);
            EditorUtility.SetDirty(cue);
        }

        [ForgeSetupStep(415)]
        public static void CreateButtons()
        {
            var cue = GetCue();
            var root = cue.transform;

            var next = CreateButton(root, NextButtonName, "Next", NextSize, NextPos);
            SetColor(next, "faceColor", new Color(0.15f, 0.40f, 0.90f, 0.95f));
            SetColor(next, "hoverColor", new Color(0.35f, 0.60f, 1f, 1f));

            var reset = CreateButton(root, ResetButtonName, "Reset", ResetSize, ResetPos);

            cue.Configure(cue.Text, next, reset);
            EditorUtility.SetDirty(cue);

            var input = GetNextInput();
            var so = new SerializedObject(input);
            so.FindProperty("nextButton").objectReferenceValue = next;
            so.FindProperty("resetButton").objectReferenceValue = reset;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static GameObject GetCueRoot(out bool created)
        {
            var rig = ForgeSetupUtil.GetOrCreateRoot(ForgeScene.RigRoot);
            created = rig.transform.Find(FloatingCueName) == null;
            return ForgeSetupUtil.GetOrCreateChild(rig.transform, FloatingCueName);
        }

        static FloatingCue GetCue()
        {
            var go = GetCueRoot(out _);
            if (!go.TryGetComponent<FloatingCue>(out var cue) || cue.Text == null)
            {
                CreateFloatingCue();
                cue = go.GetComponent<FloatingCue>();
            }
            return cue;
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
