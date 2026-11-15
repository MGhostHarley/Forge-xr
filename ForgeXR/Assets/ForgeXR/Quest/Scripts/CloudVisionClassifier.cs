using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace Forge
{
    /// <summary>
    /// Piece classifier that asks a vision model through the laptop relay (tools/vision-relay/relay.py).
    /// At most one request in flight: when idle (and at most every <see cref="minInterval"/> s) the current frame is
    /// JPEG-encoded and POSTed to {relayUrl}/detect. When the answer arrives (seconds later) its boxes are reported once,
    /// tagged with the frame they were computed on, so PieceLocator projects them with that frame's camera pose and
    /// PieceTracker holds them until the next answer.
    ///
    /// Relay URL: Application.persistentDataPath/forge_relay_url.txt if present (e.g. http://192.168.1.20:8787),
    /// else <see cref="relayUrl"/> (default http://127.0.0.1:8787, which works over USB after
    /// `adb reverse tcp:8787 tcp:8787`). The API key stays on the laptop.
    /// </summary>
    public class CloudVisionClassifier : PieceClassifierBehaviour
    {
        public const string UrlFileName = "forge_relay_url.txt";

        [SerializeField] string relayUrl = "http://127.0.0.1:8787";
        [Tooltip("Minimum seconds between requests (one request is in flight at most).")]
        [SerializeField] float minInterval = 2f;
        [Tooltip("Requests allowed in flight at once. With ~1.5 s answers and a 0.5 s interval, 3 gives a fresh answer "
                 + "about every 0.5 s. The newest answer wins; answers older than the last one shown are dropped.")]
        [SerializeField] int maxInFlight = 3;
        [Tooltip("Drop a stalled request after this long so detection keeps flowing (the API occasionally takes 40 s+).")]
        [SerializeField] int requestTimeoutSeconds = 8;
        [SerializeField, Range(30, 100)] int jpegQuality = 85;
        [Tooltip("Confidence reported for relay detections that carry none.")]
        [SerializeField, Range(0f, 1f)] float defaultConfidence = 0.8f;
        [SerializeField] float statusLogInterval = 5f;

        [Serializable] class RelayDetection { public string partId; public float x0, y0, x1, y1; public float confidence = -1f; }
        [Serializable] class RelayResponse { public RelayDetection[] detections; public float seconds; public string model; public string error; }

        readonly List<PieceDetection> pending = new();
        bool lastCallWasAnswer;
        int pendingFrameId, answerFrameId;
        public override bool LastCallWasAnswer => lastCallWasAnswer;
        public override int LastAnswerFrameId => answerFrameId;
        // Downsampled copies of recently sent frames, so ColorPieceTracker can learn colours from the exact frame.
        const int SnapshotSize = 320, SnapshotCount = 6; // >= maxInFlight + 1
        readonly Queue<(int id, Color32[] px, int w, int h)> snapshots = new();
        bool hasPending;
        int inFlight, latestShownFrame, staleDropped;
        float nextRequest, nextStatus;
        Texture2D tex;
        Color32[] flipped;
        int requests, answers, failures, lastCount;
        float lastLatency;
        string lastError = "-";

        public string RelayUrl => relayUrl;

        /// <summary>
        /// Part ids to ask the model for (sent as X-Parts); null or empty = every part. Set by QuestWalkthroughDriver to
        /// the active walkthrough's parts mid-build, so other walkthroughs' labels can't be confused in.
        /// </summary>
        public string[] RequestedParts { get; set; }

        /// <summary>If &gt; 0, every Nth request asks for all parts anyway (lets auto-select notice the other
        /// walkthrough's pieces); the others ask only for <see cref="RequestedParts"/>.</summary>
        public int ProbeAllEveryN { get; set; }

        void Start()
        {
            try
            {
                var path = Path.Combine(Application.persistentDataPath, UrlFileName);
                if (File.Exists(path))
                {
                    var url = File.ReadAllText(path).Trim();
                    if (url.Length > 0) relayUrl = url;
                }
            }
            catch (Exception e) { Debug.LogWarning($"[Forge] CloudVision: could not read {UrlFileName}: {e.Message}"); }
            relayUrl = relayUrl.TrimEnd('/');
            Debug.Log($"[Forge] CloudVision classifier using relay {relayUrl}");
        }

        void OnDestroy()
        {
            if (tex != null) Destroy(tex);
        }

        public override void Classify(CameraFrame frame, List<PieceDetection> results)
        {
            results.Clear();
            lastCallWasAnswer = hasPending;
            if (hasPending)
            {
                results.AddRange(pending);
                pending.Clear();
                hasPending = false;
                answerFrameId = pendingFrameId;
            }

            if (inFlight < Mathf.Max(1, maxInFlight) && Time.unscaledTime >= nextRequest && frame?.pixels != null && frame.width > 0)
            {
                nextRequest = Time.unscaledTime + minInterval;
                var jpg = Encode(frame);
                if (jpg != null)
                {
                    Snapshot(frame);
                    StartCoroutine(Send(jpg, frame.frameId));
                }
            }
        }

        /// <summary>Pixels (row 0 = top) of a frame this classifier sent, downsampled; false if no longer kept.</summary>
        public bool TryGetSentFrame(int frameId, out Color32[] pixels, out int width, out int height)
        {
            foreach (var s in snapshots)
                if (s.id == frameId) { pixels = s.px; width = s.w; height = s.h; return true; }
            pixels = null; width = height = 0;
            return false;
        }

        void Snapshot(CameraFrame frame)
        {
            int sw = Mathf.Min(SnapshotSize, frame.width), sh = Mathf.Min(SnapshotSize, frame.height);
            var px = snapshots.Count >= SnapshotCount ? snapshots.Dequeue().px : null;
            if (px == null || px.Length != sw * sh) px = new Color32[sw * sh];
            for (var y = 0; y < sh; y++)
            {
                var row = y * frame.height / sh * frame.width;
                for (var x = 0; x < sw; x++) px[y * sw + x] = frame.pixels[row + x * frame.width / sw];
            }
            snapshots.Enqueue((frame.frameId, px, sw, sh));
        }

        byte[] Encode(CameraFrame frame)
        {
            int w = frame.width, h = frame.height;
            if (tex == null || tex.width != w || tex.height != h)
            {
                if (tex != null) Destroy(tex);
                tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                flipped = new Color32[w * h];
            }
            // CameraFrame row 0 is the top; Texture2D row 0 is the bottom.
            for (var y = 0; y < h; y++) Array.Copy(frame.pixels, y * w, flipped, (h - 1 - y) * w, w);
            tex.SetPixels32(flipped);
            tex.Apply(false);
            return tex.EncodeToJPG(jpegQuality);
        }

        IEnumerator Send(byte[] jpg, int frameId)
        {
            inFlight++;
            requests++;
            var started = Time.unscaledTime;
            using var req = new UnityWebRequest(relayUrl + "/detect", UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(jpg) { contentType = "image/jpeg" },
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = requestTimeoutSeconds,
            };
            req.SetRequestHeader("X-Frame-Id", frameId.ToString());
            var probeAll = ProbeAllEveryN > 0 && requests % ProbeAllEveryN == 0;
            if (!probeAll && RequestedParts != null && RequestedParts.Length > 0)
                req.SetRequestHeader("X-Parts", string.Join(",", RequestedParts));
            yield return req.SendWebRequest();
            lastLatency = Time.unscaledTime - started;
            inFlight--;

            if (req.result != UnityWebRequest.Result.Success)
            {
                failures++;
                lastError = $"{req.result} {req.responseCode} {req.error}";
                Debug.LogWarning($"[Forge] CloudVision request failed ({lastLatency:F1}s): {lastError}");
                nextRequest = Time.unscaledTime + Mathf.Max(minInterval, 3f); // back off a little
                yield break;
            }

            RelayResponse resp;
            try { resp = JsonUtility.FromJson<RelayResponse>(req.downloadHandler.text); }
            catch (Exception e)
            {
                failures++;
                lastError = "bad JSON: " + e.Message;
                yield break;
            }
            if (!string.IsNullOrEmpty(resp?.error)) { failures++; lastError = resp.error; yield break; }

            answers++;
            // Overlapping requests can finish out of order: never replace a newer answer with an older one.
            if (frameId <= latestShownFrame) { staleDropped++; yield break; }
            latestShownFrame = frameId;
            pending.Clear();
            foreach (var d in resp?.detections ?? Array.Empty<RelayDetection>())
            {
                if (string.IsNullOrEmpty(d.partId)) continue;
                float x0 = Mathf.Clamp01(Mathf.Min(d.x0, d.x1)), x1 = Mathf.Clamp01(Mathf.Max(d.x0, d.x1));
                float y0 = Mathf.Clamp01(Mathf.Min(d.y0, d.y1)), y1 = Mathf.Clamp01(Mathf.Max(d.y0, d.y1));
                pending.Add(new PieceDetection
                {
                    partId = d.partId,
                    imagePoint = new Vector2((x0 + x1) * 0.5f, (y0 + y1) * 0.5f),
                    imageBox = Rect.MinMaxRect(x0, y0, x1, y1),
                    confidence = d.confidence >= 0f ? d.confidence : defaultConfidence,
                    frameId = frameId,
                });
            }
            lastCount = pending.Count;
            pendingFrameId = frameId;
            hasPending = true;
        }

        void Update()
        {
            if (statusLogInterval <= 0f || Time.unscaledTime < nextStatus) return;
            nextStatus = Time.unscaledTime + statusLogInterval;
            Debug.Log($"[Forge] CloudVision: relay={relayUrl} requests={requests} answers={answers} failures={failures} " +
                      $"inFlight={inFlight} stale={staleDropped} lastLatency={lastLatency:F1}s lastDetections={lastCount} lastError={lastError}");
        }
    }
}
