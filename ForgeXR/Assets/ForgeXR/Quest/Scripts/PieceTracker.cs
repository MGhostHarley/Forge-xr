using System.Collections.Generic;
using UnityEngine;

namespace Forge
{
    /// <summary>
    /// Per part id: turns noisy per-frame table points into a steady PickupSpot position.
    ///   - Ignores detections below <see cref="minConfidence"/>.
    ///   - Blends each accepted measurement into the target and smooths the spot toward it.
    ///   - A jump larger than <see cref="jumpDistance"/> is only accepted after <see cref="jumpConfirmFrames"/>
    ///     consistent frames (the piece was moved), so a single misdetection doesn't teleport the highlight.
    ///   - Holds the last position until <see cref="maxMissedAnswers"/> classifier answers in a row miss the piece while
    ///     it is in view (<see cref="handMissedAnswers"/> while a tracked hand is near it, covering the piece).
    ///   - A spot never detected, or lost after the hold, is hidden until the piece is seen again.
    /// Spots are free in world space (StagingMat is an identity-pose container), never deactivated,
    /// so they stay registered in HighlightRegistry.
    /// </summary>
    public class PieceTracker : MonoBehaviour
    {
        [SerializeField, Range(0f, 1f)] float minConfidence = 0.5f;
        [Tooltip("Weight of a new measurement in the target position (0..1).")]
        [SerializeField, Range(0.05f, 1f)] float measurementWeight = 0.6f;
        [Tooltip("Time constant of the visual smoothing toward the target (s).")]
        [SerializeField] float smoothingTime = 0.15f;
        [SerializeField] float jumpDistance = 0.12f;
        [SerializeField] int jumpConfirmFrames = 2;
        [Tooltip("Detections at or above this confidence (the classifier) move a piece immediately; only weaker ones "
                 + "(colour tracking) need jumpConfirmFrames agreeing reports for a big jump.")]
        [SerializeField, Range(0f, 1f)] float trustedConfidence = 0.75f;
        [Tooltip("Hide a piece after this many classifier answers in a row miss it while its spot is in view.")]
        [SerializeField] int maxMissedAnswers = 3;
        [Tooltip("...or this many while a tracked hand is near it (hand covering the piece).")]
        [SerializeField] int handMissedAnswers = 6;
        [SerializeField] float handNearDistance = 0.10f;
        [Tooltip("Show detected but not highlighted pieces (dim ring). Off = only the highlighted piece is shown.")]
        [SerializeField] bool showIdleSpots = true;
        [Tooltip("A detection gap shorter than this (s) never counts as missed (frames arrive at ~5 Hz).")]
        [SerializeField] float missedGraceSeconds = 0.4f;

        CameraFrameSource frameSource;

        class Track
        {
            public PickupSpot spot;
            public bool everSeen, visible, tracked, hasSize;
            public Vector2 size;
            public Vector3 target;
            public float lastSeen = float.NegativeInfinity;
            /// <summary>Seconds the spot has been inside the camera view without a detection.</summary>
            public int missedAnswers;
            public Vector3 pending;
            public int pendingCount;
            public bool snapNext = true;
        }

        readonly Dictionary<string, Track> tracks = new();
        float nextSpotScan;

        /// <summary>True while the piece is shown (seen recently or within its hold).</summary>
        public bool IsTracked(string partId) => tracks.TryGetValue(partId, out var t) && t.tracked;

        HashSet<string> shownParts; // null = show every tracked part

        /// <summary>Whether a part is drawn in the current walkthrough (diagnostics).</summary>
        public bool IsShownPart(string partId) => shownParts == null || shownParts.Contains(partId);

        float nextStateLog;

