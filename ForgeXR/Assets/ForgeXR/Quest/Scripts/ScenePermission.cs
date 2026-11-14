#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.ARFoundation;

namespace Forge
{
    /// <summary>
    /// Requests com.oculus.permission.USE_SCENE at runtime and enables the ARPlaneManager once it is granted
    /// (Meta's runtime returns no Space Setup planes without it). Follows the meta-openxr PermissionsCheck sample.
    /// If the permission is denied, planes stay off and TableAligner falls back to the pinch-release height.
    /// </summary>
    public class ScenePermission : MonoBehaviour
    {
        public const string ScenePermissionId = "com.oculus.permission.USE_SCENE";

        [Tooltip("Kept disabled in the scene; enabled here once USE_SCENE is granted.")]
        [SerializeField] ARPlaneManager planeManager;

        public UnityEvent onGranted = new();
        public UnityEvent onDenied = new();

        public bool IsGranted { get; private set; }

        void Start()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (Permission.HasUserAuthorizedPermission(ScenePermissionId))
            {
                Grant();
                return;
            }

            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => Grant();
            callbacks.PermissionDenied += _ => Deny();
            Debug.Log($"[Forge] Requesting {ScenePermissionId}");
            Permission.RequestUserPermission(ScenePermissionId, callbacks);
#else
            Grant();
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        // The permission may have been granted from system settings while the app was paused. Re-check only; no new prompt.
        void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus && !IsGranted && Permission.HasUserAuthorizedPermission(ScenePermissionId))
                Grant();
        }
#endif

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
            Debug.LogWarning($"[Forge] {ScenePermissionId} denied; no table planes, alignment uses the pinch-release height");
            onDenied.Invoke();
        }
    }
}
