using System.Text;
using TMPro;
using UnityEngine;

namespace Forge
{
    /// <summary>
    /// The only floating UI: a short instruction line with a Next/Done button to its right and a small
    /// Reset button to its left, hovering above the currently highlighted real piece(s).
    /// Built and wired by Setup_NextInput (orders 410/415). Harley's walkthrough sets the text through
    /// SetText / SetNextLabel; presses go through NextInput (onNext / onReset).
    ///
    /// Placement (polled every LateUpdate from HighlightRegistry; no event needed):
    ///   Tracking - midpoint of the VISIBLE highlighted PickupSpots + heightAboveSpot (above the spot labels).
    ///   Held     - no highlighted spot visible, but one was seen since the highlight last changed:
    ///              stay at that last known position.
    ///   Fallback - nothing seen for this highlight set: placed ONCE in front of the head
    ///              (fallbackForward ahead, fallbackBelowEye down), then held still (not head-locked).
    /// The cue and its buttons are never hidden, so the builder can always advance.
    /// While any of its buttons is hovered or selected, position and heading freeze so the target never
    /// moves under the ray. Heading is yaw-only, facing the viewer (local +Z points away from them).
    /// </summary>
    [DefaultExecutionOrder(200)] // after MilestoneSequence (100) sets the highlight in Start
    public class FloatingCue : MonoBehaviour
    {
        public enum Mode { None, Tracking, Held, Fallback }

        [SerializeField] TMP_Text instruction;
        [SerializeField] PokeButton nextButton;
        [SerializeField] PokeButton resetButton;

        [Header("Placement")]
        [Tooltip("Height (m) of the cue centre above the highlighted spot(s). Spot labels sit at 8-10.5 cm.")]
        [SerializeField] float heightAboveSpot = 0.15f;
        [Tooltip("Follow smoothing time constant (s).")]
        [SerializeField] float followTime = 0.2f;
        [Tooltip("The cue starts moving only once its target is further than this (m).")]
        [SerializeField] float deadZone = 0.01f;
        [Tooltip("The cue starts turning only once the viewer is more than this many degrees off its heading.")]
        [SerializeField] float yawDeadZoneDegrees = 5f;
        [SerializeField] float fallbackForward = 0.45f;
        [SerializeField] float fallbackBelowEye = 0.30f;
        [Tooltip("Seconds after scene load before the head-relative fallback may be placed (head pose settles).")]
        [SerializeField] float fallbackDelay = 1f;

        public TMP_Text Text => instruction;
        public PokeButton NextButton => nextButton;
        public PokeButton ResetButton => resetButton;
        public Mode CurrentMode => mode;

        Mode mode = Mode.None;
        string highlightSig;
        bool seenThisHighlight;
        Vector3 lastKnown;
        Vector3 targetPos;
        float yaw;
        bool placed, moving, turning;
        Vector3 lastLoggedPos;
        readonly StringBuilder sigBuilder = new();

        public void Configure(TMP_Text text, PokeButton next, PokeButton reset)
        {
            instruction = text;
            nextButton = next;
            resetButton = reset;
        }

        public void SetText(string text)
        {
            if (instruction != null) instruction.text = text;
        }

        public void SetNextLabel(string text)
        {
            if (nextButton != null) nextButton.SetLabel(text);
        }

        bool Engaged => (nextButton != null && nextButton.IsEngaged) || (resetButton != null && resetButton.IsEngaged);

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var head = cam.transform;

            UpdateTarget(head);
            if (mode == Mode.None) return; // nothing to anchor to yet
            if (!placed)
            {
                transform.position = targetPos;
                yaw = YawTowards(head.position, targetPos, yaw);
                transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                placed = true;
                lastLoggedPos = targetPos;
                return;
            }
            if (Engaged) return; // freeze while the builder aims at / pinches a button

            var k = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.01f, followTime));

            var pos = transform.position;
            var dist = Vector3.Distance(pos, targetPos);
            if (!moving && dist > deadZone) moving = true;
            if (moving)
            {
                pos = Vector3.Lerp(pos, targetPos, k);
                if (Vector3.Distance(pos, targetPos) < 0.001f) { pos = targetPos; moving = false; }
                transform.position = pos;
            }

            var wantYaw = YawTowards(head.position, pos, yaw);
            var dYaw = Mathf.Abs(Mathf.DeltaAngle(yaw, wantYaw));
            if (!turning && dYaw > yawDeadZoneDegrees) turning = true;
            if (turning)
            {
                yaw = Mathf.LerpAngle(yaw, wantYaw, k);
                if (Mathf.Abs(Mathf.DeltaAngle(yaw, wantYaw)) < 0.5f) turning = false;
                transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            }

            if (Vector3.Distance(pos, lastLoggedPos) > 0.05f && !moving)
            {
                lastLoggedPos = pos;
                ForgeLog.Diag($"CUE moved to {pos:F2} mode={mode}");
            }
        }

        void UpdateTarget(Transform head)
        {
            var sig = HighlightSignature();
            if (sig != highlightSig)
            {
                highlightSig = sig;
                seenThisHighlight = false;
            }

            var sum = Vector3.zero;
            var count = 0;
            foreach (var id in HighlightRegistry.ActiveIds)
            {
                if (!HighlightRegistry.TryGet(id, out var spot) || spot == null || !spot.IsVisible) continue;
                sum += spot.transform.position;
                count++;
            }

            if (count > 0)
            {
                lastKnown = sum / count + Vector3.up * heightAboveSpot;
                seenThisHighlight = true;
                SetMode(Mode.Tracking);
                targetPos = lastKnown;
            }
            else if (seenThisHighlight)
            {
                SetMode(Mode.Held);
                targetPos = lastKnown;
            }
            else if (mode != Mode.Fallback && Time.timeSinceLevelLoad >= fallbackDelay)
            {
                var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
                if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(head.up, Vector3.up);
                fwd.Normalize();
                targetPos = head.position + fwd * fallbackForward + Vector3.down * fallbackBelowEye;
                SetMode(Mode.Fallback);
            }
            // else: Fallback keeps its once-placed targetPos.
        }

        void SetMode(Mode m)
        {
            if (m == mode) return;
            mode = m;
            ForgeLog.Diag($"CUE mode={m} highlight=[{highlightSig}]");
        }

        string HighlightSignature()
        {
            sigBuilder.Clear();
            foreach (var id in HighlightRegistry.ActiveIds) sigBuilder.Append(id).Append(',');
            return sigBuilder.ToString();
        }

        /// <summary>Yaw (deg) so local +Z points from the viewer to <paramref name="at"/>, horizontally.</summary>
        static float YawTowards(Vector3 viewer, Vector3 at, float current)
        {
            var d = at - viewer;
            d.y = 0f;
            if (d.sqrMagnitude < 1e-4f) return current;
            return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        }
    }
}
