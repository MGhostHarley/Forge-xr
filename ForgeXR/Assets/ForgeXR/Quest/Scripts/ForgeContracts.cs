using UnityEngine;

namespace Forge
{
    /// <summary>
    /// Part ids shared with Harley's walkthrough data. A step's highlight targets are these strings.
    /// </summary>
    public static class PartIds
    {
        public const string Legs = "legs";
        public const string Torso = "torso";
        public const string Head = "head";
        public const string Hair = "hairpiece";

        /// <summary>Staging order, builder's left to right.</summary>
        public static readonly string[] DriverStaging = { Legs, Torso, Head, Hair };
    }

    /// <summary>
    /// Scene naming. There is no physical mat any more: StagingMat is an identity-pose container for the
    /// PickupSpots, which PieceTracker moves in world space onto the real pieces found by the camera.
    /// </summary>
    public static class ForgeScene
    {
        /// <summary>Root GameObject that holds all Forge objects.</summary>
        public const string RigRoot = "ForgeRig";

        /// <summary>Child of RigRoot at the identity pose; holds the spots (world position = local position).</summary>
        public const string StagingMat = "StagingMat";

        /// <summary>Pickup spot GameObjects are children of StagingMat named "Spot_" + part id.</summary>
        public const string SpotPrefix = "Spot_";

        /// <summary>Default spacing between pickup spots along +X, in metres.</summary>
        public const float SpotSpacing = 0.06f;

        /// <summary>Default local position of the spot at <paramref name="index"/> of <paramref name="count"/>.</summary>
        public static Vector3 DefaultSpotLocalPosition(int index, int count)
        {
            var x = (index - (count - 1) * 0.5f) * SpotSpacing;
            return new Vector3(x, 0f, 0f);
        }
    }
}
