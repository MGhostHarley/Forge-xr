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

        public enum Mode
        {
            /// <summary>Fake detections at fixed image points (they follow the wearer's gaze).</summary>
            FixedImagePoints,
            /// <summary>Touch the table with an index fingertip: a fake piece stays at that table point
            /// (projected into each frame), so the highlight and cue stay put. Touch again to move it.</summary>
            TouchToPlace,
        }

        [SerializeField] Mode mode = Mode.TouchToPlace;
        [Tooltip("TouchToPlace: part placed when nothing is highlighted (otherwise the next highlighted part).")]
        [SerializeField] string touchPartId = PartIds.Torso;
        [Tooltip("TouchToPlace: fingertip counts as touching when within this height above the table (m).")]
        [SerializeField] float touchHeight = 0.02f;
        [Tooltip("TouchToPlace: fingertip must stay touching this long (s) before the fake piece moves.")]
        [SerializeField] float touchHoldSeconds = 0.4f;

        [SerializeField] FakeDetection[] fakeDetections =
        {
            new() { partId = PartIds.Torso, imagePoint = new Vector2(0.5f, 0.5f), confidence = 1f },
        };

        CameraFrameSource frameSource;
        TableSurface table;
        // One fake piece per part id, placed by fingertip touches (pieces stay where they were "put").
        readonly Dictionary<string, Vector3> anchors = new();
        float touchSince = -1f;
        bool placedThisTouch;
        Vector3 touchPoint;

        void Update()
        {
            if (mode != Mode.TouchToPlace) return;
            if (table == null) table = FindAnyObjectByType<TableSurface>();
            if (table == null) return;
            var tableY = table.TableHeight;

            var touching = false;
            foreach (var tip in ForgeHands.IndexTipPositions)
            {
                if (tip.y - tableY > touchHeight) continue;
                touching = true;
                if (touchSince < 0f || (tip - touchPoint).sqrMagnitude > 0.03f * 0.03f)
                {
                    touchSince = Time.unscaledTime;
                    touchPoint = tip;
                }
                break;
            }
            if (!touching) { touchSince = -1f; placedThisTouch = false; return; }

            if (!placedThisTouch && Time.unscaledTime - touchSince >= touchHoldSeconds)
            {
                placedThisTouch = true; // one placement per touch; lift the finger to place the next part
                var p = new Vector3(touchPoint.x, tableY, touchPoint.z);
                var id = ChoosePartForTouch(p);
                anchors[id] = p;
                Debug.Log($"[Forge] DEBUG stub: fake '{id}' placed at {p:F3} by fingertip touch");
            }
        }

        /// <summary>
        /// The next currently highlighted part without a fake position yet; once all highlighted parts
        /// have one, the nearest highlighted part moves. With nothing highlighted, <see cref="touchPartId"/>.
        /// So step 1 of the driver: first touch = legs, second = torso; step 2: head; wheel: tire, rim, axle pin.
        /// </summary>
        string ChoosePartForTouch(Vector3 p)
        {
            string nearest = null;
            var best = float.PositiveInfinity;
            foreach (var id in HighlightRegistry.ActiveIds)
            {
                if (!anchors.TryGetValue(id, out var a)) return id;
                var d = (a - p).sqrMagnitude;
                if (d < best) { best = d; nearest = id; }
            }
            return nearest ?? touchPartId;
        }

        /// <summary>World point -> normalised image point using the frame's capture geometry (inverse of ImagePointToRay).</summary>
        static bool TryProject(in CaptureInfo info, Vector3 world, out Vector2 normalized)
        {
            normalized = default;
            var c = Quaternion.Inverse(info.cameraPose.rotation) * (world - info.cameraPose.position);
            if (c.z <= 0.01f) return false;
            var u = info.focalLength.x * (c.x / c.z) + info.principalPoint.x;
            var v = info.principalPoint.y - info.focalLength.y * (c.y / c.z);
            normalized = new Vector2(u / info.intrinsicsResolution.x, v / info.intrinsicsResolution.y);
            return normalized.x >= 0f && normalized.x <= 1f && normalized.y >= 0f && normalized.y <= 1f;
        }

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
            if (mode == Mode.TouchToPlace)
            {
                if (frameSource == null) frameSource = FindAnyObjectByType<CameraFrameSource>();
                if (frameSource != null && frameSource.HasCapture)
                    foreach (var kv in anchors)
                        if (TryProject(frameSource.LatestCapture, kv.Value, out var ip))
                            results.Add(new PieceDetection { partId = kv.Key, imagePoint = ip, confidence = 1f });
            }
            else
            {
                foreach (var f in fakeDetections)
                    results.Add(new PieceDetection { partId = f.partId, imagePoint = f.imagePoint, confidence = f.confidence });
            }

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
