using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR;

namespace Forge
{
    /// <summary>
    /// Puts the staging mat in front of the wearer on the first valid head pose, then disables itself.
    /// Needed because the XR Origin uses the runtime default (Stage) space, whose origin and forward
    /// come from the room setup, not from where the wearer stands at launch.
    /// Pose: <see cref="forward"/> ahead of the head along its yaw, <see cref="belowHead"/> below eye
    /// height (never lower than <see cref="minAboveFloor"/> above the XR Origin floor), mat -Z facing
    /// the wearer. Never overrides a locked TableAligner.
    /// </summary>
    public class MatStartPlacer : MonoBehaviour
    {
        [SerializeField] float forward = 0.5f;
        [SerializeField] float belowHead = 0.45f;
        [SerializeField] float minAboveFloor = 0.6f;
        [Tooltip("Without XR, place after this many seconds; with XR running, give up waiting for tracking after 4x this.")]
        [SerializeField] float fallbackSeconds = 3f;

        static readonly List<XRInputSubsystem> InputSubsystems = new();

        float startTime;
        int trackedFrames;

        void OnEnable()
        {
            startTime = Time.unscaledTime;
            trackedFrames = 0;
        }

        void LateUpdate()
        {
            var aligner = FindAnyObjectByType<TableAligner>();
            if (aligner != null && aligner.IsLocked) { enabled = false; return; }

            var cam = Camera.main;
            if (cam == null) return;

            var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            var tracked = head.isValid
                          && head.TryGetFeatureValue(CommonUsages.isTracked, out var isTracked) && isTracked
                          && cam.transform.localPosition.sqrMagnitude > 1e-4f;
            trackedFrames = tracked ? trackedFrames + 1 : 0;

            // A few consecutive tracked frames avoids placing against the identity pose at startup.
            // Without XR (editor) place after fallbackSeconds; with XR, wait longer before giving up on tracking.
            var elapsed = Time.unscaledTime - startTime;
            var timedOut = elapsed > fallbackSeconds && (!XRRunning() || elapsed > fallbackSeconds * 4f);
            if (trackedFrames < 3 && !timedOut) return;

            Place(cam.transform);
            enabled = false;
        }

        void Place(Transform head)
        {
            var fwd = head.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = head.up; // looking straight down: use the top of the head
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();

            var origin = FindAnyObjectByType<XROrigin>();
            var floorY = origin != null && origin.Origin != null ? origin.Origin.transform.position.y : 0f;

            var pos = head.position + fwd * forward;
            pos.y = Mathf.Max(head.position.y - belowHead, floorY + minAboveFloor);

            // Mat +Z points away from the wearer, so -Z (the arrow, the builder's side) faces them.
            transform.SetPositionAndRotation(pos, Quaternion.LookRotation(fwd, Vector3.up));
            Debug.Log($"[Forge] MatStartPlacer placed StagingMat at {pos}");
        }

        /// <summary>True when an XR input subsystem is running (on device), so we keep waiting for tracking.</summary>
        static bool XRRunning()
        {
            SubsystemManager.GetSubsystems(InputSubsystems);
            foreach (var s in InputSubsystems)
                if (s.running) return true;
            return false;
        }
    }
}
