using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Filtering;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;

namespace Forge
{
    /// <summary>
    /// A small floating world-space button, pressed primarily by a hand PINCH (the name is historical).
    /// Used for Next and Reset (FloatingCue). Other code creates buttons via AddComponent + Configure
    /// and subscribes to onPressed.
    ///
    /// Press sources (all go through XRSimpleInteractable.selectEntered):
    ///   - Hand NearFarInteractor, ray + pinch: the far ray's end point must lie on this button.
    ///   - Hand NearFarInteractor, near pinch: the pinch point must be within nearPinchRadius (3 cm)
    ///     of the collider (tighter than the rig's 10 cm near-cast sphere, so picking up a real
    ///     LEGO piece next to a button does not press it).
    ///   - Controller NearFarInteractor (grip), same rules. Controller A/trigger is handled by NextInput.
    ///   - Poke (fingertip) as an extra, gated by the XRPokeFilter's push depth.
    /// A NearFar pinch also needs the button to have been hovered for hoverDwellSeconds before the
    /// pinch starts (the rig's StateChange select trigger only selects on the pinch edge, so a pinch
    /// that sweeps onto the button never presses it). Presses inside cooldownSeconds are ignored.
    ///
    /// Layout (local frame): the viewer is on the -Z side looking along +Z. The BoxCollider spans
    /// z = -travel..+travel; the face sits at z = -travel and sinks to z = 0 with a fingertip poke.
    /// The face is a tiny world-space uGUI canvas (Image + TMP) with no GraphicRaycaster.
    /// </summary>
    [RequireComponent(typeof(BoxCollider), typeof(XRSimpleInteractable), typeof(XRPokeFilter))]
    public class PokeButton : MonoBehaviour, IXRSelectFilter
    {
        [SerializeField] string label = "Next";
        [Tooltip("Face size in metres (width, height).")]
        [SerializeField] Vector2 size = new(0.06f, 0.03f);

        public UnityEvent onPressed = new();

        public string Label => label;
        public Vector2 Size => size;

        /// <summary>True while any interactor hovers or selects this button (FloatingCue freezes then).</summary>
        public bool IsEngaged => interactable != null && (interactable.isHovered || interactable.isSelected);

        [Header("Look")]
        [SerializeField] Color faceColor = new(0.12f, 0.14f, 0.18f, 0.9f);
        [SerializeField] Color hoverColor = new(0.25f, 0.45f, 0.85f, 0.95f);
        [SerializeField] Color pressedColor = new(0.2f, 0.75f, 0.35f, 1f);
        [SerializeField] Color labelColor = Color.white;
        [Tooltip("Poke push travel in metres before a poke press registers.")]
        [SerializeField] float travel = 0.012f;

        [Header("Pinch rules")]
        [Tooltip("Seconds after a press during which further presses are ignored.")]
        [SerializeField] float cooldownSeconds = 0.6f;
        [Tooltip("A NearFar pinch only counts if this interactor hovered the button at least this long before the pinch.")]
        [SerializeField] float hoverDwellSeconds = 0.12f;
        [Tooltip("Near pinch: max distance (m) from the pinch point to the collider.")]
        [SerializeField] float nearPinchRadius = 0.03f;
        [Tooltip("Ray pinch: tolerance (m) around the collider for the ray end point.")]
        [SerializeField] float rayHitTolerance = 0.015f;

        const string FaceName = "Face";
        const string LabelName = "Label";
        const float CanvasUnitsPerMetre = 1000f; // canvas units are millimetres
        const float PressedScale = 0.94f;

        RectTransform face;
        Image faceImage;
        TextMeshProUGUI labelText;
        BoxCollider box;
        XRSimpleInteractable interactable;
        XRPokeFilter pokeFilter;
        float lastPressedAt = float.NegativeInfinity;
        float pressedFlashUntil;
        float lastRejectLogAt = float.NegativeInfinity;
        readonly Dictionary<IXRHoverInteractor, float> hoverStart = new();

        /// <summary>Used by setup steps after AddComponent.</summary>
        public void Configure(string displayLabel, Vector2 faceSize)
        {
            label = displayLabel;
            size = faceSize;
            OnConfigured();
        }

