using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

namespace Forge
{
    /// <summary>
    /// Single-grab transformer for the staging mat: it slides in the horizontal plane and turns only about world up.
    /// Yaw follows the twist of the grabbing hand about world up since the grab started. Height comes from
    /// <see cref="ResolveTableHeight"/>: the detected table plane under the mat (TablePlane layer), or
    /// the pinch point minus <see cref="pinchToTableOffset"/> when there is no plane (fingers rest on the table).
    /// </summary>
    public class MatPlanarGrabTransformer : XRBaseGrabTransformer
    {
        [Tooltip("Layer with the detected-plane colliders.")]
        [SerializeField] string tablePlaneLayer = "TablePlane";

        [Tooltip("Without a plane: table height = pinch point height minus this (metres).")]
        [SerializeField] float pinchToTableOffset = 0.015f;

        [Tooltip("The downward plane probe starts this far above the pinch point (metres).")]
        [SerializeField] float probeAbove = 0.25f;

        [Tooltip("Length of the downward plane probe (metres).")]
        [SerializeField] float probeLength = 0.6f;

        [Tooltip("A hit counts as a table only if the plane's up vector is within this cosine of world up.")]
        [SerializeField] float minUpDot = 0.9f;

        public float PinchToTableOffset { get => pinchToTableOffset; set => pinchToTableOffset = value; }

        /// <summary>True if the last resolved height came from a detected plane.</summary>
        public bool LastHeightFromPlane { get; private set; }

        IXRSelectInteractor m_Interactor;
        Quaternion m_StartHandRotation;
        Vector3 m_StartHandPosition;
        Vector3 m_StartMatPosition;
        float m_StartMatYaw;

        public override void OnGrab(XRGrabInteractable grabInteractable)
        {
            base.OnGrab(grabInteractable);
            m_Interactor = null; // re-captured on the next Process with the current interactor
        }

        public override void Process(XRGrabInteractable grabInteractable, XRInteractionUpdateOrder.UpdatePhase updatePhase,
            ref Pose targetPose, ref Vector3 localScale)
        {
            if (updatePhase != XRInteractionUpdateOrder.UpdatePhase.Dynamic &&
                updatePhase != XRInteractionUpdateOrder.UpdatePhase.OnBeforeRender)
                return;
            if (grabInteractable.interactorsSelecting.Count == 0)
                return;

            var interactor = grabInteractable.interactorsSelecting[0];
            var attach = interactor.GetAttachTransform(grabInteractable);
            if (attach == null)
                return;

            if (!ReferenceEquals(interactor, m_Interactor))
            {
                m_Interactor = interactor;
                m_StartHandRotation = attach.rotation;
                m_StartHandPosition = attach.position;
                m_StartMatPosition = grabInteractable.transform.position;
                m_StartMatYaw = grabInteractable.transform.eulerAngles.y;
            }

            var deltaYaw = TwistAboutUp(attach.rotation * Quaternion.Inverse(m_StartHandRotation));
            var yawRotation = Quaternion.Euler(0f, deltaYaw, 0f);

            var offset = m_StartMatPosition - m_StartHandPosition;
            offset.y = 0f;
            var position = attach.position + yawRotation * offset;
            position.y = ResolveTableHeight(position, attach.position.y);

            targetPose = new Pose(position, Quaternion.Euler(0f, m_StartMatYaw + deltaYaw, 0f));
        }

        public override void OnUnlink(XRGrabInteractable grabInteractable)
        {
            base.OnUnlink(grabInteractable);
            m_Interactor = null;
        }

        /// <summary>
        /// Table surface height at <paramref name="matPosition"/> (only x/z are used): the detected plane under it,
        /// else <paramref name="pinchHeight"/> minus the pinch offset.
        /// </summary>
        public float ResolveTableHeight(Vector3 matPosition, float pinchHeight)
        {
            if (TryGetPlaneHeight(matPosition, pinchHeight + probeAbove, out var planeY))
            {
                LastHeightFromPlane = true;
                return planeY;
            }
            LastHeightFromPlane = false;
            return pinchHeight - pinchToTableOffset;
        }

        /// <summary>Raycasts down from <paramref name="fromHeight"/> at the mat's x/z against upward-facing TablePlane colliders.</summary>
        public bool TryGetPlaneHeight(Vector3 matPosition, float fromHeight, out float height)
        {
            height = 0f;
            var layer = LayerMask.NameToLayer(tablePlaneLayer);
            if (layer < 0)
                return false;

            var origin = new Vector3(matPosition.x, fromHeight, matPosition.z);
            // Plane meshes are single-sided; hit both faces so their winding doesn't matter.
            var hitBackfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            var hits = Physics.RaycastAll(origin, Vector3.down, probeLength, 1 << layer, QueryTriggerInteraction.Ignore);
            Physics.queriesHitBackfaces = hitBackfaces;
            var best = float.NegativeInfinity;
            foreach (var hit in hits)
            {
                if (Vector3.Dot(hit.collider.transform.up, Vector3.up) < minUpDot)
                    continue; // wall or ceiling
                if (hit.point.y > best)
                    best = hit.point.y; // highest upward surface under the mat = the table, not the floor
            }

            if (float.IsNegativeInfinity(best))
                return false;
            height = best;
            return true;
        }

        /// <summary>Signed angle (degrees) of the twist component of <paramref name="q"/> about world up.</summary>
        static float TwistAboutUp(Quaternion q)
        {
            if (Mathf.Abs(q.y) < 1e-6f && Mathf.Abs(q.w) < 1e-6f)
                return 0f; // pure 180-degree swing; twist undefined
            return 2f * Mathf.Atan2(q.y, q.w) * Mathf.Rad2Deg;
        }
    }
}
