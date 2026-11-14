using System;
using UnityEngine;

namespace Forge
{
    /// <summary>
    /// Where the passthrough camera was, in world space, when a CPU image was captured.
    ///
    /// Extrinsics: Unity OpenXR: Meta 2.6.1 has no C# API for the camera pose. MetaOpenXRCameraSubsystem only gives
    /// images, intrinsics and timestamps, and the mono CPU image is the LEFT camera (MetaOpenXRCameraSubsystem.cs, descriptor comment).
    /// So the lens pose is read once from Android Camera2 CameraCharacteristics over JNI:
    ///   LENS_POSE_TRANSLATION (camera optical centre, metres) and LENS_POSE_ROTATION (quaternion x,y,z,w),
    ///   both in the Android sensor/device frame (right-handed: +X right, +Y up, +Z toward the wearer's back),
    ///   with Camera2's pinhole model x_cam = R * (x_device - t), camera axes +X right, +Y down, +Z along the optical axis.
    /// The left camera is picked by Meta's vendor key com.meta.extra_metadata.position (0 = left), else the camera with x < 0.
    /// If any of this fails, an approximate built-in head-to-left-camera pose is used (logged as a warning).
    ///
    /// Timing: head poses (Camera.main, the centre eye) are recorded every frame in a ring buffer. A CPU image's age is
    /// estimated as "Android clock now - image timestamp" (clock and units auto-detected), and the head pose at
    /// "now - age" is interpolated. cameraWorld = headWorld(t_capture) * headFromCamera.
    /// </summary>
    public class PassthroughCameraModel : MonoBehaviour
    {
        public enum LensPoseConvention
        {
            /// <summary>Android docs: R maps device to camera, so headFromCamera uses R inverse.</summary>
            AndroidDocs,
            /// <summary>Same with the quaternion not inverted (try this if highlights are mirrored in tilt).</summary>
            Transposed,
            /// <summary>Lens translation only; camera looks exactly along the head's forward axis.</summary>
            TranslationOnly,
        }

        [Header("Lens pose (read at startup; tweak on headset)")]
        [Tooltip("How the Camera2 lens rotation is turned into Unity axes. Switch at runtime if the highlight is far off.")]
        [SerializeField] LensPoseConvention convention = LensPoseConvention.AndroidDocs;
        [Tooltip("Extra head-local offset added to the camera position (m), for fine calibration.")]
        [SerializeField] Vector3 extraOffset = Vector3.zero;
        [Tooltip("Extra rotation applied to the camera orientation (degrees, head-local Euler).")]
        [SerializeField] Vector3 extraEuler = Vector3.zero;

        [Header("Fallback when Camera2 values are unavailable (approximate Quest 3 left RGB camera)")]
        [SerializeField] Vector3 fallbackPosition = new(-0.032f, -0.02f, 0.07f);
        [SerializeField] Vector3 fallbackEuler = Vector3.zero;

        [Header("Timing")]
        [Tooltip("Used when the image age cannot be measured (s).")]
        [SerializeField] float fallbackLatency = 0.05f;
        [Tooltip("Ages outside 0..this are treated as unmeasurable (s).")]
        [SerializeField] float maxPlausibleAge = 0.5f;
        [Tooltip("Shift applied to recorded head-pose times (s). Positive if head poses are predicted ahead of real time.")]
        [SerializeField] float headPoseTimeOffset = 0f;

        public bool HasLensPose { get; private set; }
        public string LensInfo { get; private set; } = "not read";
        /// <summary>Which clock gave a plausible image age on the last call: "boottime", "monotonic" or "fallback".</summary>
        public string ClockUsed { get; private set; } = "none";

        // Raw Camera2 values (device frame).
        Vector3 lensT;
        Quaternion lensQ = Quaternion.identity;
        int timestampSource = -1; // 0 = UNKNOWN (monotonic), 1 = REALTIME (boottime)

        const int HistorySize = 128;
        readonly double[] histTime = new double[HistorySize];
        readonly Vector3[] histPos = new Vector3[HistorySize];
        readonly Quaternion[] histRot = new Quaternion[HistorySize];
        int histCount, histHead;
        Camera cam;

#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaClass systemClock, javaSystem;
#endif

        void OnEnable() => Application.onBeforeRender += RecordHeadPose;
        void OnDisable() => Application.onBeforeRender -= RecordHeadPose;

        void Start()
        {
            ReadLensPose();
        }

        void OnDestroy()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            systemClock?.Dispose();
            javaSystem?.Dispose();
#endif
        }

        // ------------------------------------------------------------------ head pose history

        Transform Head()
        {
            if (cam == null || !cam.isActiveAndEnabled) cam = Camera.main;
            return cam != null ? cam.transform : null;
        }

        void RecordHeadPose()
        {
            var head = Head();
            if (head == null) return;
            histHead = (histHead + 1) % HistorySize;
            histTime[histHead] = Time.realtimeSinceStartupAsDouble + headPoseTimeOffset;
            histPos[histHead] = head.position;
            histRot[histHead] = head.rotation;
            if (histCount < HistorySize) histCount++;
        }

