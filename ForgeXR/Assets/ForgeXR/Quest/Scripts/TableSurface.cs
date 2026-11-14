using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;

namespace Forge
{
    /// <summary>
    /// Finds where a camera ray meets the real table. No manual alignment:
    ///   1. A Space Setup horizontal plane (ARPlaneManager planes on the TablePlane layer, MeshCollider) hit by the ray.
    ///   2. Otherwise the last plane height seen, else the most common resting fingertip height seen by the camera
    ///      in front of the wearer (the builder rests fingers on the table while handling pieces; a hand in the lap
    ///      is below and out of view, so it does not count), else a seed of head height minus <see cref="seedBelowHead"/>.
    /// Floor-like planes (too far below the head or too close to the XR Origin floor) are ignored.
    /// The ray is intersected <see cref="pieceHalfHeight"/> above the table so the ring lands under the piece's centre.
    /// </summary>
    public class TableSurface : MonoBehaviour
    {
        public enum Source { None, Seed, Fingertip, LastPlane, Plane }

        [SerializeField] LayerMask planeMask;
        [SerializeField] XROrigin origin;

        [Header("Geometry")]
        [SerializeField] float maxRayDistance = 2.5f;
        [Tooltip("Pieces are 1-2 cm tall: intersect this far above the table (m).")]
        [SerializeField] float pieceHalfHeight = 0.008f;
        [Tooltip("Planes or fingertips more than this far below the head are ignored (floor) (m).")]
        [SerializeField] float maxBelowHead = 1.25f;
        [Tooltip("...and less than this above the XR Origin floor (m).")]
        [SerializeField] float minAboveFloor = 0.3f;

        [Header("Fallback height")]
        [SerializeField] float seedBelowHead = 0.5f;
        [Tooltip("Index-tip joint centre sits about this high above a surface it touches (m).")]
        [SerializeField] float fingertipRadius = 0.008f;
        [Tooltip("Fingertips moving slower than this count as resting (m/s).")]
        [SerializeField] float stableSpeed = 0.08f;
        [Tooltip("Fingertips must be at least this far below the head (m).")]
        [SerializeField] float minBelowHead = 0.25f;
        [SerializeField] int minFingertipSamples = 10;
        [SerializeField] float fingertipWindowSeconds = 90f;
        [Tooltip("Resting fingertips must be at least this far in front of the head, horizontally (m).")]
        [SerializeField] float minForwardOfHead = 0.15f;
        [Tooltip("Height band width for finding the most common resting height (m).")]
        [SerializeField] float binSize = 0.015f;
        [Tooltip("A different height band must have this many times the samples of the current one to take over.")]
        [SerializeField] float switchRatio = 1.5f;

        public Source LastSource { get; private set; } = Source.None;
        /// <summary>Best current table height (world y), whatever the source.</summary>
        public float TableHeight => FallbackHeight(out _);

        float? seedY;
        float? lastPlaneY;
        float? fingertipY;
        float nextPercentile;
        readonly List<(float time, float y)> samples = new();
        readonly List<float> sortBuffer = new();
        readonly Vector3[] lastTip = new Vector3[2];
        float lastTipTime;
        int lastTipCount;
        Camera cam;
        CameraFrameSource frameSource;
        readonly Dictionary<int, int> binCounts = new();

        void Awake()
        {
            if (origin == null) origin = FindAnyObjectByType<XROrigin>();
            if (planeMask.value == 0)
            {
                var layer = LayerMask.NameToLayer("TablePlane");
                if (layer >= 0) planeMask = 1 << layer;
            }
        }

        Transform Head()
        {
            if (cam == null || !cam.isActiveAndEnabled) cam = Camera.main;
            return cam != null ? cam.transform : null;
        }

        float FloorY => origin != null && origin.Origin != null ? origin.Origin.transform.position.y : float.NegativeInfinity;

        bool PlausibleTableY(float y, float headY) =>
            y < headY - minBelowHead * 0.5f && y > headY - maxBelowHead && y > FloorY + minAboveFloor;

        /// <summary>Point on the table surface under the piece seen along <paramref name="ray"/>.</summary>
        public bool TryIntersect(Ray ray, out Vector3 tablePoint)
        {
            tablePoint = default;
            var head = Head();
            var headY = head != null ? head.position.y : ray.origin.y;
            if (ray.direction.y > -0.05f) return false; // looking at or above the horizon

            float tableY;
            if (planeMask.value != 0 &&
                Physics.Raycast(ray, out var hit, maxRayDistance, planeMask, QueryTriggerInteraction.Ignore) &&
                hit.normal.y > 0.9f && PlausibleTableY(hit.point.y, headY))
            {
                tableY = hit.point.y;
                lastPlaneY = tableY;
                LastSource = Source.Plane;
            }
            else
            {
                tableY = FallbackHeight(out var src);
                LastSource = src;
                if (src == Source.None) return false;
            }

            var liftedY = tableY + pieceHalfHeight;
            var t = (liftedY - ray.origin.y) / ray.direction.y;
            if (t <= 0f || t > maxRayDistance) return false;
            tablePoint = ray.GetPoint(t);
            tablePoint.y = tableY;
            return true;
        }

