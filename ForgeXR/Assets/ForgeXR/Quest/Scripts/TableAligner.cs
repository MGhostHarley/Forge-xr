using UnityEngine;
using UnityEngine.Events;

namespace Forge
{
    /// <summary>
    /// RETIRED. The staging mat and manual table alignment were replaced by camera-based piece placement
    /// (CameraFrameSource -> PieceLocator -> PieceTracker). This no-op stays only because NextInput and ForgeDiagnostics
    /// still reference the type; it always reports locked and is no longer added to the scene.
    /// </summary>
    public class TableAligner : MonoBehaviour
    {
        public UnityEvent onAligned = new();
        public UnityEvent onRealign = new();

        public bool IsLocked => true;

        /// <summary>Always true: nothing needs aligning any more.</summary>
        public static bool IsSceneLocked() => true;
    }
}
