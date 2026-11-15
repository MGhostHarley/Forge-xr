using System;
using System.Collections.Generic;
using UnityEngine;

namespace Forge
{
    /// <summary>
    /// Follows pieces between slow classifier answers using colour, on every camera frame (~5 Hz).
    ///
    /// Learn: when a classifier detection with a box arrives (PieceLocator calls <see cref="Learn"/>), build a
    /// hue/saturation histogram of the box versus a ring of background around it, from the exact frame the
    /// classifier saw (CloudVisionClassifier keeps a snapshot of the frame it sent). The ratio target/background is the
    /// piece's colour model, so the table colour is suppressed.
    ///
    /// Track: each frame, project the piece's tracked world position into the image, run mean-shift over the colour
    /// model in a window around it, and report the new table point to PieceTracker at <see cref="reportConfidence"/>.
    /// The classifier stays the source of truth: every new detection re-learns the model and corrects drift; a piece whose
    /// colour match fades below <see cref="minMatch"/> of its learned strength is no longer tracked by colour.
    /// </summary>
    public class ColorPieceTracker : MonoBehaviour
    {
        [SerializeField] PieceTracker tracker;
        [SerializeField] TableSurface table;
        [SerializeField] CloudVisionClassifier cloud;
        [Tooltip("Confidence of colour-tracking updates (must be >= PieceTracker's minimum).")]
        [SerializeField, Range(0.5f, 1f)] float reportConfidence = 0.6f;
        [Tooltip("Search window = box size x this.")]
        [SerializeField] float searchScale = 2.5f;
        [Tooltip("Stop colour-tracking a piece when its match falls below this fraction of the learned match.")]
        [SerializeField, Range(0.05f, 1f)] float minMatch = 0.35f;
        [Tooltip("Forget colour models older than this (s) without a fresh classifier detection.")]
        [SerializeField] float modelLifetime = 120f;
        [SerializeField] int meanShiftIterations = 6;
        [Tooltip("Pixel stride while sampling (1 = every pixel).")]
        [SerializeField] int stride = 2;

        const int HueBins = 18, SatBins = 3, GreyBins = 3;
        const int Bins = 1 + GreyBins + HueBins * SatBins; // dark + greys + hue x sat

        class Model
        {
            public float[] weight = new float[Bins]; // target / background ratio, 0..1
            public Vector2 boxSize;                  // normalised
            public float learnedMatch;               // mean weight inside the box at learn time
            public float learnedAt;
            public bool active;
        }

        readonly Dictionary<string, Model> models = new();
        readonly float[] fg = new float[Bins], bg = new float[Bins];
        int updates, lost;

        void Awake()
        {
            if (tracker == null) tracker = GetComponent<PieceTracker>();
            if (table == null) table = GetComponent<TableSurface>();
        }

        void OnEnable()
        {
            var source = GetComponent<CameraFrameSource>() ?? FindAnyObjectByType<CameraFrameSource>();
            if (source != null) source.FrameReady += OnFrame;
        }

        void OnDisable()
        {
            var source = GetComponent<CameraFrameSource>() ?? FindAnyObjectByType<CameraFrameSource>();
            if (source != null) source.FrameReady -= OnFrame;
        }

        public string Status => $"colour models={models.Count} updates={updates} lost={lost}";

        // ------------------------------------------------------------------ learn

        /// <summary>Learn a piece's colour model from the frame the detection came from.</summary>
        public void Learn(string partId, int frameId, Rect box)
        {
            if (string.IsNullOrEmpty(partId) || box.width <= 0f || box.height <= 0f) return;
            if (cloud == null) cloud = FindAnyObjectByType<CloudVisionClassifier>();
            if (cloud == null || !cloud.TryGetSentFrame(frameId, out var px, out var w, out var h)) return;

            Array.Clear(fg, 0, Bins);
            Array.Clear(bg, 0, Bins);
            var inner = ToPixels(box, w, h);
            var outer = ToPixels(Expand(box, 2f), w, h);
            float nf = 0, nb = 0;
            for (var y = outer.yMin; y < outer.yMax; y += stride)
            for (var x = outer.xMin; x < outer.xMax; x += stride)
            {
                var b = Bin(px[y * w + x]);
                if (inner.Contains(new Vector2Int(x, y))) { fg[b]++; nf++; }
                else { bg[b]++; nb++; }
            }
            if (nf < 4 || nb < 4) return;

            if (!models.TryGetValue(partId, out var m)) models[partId] = m = new Model();
            var maxRatio = 0f;
            for (var i = 0; i < Bins; i++)
            {
                var pf = fg[i] / nf;
                var pb = bg[i] / nb;
                m.weight[i] = pf / (pb + 0.02f);
                maxRatio = Mathf.Max(maxRatio, m.weight[i]);
            }
            if (maxRatio <= 0f) return;
            for (var i = 0; i < Bins; i++) m.weight[i] /= maxRatio;

            m.boxSize = box.size;
            m.learnedMatch = MeanWeight(m, px, w, h, inner);
            m.learnedAt = Time.unscaledTime;
            m.active = m.learnedMatch > 0.05f;
        }

