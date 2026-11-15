// =====================================================================================================
//  DEBUG ONLY. THIS IS NOT A CLASSIFIER. IT DOES NO RECOGNITION.
//  It reports FAKE detections at fixed image points so the Quest camera -> table placement pipeline can be
//  tested on the headset before Harley's real classifier lands. Setup (Setup_CameraPlacement, step 520)
//  removes it automatically as soon as any other PieceClassifierBehaviour is in the scene, and PieceLocator
//  always prefers a non-stub classifier.
// =====================================================================================================
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Forge
{
    /// <summary>
    /// Saves clean passthrough camera PNGs in a new folder on every launch.
    /// The existing synthetic detections drive the debug highlights.
    /// </summary>
    [AddComponentMenu("Forge/DEBUG Classifier Stub (fake detections, not recognition)")]
    public class DebugClassifierStub : PieceClassifierBehaviour
    {
        [Serializable]
        public struct FakeDetection
        {
            public string partId;
            [Tooltip("Normalised image point: (0,0) top-left, (1,1) bottom-right.")]
            public Vector2 imagePoint;
            [Range(0f, 1f)] public float confidence;
        }

        [SerializeField] FakeDetection[] fakeDetections =
        {
            new() { partId = PartIds.Torso, imagePoint = new Vector2(0.5f, 0.5f), confidence = 1f },
        };

        [SerializeField] PokeButton saveButton;

        const float CaptureTimeoutSeconds = 2f;
        readonly string captureSession = System.Guid.NewGuid().ToString("N");
        float captureDeadline;
        bool capturePending;
        int dumps;
        Texture2D dumpTexture;
        Color32[] dumpPixels;

        void Awake()
        {
            Debug.LogWarning("[Forge] DEBUG CLASSIFIER STUB ACTIVE: reporting FAKE detections " +
                             $"({fakeDetections.Length}) at fixed image points. This is not recognition.");
        }

        void OnEnable()
        {
            if (saveButton != null) saveButton.onPressed.AddListener(RequestCapture);
        }

        void OnDisable()
        {
            if (saveButton != null) saveButton.onPressed.RemoveListener(RequestCapture);
            capturePending = false;
        }

        void RequestCapture()
        {
            if (capturePending) return;
            capturePending = true;
            captureDeadline = Time.unscaledTime + CaptureTimeoutSeconds;
            saveButton.SetLabel("Saving…");
        }

        void Update()
        {
            if (!capturePending || Time.unscaledTime < captureDeadline) return;
            capturePending = false;
            saveButton.SetLabel("Retry save");
            Debug.LogWarning("[Forge] Capture timed out waiting for a camera frame");
        }

        void OnDestroy()
        {
            if (dumpTexture != null) Destroy(dumpTexture);
        }

        public override void Classify(CameraFrame frame, List<PieceDetection> results)
        {
            results.Clear();
            foreach (var f in fakeDetections)
                results.Add(new PieceDetection { partId = f.partId, imagePoint = f.imagePoint, confidence = f.confidence });

            if (!capturePending || frame?.pixels == null || Time.unscaledTime >= captureDeadline) return;
            capturePending = false;
            try
            {
                DumpFrame(frame);
                saveButton.SetLabel($"Saved {dumps}");
            }
            catch (IOException error)
            {
                saveButton.SetLabel("Retry save");
                Debug.LogError($"[Forge] Could not save capture: {error.Message}");
            }
        }

        void DumpFrame(CameraFrame frame)
        {
            const int size = CameraFrameSource.CaptureSize;
            if (dumpTexture == null)
            {
                dumpTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
                dumpPixels = new Color32[size * size];
            }

            // CameraFrame uses a top-left origin. Flip rows for Unity's texture.
            for (int y = 0; y < size; y++)
            {
                int sourceRow = y * frame.height / size * frame.width;
                int targetRow = (size - 1 - y) * size;
                if (frame.width == size)
                {
                    Array.Copy(frame.pixels, sourceRow, dumpPixels, targetRow, size);
                    continue;
                }

                // The native camera can supply a smaller image on some configurations.
                for (int x = 0; x < size; x++)
                    dumpPixels[targetRow + x] = frame.pixels[sourceRow + x * frame.width / size];
            }

            dumpTexture.SetPixels32(dumpPixels);
            dumpTexture.Apply(false);
            var dir = Path.Combine(Application.persistentDataPath, "forge_frames", captureSession);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"frame_{frame.frameId:D5}.png");
            File.WriteAllBytes(path, dumpTexture.EncodeToPNG());
            dumps++;
            Debug.Log($"[Forge] DEBUG stub dumped {path}");
        }
    }
}
