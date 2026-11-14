using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace Forge
{
    /// <summary>
    /// Places the staging mat on the real table, then locks it.
    /// Aligning: the operator pinch-grabs the mat (XRGrabInteractable + <see cref="MatPlanarGrabTransformer"/>:
    /// horizontal slide and yaw only, height from the table plane or the pinch-release height) and pokes Lock.
    /// Locked: the mat is parented to an ARAnchor, alignment visuals are hidden, and the same button reads Realign.
    /// Controller backup: grip grabs (XRI), A locks on release, holding B for 1 s re-aligns.
    /// Other components must treat "no TableAligner in the scene" as already locked (see IsSceneLocked),
    /// so chunks 2 and 4 work without chunk 3.
    /// </summary>
    public class TableAligner : MonoBehaviour
    {
        [Tooltip("The StagingMat transform that gets moved onto the table.")]
        [SerializeField] protected Transform mat;

        [Header("Wired by Setup_TableAlign")]
        [SerializeField] ARAnchorManager anchorManager;
        [SerializeField] XRGrabInteractable matGrab;
        [SerializeField] MatPlanarGrabTransformer planarTransformer;
        [Tooltip("Poke button on the operator (+Z) edge of the mat: Lock while aligning, Realign once locked.")]
        [SerializeField] PokeButton alignButton;
        [Tooltip("Everything under this object (mat outline, grab surface) is shown only while aligning.")]
        [SerializeField] GameObject alignOnlyVisuals;

        [Header("Tuning")]
        [SerializeField] string tablePlaneLayer = "TablePlane";
        [Tooltip("Poke presses on the align button are ignored this long after a state change (s).")]
        [SerializeField] float buttonCooldown = 1.0f;
        [Tooltip("Controller backup: hold B this long to re-align (s).")]
        [SerializeField] float realignHoldSeconds = 1.0f;
        [SerializeField] string lockLabel = "Lock";
        [SerializeField] string realignLabel = "Realign";

        public UnityEvent onAligned = new();
        public UnityEvent onRealign = new();

        public Transform Mat => mat;
        public bool IsLocked { get; protected set; }

        public void SetMat(Transform t) => mat = t;

        /// <summary>True if no TableAligner exists or the existing one is locked.</summary>
        public static bool IsSceneLocked()
        {
            var a = FindAnyObjectByType<TableAligner>();
            return a == null || a.IsLocked;
        }

        Transform m_MatParent;
        ARAnchor m_Anchor;
        int m_LockGeneration;
        float m_LastStateChange = float.NegativeInfinity;
        bool m_LockArmed;
        InputAction m_LockAction;
        InputAction m_RealignAction;

        void Awake()
        {
            if (mat != null) m_MatParent = mat.parent;

            // Controller backup. A is also chunk 4's Next (fires on press, ignored while unlocked),
            // so we lock on A *release*: the press that locks is never seen as Next.
            m_LockAction = new InputAction("ForgeAlignLock", InputActionType.Button, "<XRController>{RightHand}/{PrimaryButton}");
            m_RealignAction = new InputAction("ForgeAlignRealign", InputActionType.Button,
                "<XRController>{RightHand}/{SecondaryButton}",
                interactions: $"hold(duration={realignHoldSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
        }

        void OnEnable()
        {
            m_LockAction.performed += OnLockPressed;
            m_LockAction.canceled += OnLockReleased;
            m_RealignAction.performed += OnRealignHeld;
            m_LockAction.Enable();
            m_RealignAction.Enable();
            if (alignButton != null) alignButton.onPressed.AddListener(OnAlignButton);
            if (anchorManager != null) anchorManager.trackablesChanged.AddListener(OnAnchorsChanged);
        }

        void OnDisable()
        {
            m_LockAction.performed -= OnLockPressed;
            m_LockAction.canceled -= OnLockReleased;
            m_RealignAction.performed -= OnRealignHeld;
            m_LockAction.Disable();
            m_RealignAction.Disable();
            if (alignButton != null) alignButton.onPressed.RemoveListener(OnAlignButton);
            if (anchorManager != null) anchorManager.trackablesChanged.RemoveListener(OnAnchorsChanged);
        }

        void OnDestroy()
        {
            m_LockAction?.Dispose();
            m_RealignAction?.Dispose();
        }

        void Start()
        {
            if (mat == null)
            {
                Debug.LogError("[Forge] TableAligner has no mat; treating the scene as locked.", this);
                IsLocked = true;
                enabled = false;
                return;
            }
            EnterAligning();
        }

        // ---- input ----

        void OnAlignButton()
        {
            if (Time.unscaledTime - m_LastStateChange < buttonCooldown)
                return;
            if (IsLocked) Realign();
            else Lock();
        }

        void OnLockPressed(InputAction.CallbackContext _) => m_LockArmed = !IsLocked;

        void OnLockReleased(InputAction.CallbackContext _)
        {
            if (!m_LockArmed) return;
            m_LockArmed = false;
            if (!IsLocked) Lock();
        }

        void OnRealignHeld(InputAction.CallbackContext _)
        {
            if (IsLocked) Realign();
        }

        // ---- states ----

        void EnterAligning()
        {
            IsLocked = false;
            m_LockArmed = false;
            m_LastStateChange = Time.unscaledTime;
            if (matGrab != null) matGrab.enabled = true;
            SetAlignVisuals(true);
            if (alignButton != null) alignButton.SetLabel(lockLabel);
        }

        /// <summary>Freezes the mat where it is, anchors it, and hides the alignment visuals.</summary>
        void Lock()
        {
            if (IsLocked || mat == null) return;

            // Disabling the interactable cancels an ongoing grab; the mat keeps its last pose.
            if (matGrab != null) matGrab.enabled = false;

            // Planes may have refined since the last grab: snap to the plane under the mat if there is one.
            if (planarTransformer != null &&
                planarTransformer.TryGetPlaneHeight(mat.position, mat.position.y + 0.25f, out var planeY))
            {
                var p = mat.position;
                p.y = planeY;
                mat.position = p;
            }
            mat.rotation = Quaternion.Euler(0f, mat.eulerAngles.y, 0f);

            IsLocked = true;
            m_LastStateChange = Time.unscaledTime;
            SetAlignVisuals(false);
            if (alignButton != null) alignButton.SetLabel(realignLabel);
            Debug.Log($"[Forge] Mat locked at {mat.position:F3}, yaw {mat.eulerAngles.y:F1}");

            CreateAnchor(++m_LockGeneration, new Pose(mat.position, mat.rotation));
            onAligned.Invoke();
        }

        void Realign()
        {
            if (!IsLocked || mat == null) return;
            m_LockGeneration++; // a pending anchor result is discarded

            // Unparent first: removing the anchor destroys its GameObject.
            if (mat.parent != m_MatParent)
                mat.SetParent(m_MatParent, true);
            mat.rotation = Quaternion.Euler(0f, mat.eulerAngles.y, 0f);
            RemoveAnchor(m_Anchor);
            m_Anchor = null;

            EnterAligning();
            Debug.Log("[Forge] Re-aligning mat");
            onRealign.Invoke();
        }

        // ---- anchor ----

        async void CreateAnchor(int generation, Pose pose)
        {
            if (anchorManager == null || !anchorManager.enabled || anchorManager.subsystem == null)
            {
                Debug.LogWarning("[Forge] No running ARAnchorManager; mat stays locked in world space without an anchor.", this);
                return;
            }

            try
            {
                var result = await anchorManager.TryAddAnchorAsync(pose);
                var anchor = result.status.IsSuccess() ? result.value : null;

                if (generation != m_LockGeneration || !IsLocked)
                {
                    RemoveAnchor(anchor); // re-aligned while the request was in flight
                    return;
                }

                if (anchor == null)
                {
                    Debug.LogWarning($"[Forge] TryAddAnchorAsync failed ({result.status}); mat stays locked in world space.", this);
                    return;
                }

                anchor.gameObject.name = "ForgeMatAnchor";
                m_Anchor = anchor;
                mat.SetParent(anchor.transform, true);
                Debug.Log("[Forge] Mat anchored");
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
        }

        // If the runtime drops our anchor, the manager destroys its GameObject right after this event.
        // Rescue the mat first so it stays locked in world space instead of being destroyed with it.
        void OnAnchorsChanged(ARTrackablesChangedEventArgs<ARAnchor> args)
        {
            if (m_Anchor == null) return;
            var removed = args.removed;
            for (var i = 0; i < removed.Count; i++)
            {
                if (removed[i].Value != m_Anchor) continue;
                if (mat != null && mat.parent == m_Anchor.transform)
                    mat.SetParent(m_MatParent, true);
                Debug.LogWarning("[Forge] Mat anchor was removed by the runtime; mat stays locked in world space.", this);
                m_Anchor = null;
                return;
            }
        }

        void RemoveAnchor(ARAnchor anchor)
        {
            if (anchor == null) return;
            if (anchor == m_Anchor && mat != null && mat.parent == anchor.transform)
                mat.SetParent(m_MatParent, true);
            if (anchorManager != null && anchorManager.enabled && anchorManager.TryRemoveAnchor(anchor))
                return;
            Destroy(anchor.gameObject);
        }

        // ---- visuals ----

        void SetAlignVisuals(bool aligning)
        {
            if (alignOnlyVisuals != null) alignOnlyVisuals.SetActive(aligning);

            // Plane boundary lines live on the TablePlane layer. ARPlaneMeshVisualizer re-enables renderers on every
            // plane update, so hide the whole layer from the camera instead of toggling renderers.
            var layer = LayerMask.NameToLayer(tablePlaneLayer);
            var cam = Camera.main;
            if (layer >= 0 && cam != null)
            {
                if (aligning) cam.cullingMask |= 1 << layer;
                else cam.cullingMask &= ~(1 << layer);
            }
        }
    }
}
