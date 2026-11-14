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
    /// DEBUG ONLY, NOT RECOGNITION. Reports fixed fake detections (default: "torso" at the image centre) for every frame.
    /// With <see cref="dumpFrames"/> on, it also saves the frame as a PNG (with a crosshair at each fake point) to
    /// Application.persistentDataPath/forge_frames, so you can adb pull it and compare it with where the ring appeared.
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

        [Header("Frame dump (debug)")]
        [SerializeField] bool dumpFrames = false;
        [SerializeField] float dumpIntervalSeconds = 5f;
        [SerializeField] int maxDumps = 20;

        float nextDump;
        int dumps;
        Texture2D dumpTexture;
        Color32[] dumpPixels;

        void Awake()
        {
            Debug.LogWarning("[Forge] DEBUG CLASSIFIER STUB ACTIVE: reporting FAKE detections " +
                             $"({fakeDetections.Length}) at fixed image points. This is not recognition.");
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

            if (dumpFrames && dumps < maxDumps && Time.unscaledTime >= nextDump && frame?.pixels != null)
            {
                nextDump = Time.unscaledTime + dumpIntervalSeconds;
                DumpFrame(frame);
            }
        }

        void DumpFrame(CameraFrame frame)
        {
            int w = frame.width, h = frame.height;
            if (dumpTexture == null || dumpTexture.width != w || dumpTexture.height != h)
            {
                if (dumpTexture != null) Destroy(dumpTexture);
                dumpTexture = new Texture2D(w, h, TextureFormat.RGBA32, false);
                dumpPixels = new Color32[w * h];
            }

            // CameraFrame row 0 is the top; Texture2D row 0 is the bottom.
            for (var y = 0; y < h; y++)
                Array.Copy(frame.pixels, y * w, dumpPixels, (h - 1 - y) * w, w);

            var red = new Color32(255, 0, 0, 255);
            foreach (var f in fakeDetections)
            {
                var cx = Mathf.RoundToInt(f.imagePoint.x * (w - 1));
                var cy = h - 1 - Mathf.RoundToInt(f.imagePoint.y * (h - 1));
                for (var d = -6; d <= 6; d++)
                {
                    Plot(cx + d, cy, w, h, red);
                    Plot(cx, cy + d, w, h, red);
                }
            }

            dumpTexture.SetPixels32(dumpPixels);
            dumpTexture.Apply(false);
            var dir = Path.Combine(Application.persistentDataPath, "forge_frames");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"frame_{frame.frameId:D5}.png");
            File.WriteAllBytes(path, dumpTexture.EncodeToPNG());
            dumps++;
            Debug.Log($"[Forge] DEBUG stub dumped {path}");
        }

        void Plot(int x, int y, int w, int h, Color32 c)
        {
            if (x >= 0 && x < w && y >= 0 && y < h) dumpPixels[y * w + x] = c;
        }
    }
}
