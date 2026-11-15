using System;
using UnityEngine;

namespace Forge
{
    /// <summary>
    /// A walkthrough generated on the laptop from an instruction-booklet page (tools/vision-relay/generate_walkthrough.py)
    /// and downloaded from the relay (WalkthroughLoader). Implements Harley's DesktopWalkthrough API, so
    /// QuestWalkthroughDriver runs it exactly like the hand-written Driver / Wheel walkthroughs: "n/N: instruction"
    /// cue, Next / Done / Start over, highlights for the parts the detector knows, automatic step completion from the
    /// relay (flow "gen"). Stays disabled like Harley's components (the base class draws a desktop IMGUI panel in OnGUI).
    /// </summary>
    public class GeneratedWalkthrough : ForgeXR.DesktopWalkthrough
    {
        /// <summary>Wire shape of GET /walkthrough (walkthrough_spec.for_quest); JsonUtility-friendly.</summary>
        [Serializable]
        public class Step
        {
            public string instruction = "";
            public string[] highlight = Array.Empty<string>();
            public string check = "";
            public bool auto;
        }

        [Serializable]
        public class Spec
        {
            public string id = "";
            public string title = "";
            public string completion = "";
            public string[] detectable = Array.Empty<string>();
            public Step[] steps = Array.Empty<Step>();
            public int selectNonce;
        }

        Spec spec;

        public bool HasSpec => spec?.steps != null && spec.steps.Length > 0;
        public string SpecId => spec?.id ?? "";
        public string SpecTitle => spec?.title ?? "";
        public int Count => spec?.steps?.Length ?? 0;
        public string[] Detectable => spec?.detectable ?? Array.Empty<string>();

        /// <summary>Replace the walkthrough (restarts at step 1).</summary>
        public void Load(Spec s)
        {
            spec = s;
            ResetWalkthrough();
        }

        public string[] HighlightFor(int step) =>
            spec?.steps != null && step >= 0 && step < spec.steps.Length && spec.steps[step].highlight != null
                ? spec.steps[step].highlight
                : Array.Empty<string>();

        public string CheckFor(int step) =>
            spec?.steps != null && step >= 0 && step < spec.steps.Length ? spec.steps[step].check ?? "" : "";

        protected override string Title => string.IsNullOrEmpty(spec?.title) ? "Generated walkthrough" : spec.title;
        protected override string CompletionMessage => string.IsNullOrEmpty(spec?.completion) ? "Done" : spec.completion;
        protected override int StepCount => Count;
        protected override string Instruction =>
            spec?.steps != null && StepIndex < spec.steps.Length ? spec.steps[StepIndex].instruction : "";
        protected override string PartCue => string.Join(", ", HighlightFor(StepIndex));
        public override Texture2D CurrentStill => null;
    }
}
