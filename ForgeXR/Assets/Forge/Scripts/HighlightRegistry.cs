using System.Collections.Generic;
using UnityEngine;

namespace Forge
{
    /// <summary>
    /// Integration point with the walkthrough: a step's highlight targets (part id strings)
    /// resolve here to PickupSpots. Highlight() replaces the current highlight set.
    /// Complete as of the contracts commit; chunks use it as-is.
    /// </summary>
    public static class HighlightRegistry
    {
        static readonly Dictionary<string, PickupSpot> Spots = new();
        static readonly HashSet<string> Active = new();

        public static void Register(PickupSpot spot)
        {
            if (spot == null || string.IsNullOrEmpty(spot.Id)) return;
            Spots[spot.Id] = spot;
            spot.SetHighlighted(Active.Contains(spot.Id));
        }

        public static void Unregister(PickupSpot spot)
        {
            if (spot != null && Spots.TryGetValue(spot.Id, out var s) && s == spot)
                Spots.Remove(spot.Id);
        }

        public static bool TryGet(string id, out PickupSpot spot) => Spots.TryGetValue(id, out spot);

        /// <summary>Highlights exactly these ids; everything else is cleared. Unknown ids are logged.</summary>
        public static void Highlight(params string[] ids)
        {
            Active.Clear();
            if (ids != null)
                foreach (var id in ids)
                {
                    Active.Add(id);
                    if (!Spots.ContainsKey(id)) Debug.LogWarning($"[Forge] No PickupSpot registered for '{id}'");
                }
            foreach (var kv in Spots) kv.Value.SetHighlighted(Active.Contains(kv.Key));
        }

        public static void ClearAll() => Highlight();

        public static IReadOnlyCollection<string> ActiveIds => Active;

#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Spots.Clear(); Active.Clear(); }
#endif
    }
}
