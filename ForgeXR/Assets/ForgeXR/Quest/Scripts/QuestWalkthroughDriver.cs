using System;
using System.Collections.Generic;
using UnityEngine;

namespace Forge
{
    /// <summary>
    /// Drives Harley's walkthrough (ForgeXR.DesktopWalkthrough, e.g. DriverWalkthrough) on Quest.
    ///
    /// Harley's component stays DISABLED: its public API (Advance, ResetWalkthrough, StepIndex,
    /// IsComplete, CurrentInstruction) works on a disabled behaviour, while its desktop OnGUI
    /// (IMGUI panel + Space key) only runs when enabled. Awake enforces enabled = false.
    ///
    /// After every change this pushes "n/N: instruction" (Harley's completion message,
    /// without counter, when complete) to the FloatingCue, sets the Next label
    /// (Next / Done / Start over) and highlights the step's parts (nothing when complete).
    /// NextInput: Next = Advance (Start over when complete), Reset = step 1,
    /// Back (controller only) = reset and replay to the previous step (Harley has no Back).
    /// No photos on Quest (AR-minimal): CurrentStill is ignored.
    /// </summary>
    public class QuestWalkthroughDriver : MonoBehaviour
    {
        [Serializable]
        public class StepTargets
        {
            public string[] partIds = Array.Empty<string>();
            public StepTargets() { }
            public StepTargets(params string[] ids) { partIds = ids; }
        }

        public const string NextLabel = "Next";
        public const string DoneLabel = "Done";
        public const string StartOverLabel = "Start over";

        [SerializeField] ForgeXR.DesktopWalkthrough walkthrough;
        [SerializeField] NextInput input;
        [SerializeField] FloatingCue cue;

        [Tooltip("Part ids (PartIds / PickupSpot ids) to highlight per step, by step index.")]
        [SerializeField] List<StepTargets> stepParts = new()
        {
            new StepTargets(PartIds.Legs, PartIds.Torso),
            new StepTargets(PartIds.Head),
            new StepTargets(PartIds.Hair),
        };

        [Tooltip("Cue text per step. {0} = step number (1-based), {1} = step count, {2} = Harley's instruction. " +
                 "Plain text by default; add TMP rich text such as <size=70%> if FloatingCue renders it.")]
        [SerializeField] string stepFormat = "{0}/{1}: {2}";

        const int MaxProbeSteps = 64;
        int stepCount;

        public int StepCount => stepCount;

        public void Configure(ForgeXR.DesktopWalkthrough w, NextInput nextInput, FloatingCue floatingCue)
        {
            walkthrough = w;
            input = nextInput;
            cue = floatingCue;
        }

        void Awake()
        {
            if (walkthrough == null) walkthrough = GetComponent<ForgeXR.DesktopWalkthrough>();
            if (input == null) input = FindAnyObjectByType<NextInput>();
            if (cue == null) cue = FindAnyObjectByType<FloatingCue>();

            if (walkthrough == null)
            {
                Debug.LogError("[Forge] QuestWalkthroughDriver: no walkthrough assigned");
                enabled = false;
                return;
            }

            // Keep Harley's desktop IMGUI and Space handling off on Quest.
            walkthrough.enabled = false;

            stepCount = ProbeStepCount();
            if (stepCount != stepParts.Count)
                Debug.LogWarning($"[Forge] Walkthrough has {stepCount} steps but {stepParts.Count} step part mappings");
        }

        // StepCount is protected in Harley's class; count steps with the public API.
        // Safe because Advance/ResetWalkthrough only change StepIndex.
        int ProbeStepCount()
        {
            walkthrough.ResetWalkthrough();
            var n = 0;
            while (!walkthrough.IsComplete && n < MaxProbeSteps)
            {
                walkthrough.Advance();
                n++;
            }
            walkthrough.ResetWalkthrough();
            return n;
        }

        void OnEnable()
        {
            if (input == null) return;
            input.onNext.AddListener(OnNext);
            input.onBack.AddListener(OnBack);
            input.onReset.AddListener(OnReset);
        }

        void OnDisable()
        {
            if (input == null) return;
            input.onNext.RemoveListener(OnNext);
            input.onBack.RemoveListener(OnBack);
            input.onReset.RemoveListener(OnReset);
        }

        // Start runs after DefaultHighlight's Awake, so the walkthrough's highlight wins.
        void Start() => Refresh();

        void OnNext()
        {
            if (walkthrough.IsComplete) walkthrough.ResetWalkthrough();
            else walkthrough.Advance();
            Refresh();
        }

        void OnReset()
        {
            walkthrough.ResetWalkthrough();
            Refresh();
        }

        void OnBack()
        {
            var target = Mathf.Max(walkthrough.StepIndex - 1, 0);
            walkthrough.ResetWalkthrough();
            for (var i = 0; i < target; i++) walkthrough.Advance();
            Refresh();
        }

        void Refresh()
        {
            var complete = walkthrough.IsComplete;
            var step = walkthrough.StepIndex;

            if (cue != null)
            {
                // Completion: Harley's completion message only, no counter.
                cue.SetText(complete
                    ? walkthrough.CurrentInstruction
                    : string.Format(stepFormat, step + 1, stepCount, walkthrough.CurrentInstruction));
                cue.SetNextLabel(complete ? StartOverLabel : step == stepCount - 1 ? DoneLabel : NextLabel);
            }

            HighlightRegistry.Highlight(complete ? Array.Empty<string>() : PartsFor(step));
        }

        string[] PartsFor(int step)
        {
            if (step >= 0 && step < stepParts.Count && stepParts[step]?.partIds != null)
                return stepParts[step].partIds;
            Debug.LogWarning($"[Forge] No part ids mapped for walkthrough step {step}");
            return Array.Empty<string>();
        }
    }
}