        bool TryGetHeadPoseAt(double t, out Pose pose)
        {
            pose = default;
            if (histCount == 0)
            {
                var head = Head();
                if (head == null) return false;
                pose = new Pose(head.position, head.rotation);
                return true;
            }

            // Walk back from the newest sample.
            var newer = histHead;
            if (t >= histTime[newer]) { pose = new Pose(histPos[newer], histRot[newer]); return true; }
            for (var i = 1; i < histCount; i++)
            {
                var older = (histHead - i + HistorySize) % HistorySize;
                if (histTime[older] <= t)
                {
                    var span = histTime[newer] - histTime[older];
                    var a = span > 1e-6 ? (float)((t - histTime[older]) / span) : 0f;
                    pose = new Pose(Vector3.Lerp(histPos[older], histPos[newer], a),
                        Quaternion.Slerp(histRot[older], histRot[newer], a));
                    return true;
                }
                newer = older;
            }
            pose = new Pose(histPos[newer], histRot[newer]); // older than the buffer: oldest sample
            return true;
        }

        // ------------------------------------------------------------------ camera pose

        /// <summary>
        /// World pose of the passthrough camera when the image with <paramref name="imageTimestamp"/> was captured.
        /// The pose's +Z is the optical axis and +Y is "up" in the image.
        /// </summary>
        public bool TryGetCameraPose(double imageTimestamp, out Pose cameraPose, out double ageSeconds)
        {
            ageSeconds = EstimateImageAge(imageTimestamp);
            cameraPose = default;
            if (!TryGetHeadPoseAt(Time.realtimeSinceStartupAsDouble - ageSeconds, out var head)) return false;
            var local = HeadFromCamera();
            cameraPose = new Pose(head.position + head.rotation * local.position, head.rotation * local.rotation);
            return true;
        }

        /// <summary>Camera pose in head-local Unity coordinates (+Z forward, +Y up).</summary>
        public Pose HeadFromCamera()
        {
            Vector3 pos;
            Quaternion rot;
            if (!HasLensPose)
            {
                pos = fallbackPosition;
                rot = Quaternion.Euler(fallbackEuler);
            }
            else
            {
                // Device frame (RH, +Z back) -> Unity head frame (LH, +Z forward): negate z.
                pos = FlipZ(lensT);
                if (convention == LensPoseConvention.TranslationOnly)
                {
                    rot = Quaternion.identity;
                }
                else
                {
                    // Quaternion * vector is the same algebra in either handedness, so rotate raw triples and flip after.
                    var deviceFromCam = convention == LensPoseConvention.AndroidDocs ? Quaternion.Inverse(lensQ) : lensQ;
                    var fwd = FlipZ(deviceFromCam * new Vector3(0f, 0f, 1f));  // camera +Z (optical axis)
                    var up = FlipZ(deviceFromCam * new Vector3(0f, -1f, 0f));  // camera -Y (image up)
                    rot = fwd.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(fwd, up) : Quaternion.identity;
                }
            }
            return new Pose(pos + extraOffset, rot * Quaternion.Euler(extraEuler));
        }

        static Vector3 FlipZ(Vector3 v) => new(v.x, v.y, -v.z);

        // ------------------------------------------------------------------ timing

        double EstimateImageAge(double ts)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                systemClock ??= new AndroidJavaClass("android.os.SystemClock");
                javaSystem ??= new AndroidJavaClass("java.lang.System");
                // Seconds since boot are < 1e7 for ~115 days; anything larger is nanoseconds.
                var tsSec = ts > 1e7 ? ts * 1e-9 : ts;
                var boot = systemClock.CallStatic<long>("elapsedRealtimeNanos") * 1e-9;
                var mono = javaSystem.CallStatic<long>("nanoTime") * 1e-9;
                var bootAge = boot - tsSec;
                var monoAge = mono - tsSec;
                var preferBoot = timestampSource == 1;
                double first = preferBoot ? bootAge : monoAge, second = preferBoot ? monoAge : bootAge;
                if (first >= 0 && first < maxPlausibleAge) { ClockUsed = preferBoot ? "boottime" : "monotonic"; return first; }
                if (second >= 0 && second < maxPlausibleAge) { ClockUsed = preferBoot ? "monotonic" : "boottime"; return second; }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Forge] Camera clock query failed: {e.Message}");
            }
#endif
            ClockUsed = "fallback";
            return fallbackLatency;
        }

        // ------------------------------------------------------------------ Camera2 lens pose (JNI)

        void ReadLensPose()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                HasLensPose = TryReadLensPoseJni();
            }
            catch (Exception e)
            {
                HasLensPose = false;
                LensInfo = "JNI error: " + e.Message;
            }
#else
            LensInfo = "editor: using fallback pose";