        public void SetLabel(string displayLabel)
        {
            if (label == displayLabel && labelText != null && labelText.text == displayLabel) return;
            label = displayLabel;
            OnConfigured();
        }

        /// <summary>Raises onPressed. Called by the select handler; also usable from tests.</summary>
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
            if (interactable == null) return;
            interactable.selectEntered.AddListener(OnSelectEntered);
            interactable.hoverEntered.AddListener(OnHoverEntered);
            interactable.hoverExited.AddListener(OnHoverExited);
            interactable.selectFilters.Add(this);
        }

        void OnDisable()
        {
            if (interactable == null) return;
            interactable.selectEntered.RemoveListener(OnSelectEntered);
            interactable.hoverEntered.RemoveListener(OnHoverEntered);
            interactable.hoverExited.RemoveListener(OnHoverExited);
            interactable.selectFilters.Remove(this);
            hoverStart.Clear();
        }

        void OnHoverEntered(HoverEnterEventArgs args) => hoverStart[args.interactorObject] = Time.unscaledTime;
        void OnHoverExited(HoverExitEventArgs args) => hoverStart.Remove(args.interactorObject);

        // ------------------------------------------------------------------ IXRSelectFilter

        public bool canProcess => isActiveAndEnabled;

        /// <summary>Accepts poke and other interactors as-is; applies the pinch rules to NearFarInteractors.</summary>
        public bool Process(IXRSelectInteractor interactor, IXRSelectInteractable target)
        {
            if (interactor is not NearFarInteractor nearFar) return true;
            if (interactor.IsSelecting(target)) return true; // keep an existing selection valid

            var now = Time.unscaledTime;
            if (!hoverStart.TryGetValue(nearFar, out var since) || now - since < hoverDwellSeconds)
                return Reject(nearFar, "dwell");

            if (box == null) box = GetComponent<BoxCollider>();
            if (box == null) return false;

            // Near pinch: the pinch point must be right at the button.
            var nearOrigin = nearFar.nearInteractionCaster?.effectiveCastOrigin;
            if (nearOrigin != null)
            {
                var p = nearOrigin.position;
                if (Vector3.Distance(box.ClosestPoint(p), p) <= nearPinchRadius) return true;
            }

            // Ray pinch: the far ray must end on this button.
            if (nearFar.TryGetCurveEndPoint(out var end) == EndPointType.ValidCastHit)
            {
                var bounds = box.bounds;
                bounds.Expand(rayHitTolerance * 2f);
                if (bounds.Contains(end)) return true;
            }
            return Reject(nearFar, "notOnButton");
        }

        bool Reject(NearFarInteractor interactor, string reason)
        {
            var now = Time.unscaledTime;
            if (now - lastRejectLogAt > 0.25f)
            {
                lastRejectLogAt = now;
                ForgeLog.Diag($"REJECT {name} by {ForgeLog.Path(interactor.transform)} ({reason})");
            }
            return false;
        }

        // ------------------------------------------------------------------ press

        void OnSelectEntered(SelectEnterEventArgs args)
        {
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

            var poke = 0f;
            var data = pokeFilter != null ? pokeFilter.pokeStateData : null;
            if (data != null) poke = Mathf.Clamp01(data.Value.interactionStrength);
            var hovered = interactable != null && interactable.isHovered;
            var flashing = now < pressedFlashUntil;

            // Face sinks with a poking fingertip, and stays down briefly after any press.
            var depth = flashing ? 1f : poke;
            var p = face.localPosition;
            p.z = -travel * (1f - depth);
            face.localPosition = p;
            face.localScale = Vector3.one * ((flashing ? PressedScale : 1f) / CanvasUnitsPerMetre);

            Color c;
            if (flashing) c = pressedColor;
            else if (poke > 0f) c = Color.Lerp(faceColor, hoverColor, 0.4f + 0.6f * poke);
            else if (hovered) c = hoverColor;
            else c = faceColor;
            if (now - lastPressedAt < cooldownSeconds && !flashing) c.a *= 0.5f;
            if (faceImage != null) faceImage.color = c;
        }

        // ------------------------------------------------------------------ visuals

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
                box.isTrigger = false; // the NearFar near caster ignores triggers
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
