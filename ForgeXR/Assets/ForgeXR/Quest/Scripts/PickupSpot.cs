using TMPro;
using UnityEngine;

namespace Forge
{
    /// <summary>
    /// A part's pickup location on the staging mat. Registers itself with HighlightRegistry by Id.
    /// Visuals (built by the chunk 2 setup step, Setup_PickupSpots):
    ///   Ring  - flat ring lying on the table (~3.6 cm across), pulses while highlighted.
    ///   Glow  - filled disc inside the ring, only while highlighted.
    ///   Stem  - thin vertical line from the ring up to the label.
    ///   Label - TMP text above the spot that yaws to face the camera; its anchor never moves.
    /// Highlighted spots: yellow pulsing ring + glow + stem + label. Idle spots: dim white ring only.
    /// Hidden spots (piece never detected or lost; SetVisible is driven by PieceTracker) render nothing but stay
    /// enabled, so they remain registered in HighlightRegistry.
    /// No colliders, so spots never block hand poke/pinch interactions.
    /// </summary>
    public class PickupSpot : MonoBehaviour
    {
        [SerializeField] string id = PartIds.Torso;
        [SerializeField] string label = "Torso";

        [Header("Visuals (wired by setup)")]
        [SerializeField] MeshRenderer ring;
        [SerializeField] MeshRenderer glow;
        [SerializeField] MeshRenderer stem;
        [SerializeField] Transform labelRoot;
        [SerializeField] TMP_Text labelText;
        [Tooltip("Camera-facing bounding box outline drawn over the detected object (replaces the table ring).")]
        [SerializeField] LineRenderer box;
        [SerializeField] float activeLineWidth = 0.004f;
        [SerializeField] float idleLineWidth = 0.002f;

        [Header("Colours")]
        [SerializeField] Color idleRing = new(1f, 1f, 1f, 0.55f);
        [SerializeField] Color idleStem = new(1f, 1f, 1f, 0.3f);
        [SerializeField] Color idleLabel = new(1f, 1f, 1f, 0.8f);
        [SerializeField] Color activeColor = new(1f, 0.85f, 0.1f, 1f);
        [SerializeField, Range(0f, 1f)] float activeGlowAlpha = 0.28f;

        [Header("Pulse")]
        [SerializeField] float pulseHz = 1.2f;
        [SerializeField] float pulseScale = 0.1f;

        public string Id => id;
        public string Label => label;
        public bool IsHighlighted { get; private set; }
        /// <summary>False while the piece is not on the table as far as the camera knows (set by PieceTracker).</summary>
        public bool IsVisible { get; private set; } = true;

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        MaterialPropertyBlock mpb;
        Vector2 boxSize = new(0.04f, 0.04f); // world metres (width, height) of the object as seen by the camera
        bool boxOnTable = true;               // on the table the object's centre is above the spot; in a hand it's at it
        readonly Vector3[] corners = new Vector3[4];

        /// <summary>Object size (m) and whether it rests on the table; set by PieceTracker from detections.</summary>
        public void SetBox(Vector2 size, bool onTable)
        {
            boxSize = size;
            boxOnTable = onTable;
        }
        Vector3 ringBaseScale = Vector3.one;
        Camera cam;

        /// <summary>Used by setup steps to configure a spot after AddComponent.</summary>
        public void Configure(string partId, string displayLabel)
        {
            id = partId;
            label = displayLabel;
            if (labelText != null) labelText.text = displayLabel;
        }

        public void SetHighlighted(bool on)
        {
            IsHighlighted = on;
            OnHighlightChanged(on);
        }

        /// <summary>Shows or hides all of this spot's visuals without disabling it.</summary>
        public void SetVisible(bool visible)
        {
            if (IsVisible == visible) return;
            IsVisible = visible;
            ApplyState(IsHighlighted);
        }

        protected virtual void Awake()
        {
            if (ring != null) ringBaseScale = ring.transform.localScale;
            if (labelText != null) labelText.text = label;
            ApplyState(IsHighlighted);
        }

        protected virtual void OnHighlightChanged(bool on) => ApplyState(on);

        protected virtual void OnEnable() => HighlightRegistry.Register(this);
        protected virtual void OnDisable() => HighlightRegistry.Unregister(this);

        void ApplyState(bool on)
        {
            var ringColor = on ? activeColor : idleRing;
            SetColor(ring, ringColor);
            SetColor(stem, on ? WithAlpha(activeColor, 0.8f) : idleStem);
            // Bounding box over the object replaces the flat table ring (ring/glow/stem stay hidden).
            if (ring != null) ring.enabled = box == null && IsVisible;
            if (stem != null) stem.enabled = false;
            if (glow != null) glow.enabled = false;
            if (box != null)
            {
                box.enabled = IsVisible;
                box.widthMultiplier = on ? activeLineWidth : idleLineWidth;
                SetColor(box, on ? activeColor : idleRing);
            }
            if (labelText != null)
            {
                labelText.enabled = IsVisible;
                labelText.color = on ? activeColor : idleLabel;
                labelText.fontStyle = on ? FontStyles.Bold : FontStyles.Normal;
            }
            if (!on && ring != null) ring.transform.localScale = ringBaseScale;
        }

        void LateUpdate()
        {
            if (!IsVisible) return;
            UpdateBox();
            FaceCamera();

            if (IsHighlighted && ring != null)
            {
                var s = 0.5f + 0.5f * Mathf.Sin(Time.time * pulseHz * 2f * Mathf.PI);
                ring.transform.localScale = ringBaseScale * (1f + pulseScale * s);
                if (glow != null) SetColor(glow, WithAlpha(activeColor, activeGlowAlpha * (0.6f + 0.4f * s)));
            }
        }

        void UpdateBox()
        {
            if (box == null) return;
            if (cam == null || !cam.isActiveAndEnabled) { cam = Camera.main; if (cam == null) return; }
            var centre = transform.position + (boxOnTable ? Vector3.up * (boxSize.y * 0.5f) : Vector3.zero);
            var bt = box.transform;
            bt.position = centre;
            var toCam = cam.transform.position - centre;
            if (toCam.sqrMagnitude > 1e-6f) bt.rotation = Quaternion.LookRotation(-toCam.normalized, cam.transform.up);
            float hx = boxSize.x * 0.5f, hy = boxSize.y * 0.5f;
            corners[0] = new Vector3(-hx, -hy, 0f); corners[1] = new Vector3(hx, -hy, 0f);
            corners[2] = new Vector3(hx, hy, 0f); corners[3] = new Vector3(-hx, hy, 0f);
            box.loop = true;
            box.positionCount = 4;
            box.SetPositions(corners);
            if (labelRoot != null) labelRoot.position = centre + cam.transform.up * (hy + 0.018f);
        }

        /// <summary>Yaw-only billboard: the label's position stays fixed, only its heading changes.</summary>
        void FaceCamera()
        {
            if (labelRoot == null) return;
            if (cam == null || !cam.isActiveAndEnabled)
            {
                cam = Camera.main;
                if (cam == null) return;
            }
            var dir = labelRoot.position - cam.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) return;
            // TMP text reads correctly when its +Z points away from the viewer.
            labelRoot.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
        }

        void SetColor(Renderer r, Color c)
        {
            if (r == null) return;
            mpb ??= new MaterialPropertyBlock();
            r.GetPropertyBlock(mpb);
            mpb.SetColor(BaseColorId, c);
            r.SetPropertyBlock(mpb);
        }

        static Color WithAlpha(Color c, float a) { c.a = a; return c; }
    }
}
