using System.Collections.Generic;
using UnityEngine;

namespace Forge
{
    /// <summary>
    /// One passthrough camera frame handed to the classifier. Pixels are RGBA32, row 0 = top of the image.
    /// Owned by the Quest side (CameraFrameSource); valid only during the Classify call, so copy what you keep.
    /// </summary>
    public sealed class CameraFrame
    {
        public int width;
        public int height;
        /// <summary>RGBA32, length = width * height.</summary>
        public Color32[] pixels;
        /// <summary>Capture time in seconds (camera timestamp), for matching detections to the head pose.</summary>
        public double timestamp;
        public int frameId;
    }

    /// <summary>
    /// One recognised piece in a frame. imagePoint is the piece's centre in normalised image
    /// coordinates: (0,0) = top-left, (1,1) = bottom-right of the frame that was classified.
    /// </summary>
    public struct PieceDetection
    {
        /// <summary>A PartIds value, e.g. "torso".</summary>
        public string partId;
        public Vector2 imagePoint;
        /// <summary>0..1.</summary>
        public float confidence;
        /// <summary>Optional normalised bounding box (same convention as imagePoint); zero size if unknown.</summary>
        public Rect imageBox;
    }

    /// <summary>
    /// CONTRACT between the Quest side and Harley's on-device classifier.
    /// Harley: subclass this and implement Classify. The Quest side calls it on the main thread at a few Hz
    /// with the latest camera frame and turns each detection into a highlight on the real table.
    /// If inference is slow, start it asynchronously here and write the most recent finished results.
    /// The Quest side does NOT classify; it only supplies frames and places the results in 3D.
    /// </summary>
    public abstract class PieceClassifierBehaviour : MonoBehaviour
    {
        /// <summary>Clear <paramref name="results"/> and add every piece found in <paramref name="frame"/>.</summary>
        public abstract void Classify(CameraFrame frame, List<PieceDetection> results);
    }
}
