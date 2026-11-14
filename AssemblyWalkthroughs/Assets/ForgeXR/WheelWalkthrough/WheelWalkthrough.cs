using UnityEngine;

namespace ForgeXR
{
    public sealed class WheelWalkthrough : DesktopWalkthrough
    {
        [SerializeField] private Texture2D pickupStill;
        [SerializeField] private Texture2D assembledStill;
        [SerializeField] private Texture2D alignmentStill;
        [SerializeField] private Texture2D mountedStill;

        private static readonly string[] Instructions =
        {
            "Pick up the tire on the left and the rim on the right.",
            "Press the rim into the tire until the rubber sits evenly all the way around.",
            "Line up the wheel’s center hole with the yellow axle pin on the left and press the wheel on.",
            "Check that the mounted wheel spins freely, then press Done."
        };

        protected override string Title => "LEGO chassis and wheel assembly";
        protected override string CompletionMessage => "Wheel installed";
        protected override int StepCount => Instructions.Length;
        protected override string Instruction => Instructions[StepIndex];
        protected override string PartCue => StepIndex == 0 ? "Tire and rim" : string.Empty;

        public override Texture2D CurrentStill => StepIndex switch
        {
            0 => pickupStill,
            1 => assembledStill,
            2 => alignmentStill,
            _ => mountedStill
        };
    }
}
