using UnityEngine;

namespace Forge
{
    /// <summary>
    /// A part's pickup location on the staging mat. Registers itself with HighlightRegistry by Id.
    /// CONTRACT STUB: chunk 2 (ps/world-locked-label) implements the visuals (ring at table height,
    /// camera-facing label ~8 cm above) without changing these public members.
    /// </summary>
    public class PickupSpot : MonoBehaviour
    {
        [SerializeField] string id = PartIds.Torso;
        [SerializeField] string label = "Torso";

        public string Id => id;
        public string Label => label;
        public bool IsHighlighted { get; private set; }

        /// <summary>Used by setup steps to configure a spot after AddComponent.</summary>
        public void Configure(string partId, string displayLabel)
        {
            id = partId;
            label = displayLabel;
        }

        public void SetHighlighted(bool on)
        {
            IsHighlighted = on;
            OnHighlightChanged(on);
        }

        /// <summary>Chunk 2 fills this in.</summary>
        protected virtual void OnHighlightChanged(bool on) { }

        protected virtual void OnEnable() => HighlightRegistry.Register(this);
        protected virtual void OnDisable() => HighlightRegistry.Unregister(this);
    }
}