        /// <summary>One line per tracked part: shown/hidden, missed answers, in hand (diagnostics).</summary>
        public string StateSummary()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in tracks)
            {
                var t = kv.Value;
                if (!t.everSeen) continue;
                var state = t.visible ? "SHOWN" : !t.tracked ? "lost" : !IsShownPart(kv.Key) ? "filtered" : "hidden";
                sb.Append($"{kv.Key}={state}(miss {t.missedAnswers}, age {Time.unscaledTime - t.lastSeen:F1}s) ");
            }
            return sb.Length > 0 ? sb.ToString() : "no parts seen yet";
        }

        /// <summary>Only these parts get a visible ring (the current walkthrough's); others are still tracked.</summary>
        public void SetShownParts(IEnumerable<string> parts) => shownParts = parts == null ? null : new HashSet<string>(parts);

        /// <summary>Current smoothed table position of a tracked piece.</summary>
        public bool TryGetPosition(string partId, out Vector3 position)
        {
            position = default;
            if (!tracks.TryGetValue(partId, out var t) || !t.tracked || t.spot == null) return false;
            position = t.spot.transform.position;
            return true;
        }

        void Start()
        {
            ScanSpots();
            foreach (var t in tracks.Values) SetVisible(t, false);
        }

        void ScanSpots()
        {
            nextSpotScan = Time.unscaledTime + 2f;
            foreach (var spot in FindObjectsByType<PickupSpot>(FindObjectsSortMode.None))
            {
                if (string.IsNullOrEmpty(spot.Id)) continue;
                if (!tracks.TryGetValue(spot.Id, out var t))
                {
                    t = new Track();
                    tracks[spot.Id] = t;
                }
                if (t.spot != spot)
                {
                    t.spot = spot;
                    t.visible = true; // force the hide below to apply
                    SetVisible(t, false);
                }
            }
        }

        /// <summary>One detection of <paramref name="partId"/> at a table point this frame.</summary>
        /// <summary>Report with the object's apparent size (m) and whether it was placed in a hand (lifted).</summary>
        public void Report(string partId, Vector3 point, float confidence, Vector2 size, bool inHand)
        {
            Report(partId, point, confidence);
            if (!tracks.TryGetValue(partId, out var t) || t.spot == null || confidence < minConfidence) return;
            t.size = t.hasSize ? Vector2.Lerp(t.size, size, 0.5f) : size;
            t.hasSize = true;
            t.spot.SetBox(t.size, !inHand);
        }

        /// <summary>Report keeping the last known size, updating whether the piece is in a hand.</summary>
        public void Report(string partId, Vector3 point, float confidence, bool inHand)
        {
            Report(partId, point, confidence);
            if (tracks.TryGetValue(partId, out var t) && t.spot != null && t.hasSize && confidence >= minConfidence)
                t.spot.SetBox(t.size, !inHand);
        }

        public void Report(string partId, Vector3 tablePoint, float confidence)
        {
            if (confidence < minConfidence || string.IsNullOrEmpty(partId)) return;
            if (!tracks.TryGetValue(partId, out var t))
            {
                t = new Track();
                tracks[partId] = t;
            }

            var now = Time.unscaledTime;
            var lost = !t.everSeen || t.missedAnswers >= MaxMisses(t);
            if (lost)
            {
                t.target = tablePoint;
                t.snapNext = true;
                t.pendingCount = 0;
            }
            else if (Vector3.Distance(tablePoint, t.target) > jumpDistance)
            {
                if (t.pendingCount > 0 && Vector3.Distance(tablePoint, t.pending) < jumpDistance * 0.5f) t.pendingCount++;
                else { t.pending = tablePoint; t.pendingCount = 1; }
                if (confidence < trustedConfidence && t.pendingCount < jumpConfirmFrames) return; // unconfirmed jump: hold
                t.target = tablePoint;
                t.pendingCount = 0;
            }
            else
            {
                t.target = Vector3.Lerp(t.target, tablePoint, measurementWeight);
                t.pendingCount = 0;
            }

            t.everSeen = true;
            t.lastSeen = now;
            t.missedAnswers = 0;
        }

        int MaxMisses(Track t)
        {
            if (t.spot != null && ForgeHands.NearestHandDistance(t.spot.transform.position) < handNearDistance)
                return Mathf.Max(maxMissedAnswers, handMissedAnswers);
            return maxMissedAnswers;
        }

        /// <summary>
        /// A classifier answer arrived for the frame with <paramref name="info"/>: every tracked piece whose spot was in
        /// that frame's view but not in <paramref name="seen"/> counts one miss. Out-of-frame pieces are held.
        /// </summary>
        public void OnClassifierAnswer(ICollection<string> seen, in CaptureInfo info)
        {
            foreach (var kv in tracks)
            {
                var t = kv.Value;
                if (!t.everSeen || seen.Contains(kv.Key)) continue;
                if (info.IsInView(t.target)) t.missedAnswers++;
            }
        }

        void Update()
        {
            if (Time.unscaledTime >= nextStateLog)
            {
                nextStateLog = Time.unscaledTime + 2f;
                ForgeLog.Diag("TRACKS " + StateSummary());
            }
            if (Time.unscaledTime >= nextSpotScan) ScanSpots();
            var now = Time.unscaledTime;
            var k = smoothingTime > 1e-3f ? 1f - Mathf.Exp(-Time.unscaledDeltaTime / smoothingTime) : 1f;


            foreach (var kv in tracks)
            {
                var t = kv.Value;
                if (t.spot == null) continue;
                // Held until enough classifier answers in a row miss it while in view (OnClassifierAnswer); out-of-frame
                // pieces are held where they were last seen (the camera follows the head, not the eyes).
                var tracked = t.everSeen && t.missedAnswers < MaxMisses(t);
                if (tracked)
                {
                    var tr = t.spot.transform;
                    if (t.snapNext) { tr.SetPositionAndRotation(t.target, Quaternion.identity); t.snapNext = false; }
                    else tr.position = Vector3.Lerp(tr.position, t.target, k);
                }
                else if (t.everSeen && !t.snapNext)
                {
                    t.snapNext = true; // lost: next detection snaps instead of sliding across the table
                }
                t.tracked = tracked;
                var shown = shownParts == null || shownParts.Contains(kv.Key);
                SetVisible(t, tracked && shown && (showIdleSpots || t.spot.IsHighlighted));
            }
        }

        static void SetVisible(Track t, bool visible)
        {
            if (t.visible == visible) return;
            t.visible = visible;
            if (t.spot != null) t.spot.SetVisible(visible);
        }
    }
}
