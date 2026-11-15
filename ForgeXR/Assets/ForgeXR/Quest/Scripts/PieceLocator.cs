using System;
using System.Collections.Generic;
using UnityEngine;

namespace Forge
{
    /// <summary>
    /// Glue: CameraFrameSource frame -> PieceClassifierBehaviour.Classify -> image point to world ray (intrinsics + camera
    /// pose at capture) -> TableSurface hit -> PieceTracker. Uses the best detection per part id per frame.
    /// Prefers a real classifier over the DebugClassifierStub whenever both exist.
    /// Logs a one-line status every <see cref="statusLogInterval"/> seconds (logcat: [Forge] Placement).
    /// </summary>
    public class PieceLocator : MonoBehaviour
    {
        [SerializeField] CameraFrameSource frameSource;
        [Tooltip("Optional override; otherwise the scene's classifier is found (non-stub preferred).")]
        [SerializeField] PieceClassifierBehaviour classifier;
        [SerializeField] TableSurface table;
        [SerializeField] PieceTracker tracker;
        [Tooltip("Optional: learns colours from classifier boxes and follows pieces between answers.")]
        [SerializeField] ColorPieceTracker colorTracker;
        [SerializeField] float statusLogInterval = 2f;

        [Tooltip("Camera poses of recent frames, for detections that arrive late (slow/cloud classifiers).")]
        [SerializeField] int captureHistory = 256;

        readonly List<PieceDetection> results = new();
        readonly Dictionary<int, CaptureInfo> history = new();
        readonly Queue<int> historyOrder = new();
        readonly Dictionary<string, PieceDetection> best = new();
        int frames, detections, placed;
        string lastPlacement = "-";
        float nextStatus, nextClassifierSearch;

        void Awake()
        {
            if (frameSource == null) frameSource = GetComponent<CameraFrameSource>();
            if (table == null) table = GetComponent<TableSurface>();
            if (tracker == null) tracker = GetComponent<PieceTracker>();
            if (colorTracker == null) colorTracker = GetComponent<ColorPieceTracker>();
        }

        void OnEnable() { if (frameSource != null) frameSource.FrameReady += OnFrame; }
        void OnDisable() { if (frameSource != null) frameSource.FrameReady -= OnFrame; }

        PieceClassifierBehaviour ResolveClassifier()
        {
            var usable = classifier != null && classifier.isActiveAndEnabled;
            if (usable && classifier is not DebugClassifierStub) return classifier;
            if (usable && Time.unscaledTime < nextClassifierSearch) return classifier;

            nextClassifierSearch = Time.unscaledTime + 5f;
            PieceClassifierBehaviour stub = null;
            foreach (var c in FindObjectsByType<PieceClassifierBehaviour>(FindObjectsSortMode.None))
            {
                if (!c.isActiveAndEnabled) continue;
                if (c is DebugClassifierStub) { stub ??= c; continue; }
                if (classifier != c) Debug.Log($"[Forge] Using classifier {c.GetType().Name} on {c.name}");
                return classifier = c;
            }
            return classifier = stub;
        }

        void OnFrame(CameraFrame frame, CaptureInfo info)
        {
            frames++;
            history[frame.frameId] = info;
            historyOrder.Enqueue(frame.frameId);
            while (historyOrder.Count > Mathf.Max(8, captureHistory)) history.Remove(historyOrder.Dequeue());
            var c = ResolveClassifier();
            if (c == null || table == null || tracker == null) return;

            results.Clear();
            try { c.Classify(frame, results); }
            catch (Exception e) { Debug.LogException(e, c); return; }

            best.Clear();
            foreach (var d in results)
            {
                if (string.IsNullOrEmpty(d.partId)) continue;
                if (!best.TryGetValue(d.partId, out var b) || d.confidence > b.confidence) best[d.partId] = d;
            }

            foreach (var d in best.Values)
            {
                detections++;
                var source = info;
                if (d.frameId > 0 && d.frameId != frame.frameId && !history.TryGetValue(d.frameId, out source))
                    continue; // computed on a frame older than the pose history: can't place it reliably
                var ray = source.ImagePointToRay(d.imagePoint);
                if (!table.TryIntersect(ray, out var p)) continue;
                tracker.Report(d.partId, p, d.confidence);
                if (colorTracker != null && d.imageBox.width > 0f) colorTracker.Learn(d.partId, d.frameId > 0 ? d.frameId : frame.frameId, d.imageBox);
                placed++;
                lastPlacement = $"{d.partId}@{p:F3} img={d.imagePoint:F2} conf={d.confidence:F2} table={table.LastSource}";
            }

            if (c.LastCallWasAnswer)
            {
                seenThisAnswer.Clear();
                foreach (var id in best.Keys) seenThisAnswer.Add(id);
                var answerInfo = info;
                var af = c.LastAnswerFrameId;
                if (af <= 0 || af == frame.frameId || history.TryGetValue(af, out answerInfo))
                    tracker.OnClassifierAnswer(seenThisAnswer, answerInfo);
            }
        }

        readonly HashSet<string> seenThisAnswer = new();

        void Update()
        {
            if (statusLogInterval <= 0f || Time.unscaledTime < nextStatus) return;
            nextStatus = Time.unscaledTime + statusLogInterval;
            Debug.Log($"[Forge] Placement: camera='{(frameSource != null ? frameSource.Status : "none")}' frames={frames} " +
                      $"detections={detections} placed={placed} classifier={(classifier != null ? classifier.GetType().Name : "none")} " +
                      $"tableY={(table != null ? table.TableHeight.ToString("F3") : "-")} last={lastPlacement}" +
                      (colorTracker != null ? $" {colorTracker.Status}" : ""));
        }
    }
}
