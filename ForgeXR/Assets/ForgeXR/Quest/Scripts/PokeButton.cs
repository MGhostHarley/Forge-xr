using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Filtering;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace Forge
{
    /// <summary>
    /// A world-space button pressed by poking it with an index fingertip (XRI poke on the Hands rig).
    /// Used for Next, Reset (chunk 4) and Lock / Realign (chunk 3).
    /// Other chunks create buttons via AddComponent + Configure and subscribe to onPressed.
    ///
    /// Layout (local frame): the builder is on the -Z side and pokes toward +Z (PokeAxis.Z).
    /// The BoxCollider spans z = -travel..+travel; the face sits at z = -travel and sinks to z = 0
    /// as the fingertip pushes in. Select fires when the fingertip reaches z = 0.
    /// The face is a tiny world-space uGUI canvas (Image + TMP) so no material asset is needed.
    /// The canvas has no GraphicRaycaster, so only the XRI poke path presses it.
    /// Only poke interactors (fingertips, controller tips) press it; ray/grip selects are ignored.
    /// </summary>
    [RequireComponent(typeof(BoxCollider), typeof(XRSimpleInteractable), typeof(XRPokeFilter))]
    public class PokeButton : MonoBehaviour
    {
        [SerializeField] string label = "Next";
        [Tooltip("Face size in metres (width, height).")]
        [SerializeField] Vector2 size = new(0.06f, 0.03f);

        public UnityEvent onPressed = new();

        public string Label => label;
        public Vector2 Size => size;

        [Header("Look")]
        [SerializeField] Color faceColor = new(0.12f, 0.14f, 0.18f, 0.9f);
        [SerializeField] Color hoverColor = new(0.25f, 0.45f, 0.85f, 0.95f);
        [SerializeField] Color pressedColor = new(0.2f, 0.75f, 0.35f, 1f);
        [SerializeField] Color labelColor = Color.white;
        [Tooltip("Push travel in metres before the press registers.")]
        [SerializeField] float travel = 0.012f;
        [Tooltip("Seconds after a press during which further presses are ignored.")]
        [SerializeField] float cooldownSeconds = 0.6f;

        const string FaceName = "Face";
        const string LabelName = "Label";
        const float CanvasUnitsPerMetre = 1000f; // canvas units are millimetres

        RectTransform face;
        Image faceImage;
        TextMeshProUGUI labelText;
        BoxCollider box;
        XRSimpleInteractable interactable;
        XRPokeFilter pokeFilter;
        float lastPressedAt = float.NegativeInfinity;
        float pressedFlashUntil;

        /// <summary>Used by setup steps after AddComponent.</summary>
        public void Configure(string displayLabel, Vector2 faceSize)
        {
            label = displayLabel;
            size = faceSize;
            OnConfigured();
        }

        public void SetLabel(string displayLabel)
        {
            label = displayLabel;
            OnConfigured();
        }

        /// <summary>Raises onPressed. Call this from the poke handler; also usable from tests.</summary>
        public void Press() => onPressed.Invoke();

        /// <summary>Rebuilds label and visuals. Works in edit mode (setup steps) and at runtime.</summary>
        protected virtual void OnConfigured()
        {
            EnsureVisuals();
            ApplyLayout(refreshPokeFilter: Application.isPlaying);
        }

        void Awake()
        {
            box = GetComponent<BoxCollider>();
            interactable = GetComponent<XRSimpleInteractable>();
            pokeFilter = GetComponent<XRPokeFilter>();
            EnsureVisuals();
            ApplyLayout(refreshPokeFilter: false); // the filter reads the collider itself in Start
        }

        void OnEnable()
        {
            if (interactable != null) interactable.selectEntered.AddListener(OnSelectEntered);
        }

        void OnDisable()
        {
            if (interactable != null) interactable.selectEntered.RemoveListener(OnSelectEntered);
        }

        void OnSelectEntered(SelectEnterEventArgs args)
        {
            // Only fingertip / controller-tip pokes count; ignore near-far ray or gaze selects.
            if (args.interactorObject is not IPokeStateDataProvider) return;
            var now = Time.unscaledTime;
            if (now - lastPressedAt < cooldownSeconds) return;
            lastPressedAt = now;
            pressedFlashUntil = now + 0.2f;
            Press();
        }

        void Update()
        {
            if (face == null) return;
            var now = Time.unscaledTime;

            var strength = 0f;
            var data = pokeFilter != null ? pokeFilter.pokeStateData : null;
            if (data != null) strength = Mathf.Clamp01(data.Value.interactionStrength);

            // Face sinks with the fingertip, and stays down briefly after a press.
            var depth = now < pressedFlashUntil ? 1f : strength;
            var p = face.localPosition;
            p.z = -travel * (1f - depth);
            face.localPosition = p;

            Color c;
            if (now < pressedFlashUntil) c = pressedColor;
            else if (strength > 0f) c = Color.Lerp(faceColor, hoverColor, 0.4f + 0.6f * strength);
            else c = faceColor;
            if (now - lastPressedAt < cooldownSeconds && now >= pressedFlashUntil) c.a *= 0.5f;
            if (faceImage != null) faceImage.color = c;
        }

        void EnsureVisuals()
        {
            var faceT = transform.Find(FaceName);
            if (faceT == null)
            {
                var go = new GameObject(FaceName, typeof(RectTransform));
                go.transform.SetParent(transform, false);
                faceT = go.transform;
            }
            face = (RectTransform)faceT;

            var canvas = face.GetComponent<Canvas>();
            if (canvas == null) canvas = face.gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            faceImage = face.GetComponent<Image>();
            if (faceImage == null) faceImage = face.gameObject.AddComponent<Image>();
            faceImage.raycastTarget = false;

            var labelT = face.Find(LabelName);
            if (labelT == null)
            {
                var go = new GameObject(LabelName, typeof(RectTransform));
                go.transform.SetParent(face, false);
                labelT = go.transform;
            }
            labelText = labelT.GetComponent<TextMeshProUGUI>();
            if (labelText == null) labelText = labelT.gameObject.AddComponent<TextMeshProUGUI>();
            labelText.raycastTarget = false;
        }

        void ApplyLayout(bool refreshPokeFilter)
        {
            if (box == null) box = GetComponent<BoxCollider>();
            if (box != null)
            {
                box.isTrigger = false; // XRI ignores trigger colliders unless serialized explicitly
                box.center = Vector3.zero;
                box.size = new Vector3(size.x, size.y, travel * 2f);
            }

            if (face != null)
            {
                face.localRotation = Quaternion.identity;
                face.localPosition = new Vector3(0f, 0f, -travel);
                face.localScale = Vector3.one / CanvasUnitsPerMetre;
                face.sizeDelta = size * CanvasUnitsPerMetre;
            }
            if (faceImage != null) faceImage.color = faceColor;

            if (labelText != null)
            {
                var rt = labelText.rectTransform;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                rt.localPosition = new Vector3(rt.localPosition.x, rt.localPosition.y, -1f); // 1 mm in front of the face
                labelText.text = label;
                labelText.color = labelColor;
                labelText.alignment = TextAlignmentOptions.Center;
                labelText.textWrappingMode = TextWrappingModes.NoWrap;
                labelText.enableAutoSizing = true;
                labelText.fontSizeMin = 4f;
                labelText.fontSizeMax = size.y * CanvasUnitsPerMetre * 0.5f;
                labelText.margin = new Vector4(2f, 1f, 2f, 1f);
            }

            // Reconfigured at runtime: re-run the poke filter's setup so its depth uses the new collider size.
            if (refreshPokeFilter && pokeFilter != null && box != null)
                pokeFilter.pokeCollider = box;
        }
    }
}
