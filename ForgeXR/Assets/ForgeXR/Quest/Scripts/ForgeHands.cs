using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.Hands;

namespace Forge
{
    /// <summary>
    /// Tracked-hand joint positions in world space, cached once per frame.
    /// XR Hands reports joint poses relative to the XR Origin's camera floor offset, so they are transformed by it here.
    /// Used by TableSurface (fingertip table height) and PieceTracker (hold a piece while a hand covers it).
    /// </summary>
    public static class ForgeHands
    {
        static readonly List<XRHandSubsystem> Subsystems = new();
        static readonly List<Vector3> IndexTips = new();
        static readonly List<Vector3> CoverPoints = new();
        static int cachedFrame = -1;
        static XROrigin origin;

        /// <summary>World positions of the index tips of all tracked hands (0-2 entries).</summary>
        public static IReadOnlyList<Vector3> IndexTipPositions { get { Refresh(); return IndexTips; } }

        /// <summary>Palm, index tip and thumb tip of all tracked hands: points that can cover a piece on the table.</summary>
        public static IReadOnlyList<Vector3> CoverPositions { get { Refresh(); return CoverPoints; } }

        /// <summary>Distance from <paramref name="p"/> to the nearest tracked hand point, or +infinity.</summary>
        public static float NearestHandDistance(Vector3 p)
        {
            Refresh();
            var best = float.PositiveInfinity;
            foreach (var c in CoverPoints) best = Mathf.Min(best, Vector3.Distance(p, c));
            return best;
        }

        static void Refresh()
        {
            if (cachedFrame == Time.frameCount) return;
            cachedFrame = Time.frameCount;
            IndexTips.Clear();
            CoverPoints.Clear();

            XRHandSubsystem hands = null;
            SubsystemManager.GetSubsystems(Subsystems);
            foreach (var s in Subsystems)
                if (s.running) { hands = s; break; }
            if (hands == null) return;

            if (origin == null) origin = Object.FindAnyObjectByType<XROrigin>();
            var space = origin == null ? null
                : origin.CameraFloorOffsetObject != null ? origin.CameraFloorOffsetObject.transform : origin.transform;

            Add(hands.leftHand, space);
            Add(hands.rightHand, space);
        }

        static void Add(XRHand hand, Transform space)
        {
            if (!hand.isTracked) return;
            if (TryJoint(hand, XRHandJointID.IndexTip, space, out var tip))
            {
                IndexTips.Add(tip);
                CoverPoints.Add(tip);
            }
            if (TryJoint(hand, XRHandJointID.Palm, space, out var palm)) CoverPoints.Add(palm);
            if (TryJoint(hand, XRHandJointID.ThumbTip, space, out var thumb)) CoverPoints.Add(thumb);
        }

        static bool TryJoint(XRHand hand, XRHandJointID id, Transform space, out Vector3 world)
        {
            world = default;
            if (!hand.GetJoint(id).TryGetPose(out var pose)) return false;
            world = space != null ? space.TransformPoint(pose.position) : pose.position;
            return true;
        }

#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { cachedFrame = -1; origin = null; IndexTips.Clear(); CoverPoints.Clear(); }
#endif
    }
}