        float FallbackHeight(out Source source)
        {
            if (lastPlaneY.HasValue) { source = Source.LastPlane; return lastPlaneY.Value; }
            if (fingertipY.HasValue) { source = Source.Fingertip; return fingertipY.Value; }
            if (!seedY.HasValue)
            {
                var head = Head();
                if (head == null || head.localPosition.sqrMagnitude < 1e-4f) { source = Source.None; return 0f; } // not tracked yet
                seedY = Mathf.Max(head.position.y - seedBelowHead, FloorY + minAboveFloor);
                Debug.Log($"[Forge] Table height seeded at {seedY.Value:F3} (head {head.position.y:F3} - {seedBelowHead})");
            }
            source = Source.Seed;
            return seedY.Value;
        }

        void Update()
        {
            SampleFingertips();
            if (Time.unscaledTime >= nextPercentile)
            {
                nextPercentile = Time.unscaledTime + 0.5f;
                UpdateFingertipEstimate();
            }
        }

        void SampleFingertips()
        {
            var head = Head();
            if (head == null) return;
            var tips = ForgeHands.IndexTipPositions;
            var now = Time.unscaledTime;
            var dt = now - lastTipTime;

            // Rate-limit to ~10 Hz and only compare against the previous sample of the same hand count.
            if (dt < 0.1f) return;
            if (tips.Count == lastTipCount && dt < 0.5f)
            {
                for (var i = 0; i < tips.Count && i < 2; i++)
                {
                    var speed = Vector3.Distance(tips[i], lastTip[i]) / dt;
                    var y = tips[i].y;
                    if (speed < stableSpeed && PlausibleTableY(y, head.position.y) && InFrontAndInView(tips[i], head))
                        samples.Add((now, y - fingertipRadius));
                }
            }
            for (var i = 0; i < tips.Count && i < 2; i++) lastTip[i] = tips[i];
            lastTipCount = tips.Count;
            lastTipTime = now;

            var cutoff = now - fingertipWindowSeconds;
            var drop = 0;
            while (drop < samples.Count && samples[drop].time < cutoff) drop++;
            if (drop > 0) samples.RemoveRange(0, drop);
        }

        /// <summary>A table touch happens in front of the wearer, where the camera looks; a lap rest does not.</summary>
        bool InFrontAndInView(Vector3 tip, Transform head)
        {
            var fwd = head.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) return false;
            var toTip = tip - head.position; toTip.y = 0f;
            if (Vector3.Dot(toTip, fwd.normalized) < minForwardOfHead) return false;
            if (frameSource == null) frameSource = FindAnyObjectByType<CameraFrameSource>();
            return frameSource == null || !frameSource.HasCapture || frameSource.LatestCapture.IsInView(tip, 0.02f);
        }

        void UpdateFingertipEstimate()
        {
            if (samples.Count < minFingertipSamples) return;

            // Most common resting height (1.5 cm bands, neighbours included), not the lowest: one dip can't win.
            binCounts.Clear();
            foreach (var s in samples)
            {
                var b = Mathf.FloorToInt(s.y / binSize);
                binCounts[b] = binCounts.TryGetValue(b, out var c) ? c + 1 : 1;
            }
            int Score(int b) => (binCounts.TryGetValue(b - 1, out var l) ? l : 0) + (binCounts.TryGetValue(b, out var m) ? m : 0) +
                                (binCounts.TryGetValue(b + 1, out var r) ? r : 0);
            int best = 0, bestScore = -1;
            foreach (var b in binCounts.Keys)
            {
                var sc = Score(b);
                if (sc > bestScore || (sc == bestScore && b > best)) { best = b; bestScore = sc; }
            }
            if (bestScore < minFingertipSamples) return;

            // Hysteresis: keep the current band unless another clearly dominates.
            if (fingertipY.HasValue)
            {
                var cur = Mathf.FloorToInt(fingertipY.Value / binSize);
                if (Mathf.Abs(best - cur) > 1 && bestScore < switchRatio * Score(cur)) best = cur;
            }

            // Median of the samples in the winning band and its neighbours.
            sortBuffer.Clear();
            foreach (var s in samples)
                if (Mathf.Abs(Mathf.FloorToInt(s.y / binSize) - best) <= 1) sortBuffer.Add(s.y);
            if (sortBuffer.Count == 0) return;
            sortBuffer.Sort();
            var y = sortBuffer[sortBuffer.Count / 2];
            if (!fingertipY.HasValue || Mathf.Abs(fingertipY.Value - y) > 0.01f)
                Debug.Log($"[Forge] Table height from fingertips: {y:F3} ({sortBuffer.Count}/{samples.Count} resting samples in view)");
            fingertipY = y;
        }
    }
}
