using UnityEngine;

namespace ForgeXR
{
    public sealed class DriverWalkthrough : DesktopWalkthrough
    {
        [SerializeField] private Texture2D torsoStill;
        [SerializeField] private Texture2D headStill;
        [SerializeField] private Texture2D finishedStill;

        private static readonly string[] Instructions =
        {
            "Press the torso onto the legs with the shirt print facing the toes.",
            "Press the head onto the neck with the face pointing forward.",
            "Press the hairpiece onto the head."
        };

        private static readonly string[] Parts =
        {
            "Tan legs and blue torso",
            "Yellow head",
            "Hairpiece"
        };

        protected override string Title => "LEGO driver assembly";
        protected override string CompletionMessage => "Driver assembled";
        protected override int StepCount => Instructions.Length;
        protected override string Instruction => Instructions[StepIndex];
        protected override string PartCue => Parts[StepIndex];
        public override Texture2D CurrentStill => StepIndex switch
        {
            0 => torsoStill,
            1 => headStill,
            _ => finishedStill
        };
    }
}
