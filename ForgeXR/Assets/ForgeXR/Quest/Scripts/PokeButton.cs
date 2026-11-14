using UnityEngine;
using UnityEngine.Events;

namespace Forge
{
    /// <summary>
    /// A world-space button pressed by poking it with an index fingertip (XRI poke on the Hands rig).
    /// Used for Next, Reset (chunk 4) and Lock / Realign (chunk 3).
    /// CONTRACT STUB: chunk 4 (ps/next-input) implements the poke interaction and visuals without
    /// changing these public members. Other chunks create buttons via AddComponent + Configure and
    /// subscribe to onPressed.
    /// </summary>
    public class PokeButton : MonoBehaviour
    {
        [SerializeField] string label = "Next";
        [Tooltip("Face size in metres (width, height).")]
        [SerializeField] Vector2 size = new(0.06f, 0.03f);

        public UnityEvent onPressed = new();

        public string Label => label;
        public Vector2 Size => size;

        /// <summary>Used by setup steps after AddComponent.</summary>
        public void Configure(string displayLabel, Vector2 faceSize)
        {
            label = displayLabel;
            size = faceSize;
            OnConfigured();
        }

        public void SetLabel(string displayLabel)
        {
            label = displayLabel;
            OnConfigured();
        }

        /// <summary>Raises onPressed. Call this from the poke handler; also usable from tests.</summary>
        public void Press() => onPressed.Invoke();

        /// <summary>Chunk 4 fills this in (rebuild label/visuals).</summary>
        protected virtual void OnConfigured() { }
    }
}
