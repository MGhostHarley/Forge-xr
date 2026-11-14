using UnityEngine;

namespace Forge
{
    /// <summary>
    /// Checkpoint default: highlights the given part ids at startup so the torso spot lights up
    /// without any walkthrough logic. Runs in Awake and only if nothing is highlighted yet, so any
    /// walkthrough controller that calls HighlightRegistry.Highlight in Start (or later) wins.
    /// Safe to remove once the walkthrough drives highlighting.
    /// </summary>
    public class DefaultHighlight : MonoBehaviour
    {
        [SerializeField] string[] ids = { PartIds.Torso };

        void Awake()
        {
            if (HighlightRegistry.ActiveIds.Count == 0)
                HighlightRegistry.Highlight(ids);
        }
    }
}
