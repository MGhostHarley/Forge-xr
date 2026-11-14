using UnityEngine;
using UnityEngine.Events;

namespace Forge
{
    /// <summary>
    /// Walkthrough advance input. Harley's walkthrough controller subscribes to onNext/onBack
    /// (Space advances in the desktop scene; here A/trigger/B on the right controller plus Space).
    /// CONTRACT STUB: chunk 4 (ps/next-input) implements the bindings without changing these public members.
    /// Input must be ignored while TableAligner.IsSceneLocked() is false.
    /// </summary>
    public class NextInput : MonoBehaviour
    {
        public UnityEvent onNext = new();
        public UnityEvent onBack = new();
        /// <summary>Back to step 1 (Harley's Reset). Primary source: a virtual Reset PokeButton.</summary>
        public UnityEvent onReset = new();

        public void RaiseNext() => onNext.Invoke();
        public void RaiseBack() => onBack.Invoke();
        public void RaiseReset() => onReset.Invoke();
    }
}
