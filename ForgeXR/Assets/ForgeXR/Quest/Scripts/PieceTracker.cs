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
    ///   - Holds the last position for <see cref="holdSeconds"/> when the piece isn't seen, or
    ///     <see cref="handHoldSeconds"/> while a tracked hand is near it (hand covering the piece).
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
        [SerializeField] float holdSeconds = 1.5f;
        [SerializeField] float handHoldSeconds = 4f;
        [SerializeField] float handNearDistance = 0.10f;
        [Tooltip("Show detected but not highlighted pieces (dim ring). Off = only the highlighted piece is shown.")]
        [SerializeField] bool showIdleSpots = true;

        class Track
        {
            public PickupSpot spot;
            public bool everSeen, visible;
            public Vector3 target;
            public float lastSeen = float.NegativeInfinity;
            public Vector3 pending;
            public int pendingCount;
            public bool snapNext = true;
        }

        readonly Dictionary<string, Track> tracks = new();
        float nextSpotScan;

        /// <summary>True while the piece is shown (seen recently or within its hold).</summary>
        public bool IsTracked(string partId) => tracks.TryGetValue(partId, out var t) && t.visible;

        /// <summary>Current smoothed table position of a tracked piece.</summary>
        public bool TryGetPosition(string partId, out Vector3 position)
        {
            position = default;
            if (!tracks.TryGetValue(partId, out var t) || !t.visible || t.spot == null) return false;
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
        public void Report(string partId, Vector3 tablePoint, float confidence)
        {
            if (confidence < minConfidence || string.IsNullOrEmpty(partId)) return;
            if (!tracks.TryGetValue(partId, out var t))
            {
                t = new Track();
                tracks[partId] = t;
            }

            var now = Time.unscaledTime;
            var lost = !t.everSeen || now - t.lastSeen > CurrentHold(t);
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
                if (t.pendingCount < jumpConfirmFrames) return; // unconfirmed jump: ignore, keep holding
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
        }

        float CurrentHold(Track t)
        {
            if (t.spot != null && ForgeHands.NearestHandDistance(t.spot.transform.position) < handNearDistance)
                return Mathf.Max(holdSeconds, handHoldSeconds);
            return holdSeconds;
        }

        void Update()
        {
            if (Time.unscaledTime >= nextSpotScan) ScanSpots();
            var now = Time.unscaledTime;
            var k = smoothingTime > 1e-3f ? 1f - Mathf.Exp(-Time.unscaledDeltaTime / smoothingTime) : 1f;

            foreach (var t in tracks.Values)
            {
                if (t.spot == null) continue;
                var tracked = t.everSeen && now - t.lastSeen <= CurrentHold(t);
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
                SetVisible(t, tracked && (showIdleSpots || t.spot.IsHighlighted));
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
