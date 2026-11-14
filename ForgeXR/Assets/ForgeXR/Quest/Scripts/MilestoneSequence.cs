// TEMPORARY TEST HARNESS - DELETE when Harley's walkthrough lands.
// When deleting: remove this file, remove Setup_NextInput.CreateMilestoneSequence (order 420),
// and destroy the ForgeRig/MilestoneSequence GameObject in the scene (it would otherwise keep
// a missing-script component).
using TMPro;
using UnityEngine;

namespace Forge
{
    /// <summary>
    /// TEMPORARY placeholder for the joint milestone only: step 1 highlights legs + torso with one
    /// line of text, Next moves the highlight to the head. No data model, no photos.
    /// Harley's walkthrough replaces this by subscribing to the same NextInput events.
    /// Runs after default-order scripts so it takes over chunk 2's default torso highlight.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class MilestoneSequence : MonoBehaviour
    {
        [SerializeField] NextInput input;
        [SerializeField] TMP_Text cueText;

        static readonly string[][] Targets =
        {
            new[] { PartIds.Legs, PartIds.Torso },
            new[] { PartIds.Head },
        };

        static readonly string[] Lines =
        {
            "Press the torso onto the legs with the shirt print facing the toes.",
            "Press the head onto the neck with the face pointing forward.",
        };

        int step;

        public void Configure(NextInput nextInput, TMP_Text text)
        {
            input = nextInput;
            cueText = text;
        }

        void OnEnable()
        {
            if (input == null) input = FindAnyObjectByType<NextInput>();
            if (input == null) return;
            input.onNext.AddListener(Next);
            input.onBack.AddListener(Back);
            input.onReset.AddListener(ResetSteps);
        }

        void OnDisable()
        {
            if (input == null) return;
            input.onNext.RemoveListener(Next);
            input.onBack.RemoveListener(Back);
            input.onReset.RemoveListener(ResetSteps);
        }

        void Start() => Show(0);

        void Next() => Show(Mathf.Min(step + 1, Targets.Length - 1));
        void Back() => Show(Mathf.Max(step - 1, 0));
        void ResetSteps() => Show(0);

        void Show(int i)
        {
            step = i;
            HighlightRegistry.Highlight(Targets[i]);
            if (cueText != null)
                cueText.text = $"<size=60%>Step {i + 1} of {Targets.Length}</size>\n{Lines[i]}";
        }
    }
}