#endif
            if (HasLensPose)
                Debug.Log($"[Forge] Passthrough lens pose: {LensInfo}");
            else
                Debug.LogWarning($"[Forge] Passthrough lens pose unavailable ({LensInfo}); using approximate fallback {fallbackPosition} / {fallbackEuler}");
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        struct Candidate
        {
            public string id;
            public float[] t, q, intr;
            public int position, source, reference, tsSource;
        }

        bool TryReadLensPoseJni()
        {
            using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            using var manager = activity.Call<AndroidJavaObject>("getSystemService", "camera");
            if (manager == null) { LensInfo = "no CameraManager"; return false; }

            var ids = manager.Call<string[]>("getCameraIdList");
            if (ids == null || ids.Length == 0) { LensInfo = "no cameras"; return false; }

            using var cc = new AndroidJavaClass("android.hardware.camera2.CameraCharacteristics");
            using var keyT = cc.GetStatic<AndroidJavaObject>("LENS_POSE_TRANSLATION");
            using var keyQ = cc.GetStatic<AndroidJavaObject>("LENS_POSE_ROTATION");
            using var keyRef = cc.GetStatic<AndroidJavaObject>("LENS_POSE_REFERENCE");
            using var keyIntr = cc.GetStatic<AndroidJavaObject>("LENS_INTRINSIC_CALIBRATION");
            using var keyTs = cc.GetStatic<AndroidJavaObject>("SENSOR_INFO_TIMESTAMP_SOURCE");
            using var keyPos = MakeVendorIntKey("com.meta.extra_metadata.position");
            using var keySrc = MakeVendorIntKey("com.meta.extra_metadata.camera_source");

            var log = new System.Text.StringBuilder();
            Candidate? chosen = null, leftmost = null;
            foreach (var id in ids)
            {
                using var chars = manager.Call<AndroidJavaObject>("getCameraCharacteristics", id);
                if (chars == null) continue;
                var c = new Candidate
                {
                    id = id,
                    t = GetFloats(chars, keyT),
                    q = GetFloats(chars, keyQ),
                    intr = GetFloats(chars, keyIntr),
                    reference = GetInt(chars, keyRef),
                    tsSource = GetInt(chars, keyTs),
                    position = GetInt(chars, keyPos),
                    source = GetInt(chars, keySrc),
                };
                log.Append($"[cam {id} pos={c.position} src={c.source} ref={c.reference} tsSrc={c.tsSource} " +
                           $"t={Fmt(c.t)} q={Fmt(c.q)} K={Fmt(c.intr)}] ");
                if (c.t == null || c.t.Length < 3 || c.q == null || c.q.Length < 4) continue;
                if (c.source > 0) continue; // 0 = passthrough RGB camera when the key exists
                if (c.position == 0 && chosen == null) chosen = c;
                if (leftmost == null || c.t[0] < leftmost.Value.t[0]) leftmost = c;
            }

            var pick = chosen ?? (leftmost.HasValue && leftmost.Value.t[0] < 0f ? leftmost : null);
            if (pick == null) { LensInfo = "no left camera with a lens pose. " + log; return false; }
            var p = pick.Value;

            // PRIMARY_CAMERA reference with a zero translation means "relative to itself": no head offset available.
            if (p.reference == 0 && new Vector3(p.t[0], p.t[1], p.t[2]).sqrMagnitude < 1e-6f)
            {
                LensInfo = $"camera {p.id} pose is relative to the primary camera. " + log;
                return false;
            }

            lensT = new Vector3(p.t[0], p.t[1], p.t[2]);
            lensQ = new Quaternion(p.q[0], p.q[1], p.q[2], p.q[3]);
            timestampSource = p.tsSource;
            HasLensPose = true; // so the log below shows the pose actually used, not the fallback
            LensInfo = $"using camera {p.id} t={lensT:F4} q={lensQ:F4} headFromCamera={HeadFromCamera().position:F3}/{HeadFromCamera().rotation.eulerAngles:F1}. All: {log}";
            return true;
        }

        static AndroidJavaObject MakeVendorIntKey(string name)
        {
            try
            {
                using var classClass = new AndroidJavaClass("java.lang.Class");
                using var integerClass = classClass.CallStatic<AndroidJavaObject>("forName", "java.lang.Integer");
                return new AndroidJavaObject("android.hardware.camera2.CameraCharacteristics$Key", name, integerClass);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Forge] Could not create vendor key {name}: {e.Message}");
                return null;
            }
        }

        static float[] GetFloats(AndroidJavaObject chars, AndroidJavaObject key)
        {
            if (key == null) return null;
            try
            {
                using var v = chars.Call<AndroidJavaObject>("get", key);
                return v == null ? null : AndroidJNIHelper.ConvertFromJNIArray<float[]>(v.GetRawObject());
            }
            catch (Exception) { return null; }
        }

        static int GetInt(AndroidJavaObject chars, AndroidJavaObject key)
        {
            if (key == null) return -1;
            try
            {
                using var v = chars.Call<AndroidJavaObject>("get", key);
                return v == null ? -1 : v.Call<int>("intValue");
            }
            catch (Exception) { return -1; }
        }

        static string Fmt(float[] a) => a == null ? "null" : "(" + string.Join(",", Array.ConvertAll(a, f => f.ToString("F4"))) + ")";
#endif
    }
}