        // ------------------------------------------------------------------ track

        void OnFrame(CameraFrame frame, CaptureInfo info)
        {
            if (tracker == null || table == null || frame?.pixels == null || models.Count == 0) return;
            int w = frame.width, h = frame.height;
            foreach (var kv in models)
            {
                var m = kv.Value;
                if (!m.active) continue;
                if (Time.unscaledTime - m.learnedAt > modelLifetime) { m.active = false; continue; }
                if (!tracker.TryGetPosition(kv.Key, out var world)) continue;
                if (!info.TryWorldToImage(world, out var center)) continue;
                if (center.x < 0f || center.x > 1f || center.y < 0f || center.y > 1f) continue; // out of frame: hold

                // Mean-shift: move a box-sized window to the colour-weighted centroid, within the search area.
                var limit = ToPixels(new Rect(center - m.boxSize * (searchScale * 0.5f), m.boxSize * searchScale), w, h);
                var half = new Vector2(m.boxSize.x * w * 0.5f, m.boxSize.y * h * 0.5f);
                var c = new Vector2(center.x * w, center.y * h);
                for (var it = 0; it < meanShiftIterations; it++)
                {
                    float sx = 0, sy = 0, sw = 0;
                    int x0 = Mathf.Max(limit.xMin, (int)(c.x - half.x)), x1 = Mathf.Min(limit.xMax, (int)(c.x + half.x));
                    int y0 = Mathf.Max(limit.yMin, (int)(c.y - half.y)), y1 = Mathf.Min(limit.yMax, (int)(c.y + half.y));
                    for (var y = y0; y < y1; y += stride)
                    for (var x = x0; x < x1; x += stride)
                    {
                        var wt = m.weight[Bin(frame.pixels[y * w + x])];
                        sx += wt * x; sy += wt * y; sw += wt;
                    }
                    if (sw <= 1e-3f) break;
                    var next = new Vector2(sx / sw, sy / sw);
                    var done = (next - c).sqrMagnitude < 1f;
                    c = next;
                    if (done) break;
                }

                var boxPx = new RectInt((int)(c.x - half.x), (int)(c.y - half.y), (int)(half.x * 2), (int)(half.y * 2));
                var match = MeanWeight(m, frame.pixels, w, h, Clip(boxPx, w, h));
                if (match < minMatch * m.learnedMatch) { m.active = false; lost++; continue; } // let the classifier re-find it

                var ray = info.ImagePointToRay(new Vector2(c.x / w, c.y / h));
                if (!table.TryIntersect(ray, out var p)) continue;
                tracker.Report(kv.Key, p, reportConfidence);
                updates++;
            }
        }

        // ------------------------------------------------------------------ helpers

        float MeanWeight(Model m, Color32[] px, int w, int h, RectInt r)
        {
            float s = 0; var n = 0;
            for (var y = r.yMin; y < r.yMax; y += stride)
            for (var x = r.xMin; x < r.xMax; x += stride) { s += m.weight[Bin(px[y * w + x])]; n++; }
            return n > 0 ? s / n : 0f;
        }

        static int Bin(Color32 c)
        {
            Color.RGBToHSV(c, out var hue, out var sat, out var val);
            if (val < 0.18f) return 0;                                                  // dark (black tire)
            if (sat < 0.22f) return 1 + Mathf.Min(GreyBins - 1, (int)(val * GreyBins)); // greys / whites / tan-ish
            var hb = Mathf.Min(HueBins - 1, (int)(hue * HueBins));
            var sb = Mathf.Min(SatBins - 1, (int)((sat - 0.22f) / 0.78f * SatBins));
            return 1 + GreyBins + hb * SatBins + sb;
        }

        static Rect Expand(Rect r, float scale) => new(r.center - r.size * (scale * 0.5f), r.size * scale);

        static RectInt ToPixels(Rect r, int w, int h) =>
            Clip(new RectInt(Mathf.FloorToInt(r.xMin * w), Mathf.FloorToInt(r.yMin * h),
                Mathf.CeilToInt(r.width * w), Mathf.CeilToInt(r.height * h)), w, h);

        static RectInt Clip(RectInt r, int w, int h)
        {
            int x0 = Mathf.Clamp(r.xMin, 0, w), y0 = Mathf.Clamp(r.yMin, 0, h);
            int x1 = Mathf.Clamp(r.xMax, 0, w), y1 = Mathf.Clamp(r.yMax, 0, h);
            return new RectInt(x0, y0, Mathf.Max(0, x1 - x0), Mathf.Max(0, y1 - y0));
        }
    }
}
