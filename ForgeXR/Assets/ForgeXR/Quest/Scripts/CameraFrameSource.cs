using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace Forge
{
    /// <summary>Camera geometry for one delivered frame: enough to turn an image point into a world ray.</summary>
    public struct CaptureInfo
    {
        /// <summary>World pose of the camera at capture. +Z = optical axis, +Y = image up.</summary>
        public Pose cameraPose;
        public Vector2 focalLength;      // pixels, at intrinsicsResolution
        public Vector2 principalPoint;   // pixels from the top-left, at intrinsicsResolution
        public Vector2Int intrinsicsResolution;
        public double imageAgeSeconds;

        /// <summary>World ray through a normalised image point ((0,0) = top-left, (1,1) = bottom-right).</summary>
        public Ray ImagePointToRay(Vector2 normalized)
        {
            var u = normalized.x * intrinsicsResolution.x;
            var v = normalized.y * intrinsicsResolution.y;
            // Pinhole camera with +Y down in the image; Unity camera axes have +Y up.
            var d = new Vector3((u - principalPoint.x) / focalLength.x, -(v - principalPoint.y) / focalLength.y, 1f);
            return new Ray(cameraPose.position, cameraPose.rotation * d.normalized);
        }

        /// <summary>World point -> normalised image point (inverse of ImagePointToRay). False if behind the camera.
        /// The result may lie outside 0..1 when the point is out of frame.</summary>
        public bool TryWorldToImage(Vector3 world, out Vector2 normalized)
        {
            normalized = default;
            if (intrinsicsResolution.x <= 0 || intrinsicsResolution.y <= 0) return false;
            var c = Quaternion.Inverse(cameraPose.rotation) * (world - cameraPose.position);
            if (c.z <= 0.01f) return false;
            var u = focalLength.x * (c.x / c.z) + principalPoint.x;
            var v = principalPoint.y - focalLength.y * (c.y / c.z);
            normalized = new Vector2(u / intrinsicsResolution.x, v / intrinsicsResolution.y);
            return true;
        }

        /// <summary>True if the world point is in front of the camera and inside the frame (with a margin, 0..0.5).</summary>
        public bool IsInView(Vector3 world, float margin = 0.08f)
        {
            return TryWorldToImage(world, out var n) &&
                   n.x >= margin && n.x <= 1f - margin && n.y >= margin && n.y <= 1f - margin;
        }
    }

    /// <summary>
    /// Pulls passthrough CPU images (left camera) from AR Foundation's ARCameraManager at a fixed low rate,
    /// downsamples them to small RGBA32 frames with an async conversion (off the main thread), stamps each with the
    /// camera pose at capture time, and raises <see cref="FrameReady"/>. Requires "Camera Image Support" on the
    /// Meta Quest Camera (Passthrough) feature and the horizonos.permission.HEADSET_CAMERA permission (ScenePermission).
    /// In the editor (no camera images) it can emit blank synthetic frames from Camera.main so the pipeline runs.
    /// </summary>
    public class CameraFrameSource : MonoBehaviour
    {
        [SerializeField] ARCameraManager cameraManager;
        [SerializeField] PassthroughCameraModel cameraModel;

        [Header("Frames handed to the classifier")]
        public const int CaptureSize = 960;
        [SerializeField] float framesPerSecond = 10f;
        [Tooltip("Drop an async conversion that has not finished after this long (s).")]
        [SerializeField] float conversionTimeout = 1f;
        [Tooltip("Editor only: emit blank frames posed like Camera.main when no camera images exist.")]
        [SerializeField] bool editorSyntheticFrames = true;

        /// <summary>The frame is reused; it is only valid during the callback.</summary>
        public event Action<CameraFrame, CaptureInfo> FrameReady;

        public int FramesDelivered { get; private set; }
        public string Status { get; private set; } = "starting";

        readonly CameraFrame frame = new();
        int nextFrameId = 1; // 0 is reserved: PieceDetection.frameId 0 means "the current frame"
        float nextCapture;
        double lastTimestamp = double.NaN;

        XRCpuImage.AsyncConversion pending;
        bool hasPending;
        float pendingStart;
        double pendingTimestamp;
        CaptureInfo pendingInfo;

        XRCameraIntrinsics intrinsics;
        bool hasIntrinsics;

        void Awake()
        {
            if (cameraManager == null && Camera.main != null) cameraManager = Camera.main.GetComponent<ARCameraManager>();
            if (cameraModel == null) cameraModel = GetComponent<PassthroughCameraModel>();
        }

        void OnDisable() => DisposePending();

        // ---- watchdog: the Meta camera can stop delivering CPU images after the app is paused (headset taken off)
        [Tooltip("Restart the camera when no new frame has been delivered for this long (s) while the app runs.")]
        [SerializeField] float stallRestartSeconds = 3f;
        float lastDeliveredAt = -1f, lastRestartAt = -100f;
        bool restartPending;
        int cameraRestarts;

        void OnApplicationPause(bool paused)
        {
            if (!paused) { lastDeliveredAt = Time.unscaledTime; RestartCamera("resumed"); }
        }

        void RestartCamera(string why)
        {
            if (cameraManager == null || restartPending || Time.unscaledTime - lastRestartAt < 2f) return;
            DisposePending();
            cameraManager.enabled = false; // re-enabled next frame so the subsystem stops and starts again
            restartPending = true;
            lastRestartAt = Time.unscaledTime;
            cameraRestarts++;
            Debug.Log($"[Forge] Restarting camera ({why}), restart #{cameraRestarts}");
        }

        void Update()
        {
            if (restartPending)
            {
                restartPending = false;
                if (cameraManager != null) cameraManager.enabled = true;
                lastDeliveredAt = Time.unscaledTime;
                return;
            }
            if (lastDeliveredAt >= 0f && stallRestartSeconds > 0f && Time.unscaledTime - lastDeliveredAt > stallRestartSeconds &&
                Time.unscaledTime - lastRestartAt > Mathf.Max(stallRestartSeconds, 5f))
                RestartCamera($"no frame for {Time.unscaledTime - lastDeliveredAt:F1}s: {Status}");

            if (hasPending)
            {
                PollPending();
                return;
            }
            if (Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + 1f / Mathf.Max(0.1f, framesPerSecond);

            if (TryStartCapture()) return;
#if UNITY_EDITOR
            if (editorSyntheticFrames) DeliverSyntheticFrame();
#endif
        }

        /// <summary>Returns false only when no camera image source exists at all.</summary>
        bool TryStartCapture()
        {
            if (cameraModel == null) { Status = "no PassthroughCameraModel"; return false; }
            if (cameraManager == null || !cameraManager.enabled || cameraManager.subsystem == null || !cameraManager.subsystem.running)
            {
                Status = "camera subsystem not running";
                return false;
            }

            if (cameraManager.TryGetIntrinsics(out var intr) && intr.focalLength.x > 0f)
            {
                intrinsics = intr;
                hasIntrinsics = true;
            }

            if (!cameraManager.TryAcquireLatestCpuImage(out var image))
            {
                Status = "no CPU image (HEADSET_CAMERA permission? Camera Image Support?)";
                return false;
            }

            using (image)
            {
                if (image.timestamp == lastTimestamp) { Status = "waiting for a new image"; return true; }
                if (!hasIntrinsics) { Status = "no camera intrinsics"; return true; }
                if (!image.FormatSupported(TextureFormat.RGBA32)) { Status = $"format {image.format} cannot convert to RGBA32"; return true; }
                if (!cameraModel.TryGetCameraPose(image.timestamp, out var pose, out var age)) { Status = "no head pose"; return true; }

                lastTimestamp = image.timestamp;
                int cropSize = Mathf.Min(image.width, image.height);
                var crop = new RectInt((image.width - cropSize) / 2, (image.height - cropSize) / 2, cropSize, cropSize);
                int outputSize = Mathf.Min(CaptureSize, cropSize);
                var conversion = new XRCpuImage.ConversionParams
                {
                    inputRect = crop,
                    outputDimensions = new Vector2Int(outputSize, outputSize),
                    outputFormat = TextureFormat.RGBA32,
                    transformation = XRCpuImage.Transformation.None, // row 0 = top of the image
                };

                // Express the camera intrinsics in the cropped output's pixel coordinates.
                var intrinsicsSize = intrinsics.resolution.x > 0 && intrinsics.resolution.y > 0
                    ? intrinsics.resolution
                    : image.dimensions;
                var toImagePixels = new Vector2((float)image.width / intrinsicsSize.x,
                    (float)image.height / intrinsicsSize.y);
                float outputScale = (float)outputSize / cropSize;
                pendingInfo = new CaptureInfo
                {
                    cameraPose = pose,
                    focalLength = Vector2.Scale(intrinsics.focalLength, toImagePixels) * outputScale,
                    principalPoint = (Vector2.Scale(intrinsics.principalPoint, toImagePixels) -
                        new Vector2(crop.x, crop.y)) * outputScale,
                    intrinsicsResolution = conversion.outputDimensions,
                    imageAgeSeconds = age,
                };
                pendingTimestamp = image.timestamp;
                pending = image.ConvertAsync(conversion);
                hasPending = true;
                pendingStart = Time.unscaledTime;

                if (FramesDelivered == 0 && nextFrameId == 1)
                    Debug.Log($"[Forge] First camera image {image.width}x{image.height} {image.format} ts={image.timestamp} " +
                              $"intrinsics f={intrinsics.focalLength} c={intrinsics.principalPoint} res={intrinsics.resolution} " +
                              $"age={age:F3}s clock={cameraModel.ClockUsed}");
            }
            return true;
        }

        void PollPending()
        {
            var status = pending.status;
            if (status == XRCpuImage.AsyncConversionStatus.Ready)
            {
                try
                {
                    var dims = pending.conversionParams.outputDimensions;
                    var data = pending.GetData<Color32>();
                    if (data.IsCreated && data.Length >= dims.x * dims.y)
                    {
                        FillFrame(dims.x, dims.y, pendingTimestamp);
                        NativeArray<Color32>.Copy(data, frame.pixels, dims.x * dims.y);
                        Deliver(pendingInfo);
                    }
                    else Status = "conversion returned no data";
                }
                finally { DisposePending(); }
            }
            else if (status == XRCpuImage.AsyncConversionStatus.Failed || status == XRCpuImage.AsyncConversionStatus.Disposed)
            {
                Status = $"conversion {status}";
                DisposePending();
            }
            else if (Time.unscaledTime - pendingStart > conversionTimeout)
            {
                Status = "conversion timed out";
                DisposePending();
            }
        }

        void DisposePending()
        {
            if (!hasPending) return;
            hasPending = false;
            pending.Dispose();
        }

        void FillFrame(int w, int h, double timestamp)
        {
            if (frame.pixels == null || frame.pixels.Length != w * h) frame.pixels = new Color32[w * h];
            frame.width = w;
            frame.height = h;
            frame.timestamp = timestamp;
            frame.frameId = nextFrameId++;
        }

        /// <summary>Camera geometry of the frame currently being delivered (valid during FrameReady/Classify).</summary>
        public CaptureInfo LatestCapture { get; private set; }
        public bool HasCapture { get; private set; }

        void Deliver(CaptureInfo info)
        {
            lastDeliveredAt = Time.unscaledTime;
            LatestCapture = info;
            HasCapture = true;
            FramesDelivered++;
            Status = $"ok ({FramesDelivered} frames, age {info.imageAgeSeconds * 1000:F0} ms)";
            try { FrameReady?.Invoke(frame, info); }
            catch (Exception e) { Debug.LogException(e, this); }
        }

#if UNITY_EDITOR
        void DeliverSyntheticFrame()
        {
            var cam = Camera.main;
            if (cam == null) return;
            const int w = CaptureSize, h = CaptureSize;
            FillFrame(w, h, Time.realtimeSinceStartupAsDouble);
            var grey = new Color32(96, 96, 96, 255);
            for (var i = 0; i < frame.pixels.Length; i++) frame.pixels[i] = grey;
            var fy = h * 0.5f / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            Deliver(new CaptureInfo
            {
                cameraPose = new Pose(cam.transform.position, cam.transform.rotation),
                focalLength = new Vector2(fy, fy),
                principalPoint = new Vector2(w * 0.5f, h * 0.5f),
                intrinsicsResolution = new Vector2Int(w, h),
            });
            Status = "editor synthetic frames";
        }
#endif
    }
}
