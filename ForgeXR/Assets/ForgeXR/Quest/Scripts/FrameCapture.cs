using System;
using System.IO;
using UnityEngine;

namespace Forge
{
    /// <summary>
    /// "Save frame" pinch button: saves the next clean passthrough camera frame (exactly what the classifier sees,
    /// CameraFrameSource.CaptureSize square) as a PNG to
    /// Application.persistentDataPath/forge_frames/&lt;session&gt;/frame_NNNNN.png, a new session folder per launch.
    /// Listens to CameraFrameSource directly, so it works with any classifier (cloud, stub, Harley's).
    /// Capture logic adapted from Harley's quest-pinch-capture branch.
    /// Pull with: adb pull /sdcard/Android/data/com.forge.forgexr/files/forge_frames
    /// </summary>
    public class FrameCapture : MonoBehaviour
    {
        [SerializeField] CameraFrameSource frameSource;
        [SerializeField] PokeButton saveButton;
        [SerializeField] float captureTimeoutSeconds = 2f;

        readonly string session = Guid.NewGuid().ToString("N");
        bool pending;
        float deadline;
        int saved;
        Texture2D texture;
        Color32[] pixels;

        public void Configure(CameraFrameSource source, PokeButton button)
        {
            frameSource = source;
            saveButton = button;
        }

        void OnEnable()
        {
            if (frameSource == null) frameSource = FindAnyObjectByType<CameraFrameSource>();
            if (frameSource != null) frameSource.FrameReady += OnFrame;
            if (saveButton != null) saveButton.onPressed.AddListener(Request);
        }

        void OnDisable()
        {
            if (frameSource != null) frameSource.FrameReady -= OnFrame;
            if (saveButton != null) saveButton.onPressed.RemoveListener(Request);
            pending = false;
        }

        void OnDestroy()
        {
            if (texture != null) Destroy(texture);
        }

        /// <summary>Save the next camera frame (also callable from code).</summary>
        public void Request()
        {
            if (pending) return;
            pending = true;
            deadline = Time.unscaledTime + captureTimeoutSeconds;
            if (saveButton != null) saveButton.SetLabel("Saving…");
        }

        void Update()
        {
            if (!pending || Time.unscaledTime < deadline) return;
            pending = false;
            if (saveButton != null) saveButton.SetLabel("Retry save");
            Debug.LogWarning("[Forge] Capture timed out waiting for a camera frame");
        }

        void OnFrame(CameraFrame frame, CaptureInfo info)
        {
            if (!pending || frame?.pixels == null || Time.unscaledTime >= deadline) return;
            pending = false;
            try
            {
                var path = Save(frame);
                saved++;
                if (saveButton != null) saveButton.SetLabel($"Saved {saved}");
                Debug.Log($"[Forge] Capture saved {path}");
            }
            catch (IOException e)
            {
                if (saveButton != null) saveButton.SetLabel("Retry save");
                Debug.LogError($"[Forge] Could not save capture: {e.Message}");
            }
        }

        string Save(CameraFrame frame)
        {
            int w = frame.width, h = frame.height;
            if (texture == null || texture.width != w || texture.height != h)
            {
                if (texture != null) Destroy(texture);
                texture = new Texture2D(w, h, TextureFormat.RGBA32, false);
                pixels = new Color32[w * h];
            }
            // CameraFrame row 0 is the top; Texture2D row 0 is the bottom.
            for (var y = 0; y < h; y++) Array.Copy(frame.pixels, y * w, pixels, (h - 1 - y) * w, w);
            texture.SetPixels32(pixels);
            texture.Apply(false);

            var dir = Path.Combine(Application.persistentDataPath, "forge_frames", session);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"frame_{frame.frameId:D5}.png");
            File.WriteAllBytes(path, texture.EncodeToPNG());
            return path;
        }
    }
}
