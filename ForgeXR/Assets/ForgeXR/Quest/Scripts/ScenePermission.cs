#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.ARFoundation;

namespace Forge
{
    /// <summary>
    /// Runtime permissions, requested together in one dialog batch (separate requests can drop one):
    ///   com.oculus.permission.USE_SCENE         -> enables ARPlaneManager (Space Setup table planes).
    ///   horizonos.permission.HEADSET_CAMERA     -> passthrough camera CPU images.
    /// MetaOpenXRCameraSubsystem checks HEADSET_CAMERA only when it starts (MetaOpenXRCameraSubsystem.cs, Provider.Start),
    /// so if it is granted while the app runs, the ARCameraManager is restarted (brief passthrough flicker, first launch only).
    /// Permission callbacks may arrive off the main thread, so they only set flags that Update applies.
    /// </summary>
    public class ScenePermission : MonoBehaviour
    {
        public const string ScenePermissionId = "com.oculus.permission.USE_SCENE";
        public const string CameraPermissionId = "horizonos.permission.HEADSET_CAMERA";

        [Tooltip("Kept disabled in the scene; enabled here once USE_SCENE is granted.")]
        [SerializeField] ARPlaneManager planeManager;
        [Tooltip("Restarted once HEADSET_CAMERA is granted during this session.")]
        [SerializeField] ARCameraManager cameraManager;

        public UnityEvent onGranted = new();
        public UnityEvent onDenied = new();
        public UnityEvent onCameraGranted = new();
        public UnityEvent onCameraDenied = new();

        public bool IsGranted { get; private set; }
        public bool IsCameraGranted { get; private set; }

        volatile bool sceneGrantedFlag, sceneDeniedFlag, cameraGrantedFlag, cameraDeniedFlag;
        bool cameraRestartNeeded;

        void Start()
        {
            if (cameraManager == null && Camera.main != null) cameraManager = Camera.main.GetComponent<ARCameraManager>();
#if UNITY_ANDROID && !UNITY_EDITOR
            var sceneOk = Permission.HasUserAuthorizedPermission(ScenePermissionId);
            var cameraOk = Permission.HasUserAuthorizedPermission(CameraPermissionId);
            if (sceneOk) sceneGrantedFlag = true;
            if (cameraOk) GrantCamera(restart: false); // granted in an earlier session: the subsystem already started with it

            var missing = new System.Collections.Generic.List<string>();
            if (!sceneOk) missing.Add(ScenePermissionId);
            if (!cameraOk) missing.Add(CameraPermissionId);
            if (missing.Count == 0) return;

            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += p => { if (p == ScenePermissionId) sceneGrantedFlag = true; else if (p == CameraPermissionId) cameraGrantedFlag = true; };
            callbacks.PermissionDenied += p => { if (p == ScenePermissionId) sceneDeniedFlag = true; else if (p == CameraPermissionId) cameraDeniedFlag = true; };
            Debug.Log($"[Forge] Requesting {string.Join(", ", missing)}");
            Permission.RequestUserPermissions(missing.ToArray(), callbacks);
#else
            sceneGrantedFlag = true;
            GrantCamera(restart: false);
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        // A permission may have been granted from system settings while the app was paused. Re-check only; no new prompt.
        void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus) return;
            if (!IsGranted && Permission.HasUserAuthorizedPermission(ScenePermissionId)) sceneGrantedFlag = true;
            if (!IsCameraGranted && Permission.HasUserAuthorizedPermission(CameraPermissionId)) cameraGrantedFlag = true;
        }
#endif

        void Update()
        {
            if (sceneGrantedFlag) { sceneGrantedFlag = false; Grant(); }
            if (sceneDeniedFlag) { sceneDeniedFlag = false; Deny(); }
            if (cameraGrantedFlag) { cameraGrantedFlag = false; GrantCamera(restart: true); }
            if (cameraDeniedFlag)
            {
                cameraDeniedFlag = false;
                Debug.LogWarning($"[Forge] {CameraPermissionId} denied; no camera frames, so no piece highlights");
                onCameraDenied.Invoke();
            }

            if (cameraRestartNeeded && cameraManager != null)
            {
                cameraRestartNeeded = false;
                // Stop + start the camera subsystem so Provider.Start re-checks the permission and initialises the camera.
                Debug.Log("[Forge] Restarting ARCameraManager to pick up the camera permission");
                cameraManager.enabled = false;
                cameraManager.enabled = true;
            }
        }

        void Grant()
        {
            if (IsGranted) return;
            IsGranted = true;
            Debug.Log($"[Forge] {ScenePermissionId} granted; enabling plane detection");
            if (planeManager != null) planeManager.enabled = true;
            onGranted.Invoke();
        }

        void Deny()
        {
            Debug.LogWarning($"[Forge] {ScenePermissionId} denied; no table planes, table height comes from fingertips");
            onDenied.Invoke();
        }

        void GrantCamera(bool restart)
        {
            if (IsCameraGranted) return;
            IsCameraGranted = true;
            Debug.Log($"[Forge] {CameraPermissionId} granted{(restart ? "; camera restart scheduled" : "")}");
            if (restart) cameraRestartNeeded = true;
            onCameraGranted.Invoke();
        }
    }
}
