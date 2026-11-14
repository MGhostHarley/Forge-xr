using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Forge
{
    /// <summary>
    /// Drives Harley's walkthroughs on Quest through Harley's ForgeXR.DemoWalkthrough (Driver + Wheel).
    ///
    /// Harley's components stay DISABLED: their public API (Advance, ResetWalkthrough, StepIndex,
    /// IsComplete, CurrentInstruction, SelectDriver/SelectWheel, ActiveWalkthrough) works on disabled
    /// behaviours, while their desktop OnGUI (IMGUI panel + Space key) only runs when enabled.
    ///
    /// Which walkthrough runs is chosen from the parts on the table: before a walkthrough starts
    /// (step 1) or after it completes, the flow with more of its parts currently detected by the
    /// classifier (PieceTracker) is selected, after it has been the clear winner for a moment.
    /// Operator override (testing / no classifier yet): controller left Y or keyboard W toggles
    /// Driver/Wheel and pauses auto-select until that walkthrough completes.
    ///
    /// After every change this pushes "n/N: instruction" (Harley's completion message, without
    /// counter, when complete) to the FloatingCue, sets the Next label (Next / Done / Start over)
    /// and highlights the step's parts (nothing when complete). NextInput: Next = Advance
    /// (Start over when complete), Reset = step 1 of the selected walkthrough, Back (controller only)
    /// = reset and replay to the previous step (Harley has no Back). No photos on Quest.
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

        public enum Flow { Driver, Wheel }

        public const string NextLabel = "Next";
        public const string DoneLabel = "Done";
        public const string StartOverLabel = "Start over";

        [SerializeField] ForgeXR.DemoWalkthrough demo;
        [SerializeField] ForgeXR.DriverWalkthrough driver;
        [SerializeField] ForgeXR.WheelWalkthrough wheel;
        [SerializeField] NextInput input;
        [SerializeField] FloatingCue cue;
        [SerializeField] PieceTracker tracker;

        [Tooltip("Driver: part ids to highlight per step, by step index.")]
        [SerializeField] List<StepTargets> driverSteps = new()
        {
            new StepTargets(PartIds.Legs, PartIds.Torso),
            new StepTargets(PartIds.Head),
            new StepTargets(PartIds.Hair),
        };

        [Tooltip("Wheel: part ids to highlight per step, by step index.")]
        [SerializeField] List<StepTargets> wheelSteps = new()
        {
            new StepTargets(PartIds.Tire, PartIds.Rim), // pick up the tire and the rim
            new StepTargets(PartIds.Tire, PartIds.Rim), // press the rim into the tire
            new StepTargets(PartIds.AxlePin),           // line up with the yellow axle pin and press on
            new StepTargets(PartIds.AxlePin),           // spin check where the wheel is now mounted
        };

        [Tooltip("Cue text per step. {0} = step number (1-based), {1} = step count, {2} = Harley's instruction.")]
        [SerializeField] string stepFormat = "{0}/{1}: {2}";

        [Header("Auto-select from detected parts")]
        [SerializeField] bool autoSelect = true;
        [Tooltip("A flow must lead the detected-part count this long (s) before switching to it.")]
        [SerializeField] float selectStableSeconds = 1f;
        [SerializeField] float selectCheckInterval = 0.25f;

        const int MaxProbeSteps = 64;
        int driverStepCount, wheelStepCount;
        Flow flow = Flow.Driver;
        bool manualLock;
        Flow? candidate;
        float candidateSince, nextCheck;
        InputAction toggleAction;

        public Flow CurrentFlow => flow;
        ForgeXR.DesktopWalkthrough Active => flow == Flow.Wheel ? wheel : driver;
        int ActiveStepCount => flow == Flow.Wheel ? wheelStepCount : driverStepCount;
        List<StepTargets> ActiveSteps => flow == Flow.Wheel ? wheelSteps : driverSteps;

        public void Configure(ForgeXR.DemoWalkthrough d, ForgeXR.DriverWalkthrough dr, ForgeXR.WheelWalkthrough wh,
                              NextInput nextInput, FloatingCue floatingCue, PieceTracker pieceTracker)
        {
            demo = d;
            driver = dr;
            wheel = wh;
            input = nextInput;
            cue = floatingCue;
            tracker = pieceTracker;
        }

        void Awake()
        {
            if (demo == null) demo = GetComponent<ForgeXR.DemoWalkthrough>();
            if (driver == null) driver = GetComponent<ForgeXR.DriverWalkthrough>();
            if (wheel == null) wheel = GetComponent<ForgeXR.WheelWalkthrough>();
            if (input == null) input = FindAnyObjectByType<NextInput>();
            if (cue == null) cue = FindAnyObjectByType<FloatingCue>();
            if (tracker == null) tracker = FindAnyObjectByType<PieceTracker>();

            if (driver == null || wheel == null)
            {
                Debug.LogError("[Forge] QuestWalkthroughDriver: driver and wheel walkthroughs must both be assigned");
                enabled = false;
                return;
            }

            // Keep Harley's desktop IMGUI and Space handling off on Quest.
            driver.enabled = false;
            wheel.enabled = false;
            if (demo != null) demo.enabled = false;

            driverStepCount = ProbeStepCount(driver);
            wheelStepCount = ProbeStepCount(wheel);
            if (driverStepCount != driverSteps.Count)
                Debug.LogWarning($"[Forge] Driver has {driverStepCount} steps but {driverSteps.Count} step part mappings");
            if (wheelStepCount != wheelSteps.Count)
                Debug.LogWarning($"[Forge] Wheel has {wheelStepCount} steps but {wheelSteps.Count} step part mappings");

            toggleAction = new InputAction("ToggleWalkthrough", InputActionType.Button);
            toggleAction.AddBinding("<XRController>{LeftHand}/{SecondaryButton}"); // left Y
            toggleAction.AddBinding("<Keyboard>/w");
            toggleAction.performed += _ => ToggleManually();
        }

        void OnDestroy() => toggleAction?.Dispose();

        // StepCount is protected in Harley's class; count steps with the public API.
        // Safe because Advance/ResetWalkthrough only change StepIndex.
        static int ProbeStepCount(ForgeXR.DesktopWalkthrough w)
        {
            w.ResetWalkthrough();
            var n = 0;
            while (!w.IsComplete && n < MaxProbeSteps)
            {
                w.Advance();
                n++;
            }
            w.ResetWalkthrough();
            return n;
        }

        void OnEnable()
        {
            toggleAction?.Enable();
            if (input == null) return;
            input.onNext.AddListener(OnNext);
            input.onBack.AddListener(OnBack);
            input.onReset.AddListener(OnReset);
        }

        void OnDisable()
        {
            toggleAction?.Disable();
            if (input == null) return;
            input.onNext.RemoveListener(OnNext);
            input.onBack.RemoveListener(OnBack);
            input.onReset.RemoveListener(OnReset);
        }

        // Start runs after DefaultHighlight's Awake, so the walkthrough's highlight wins.
        void Start() => Select(Flow.Driver, "start");

        void Update()
        {
            if (!autoSelect || manualLock || tracker == null) return;
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + selectCheckInterval;

            // Only choose before a walkthrough starts or after it completes, never mid-build.
            var active = Active;
            if (active.StepIndex != 0 && !active.IsComplete) { candidate = null; return; }

            var driverSeen = CountTracked(PartIds.DriverStaging);
            var wheelSeen = CountTracked(PartIds.WheelStaging);
            Flow? leader = driverSeen > wheelSeen ? Flow.Driver : wheelSeen > driverSeen ? Flow.Wheel : null;

            if (leader == null || leader == flow) { candidate = null; return; }
            if (candidate != leader) { candidate = leader; candidateSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - candidateSince >= selectStableSeconds)
            {
                candidate = null;
                Select(leader.Value, $"detected parts driver={driverSeen} wheel={wheelSeen}");
            }
        }

        int CountTracked(string[] ids)
        {
            var n = 0;
            foreach (var id in ids) if (tracker.IsTracked(id)) n++;
            return n;
        }

        void ToggleManually()
        {
            manualLock = true;
            Select(flow == Flow.Driver ? Flow.Wheel : Flow.Driver, "operator toggle (auto-select paused until complete)");
        }

        void Select(Flow f, string reason)
        {
            flow = f;
            if (demo != null)
            {
                if (f == Flow.Wheel) demo.SelectWheel();
                else demo.SelectDriver();
            }
            Active.ResetWalkthrough();
            Debug.Log($"[Forge] Walkthrough: {f} ({reason})");
            Refresh();
        }

        void OnNext()
        {
            var active = Active;
            if (active.IsComplete) active.ResetWalkthrough();
            else active.Advance();
            if (active.IsComplete) manualLock = false; // auto-select may pick the next walkthrough
            Refresh();
        }

        void OnReset()
        {
            Active.ResetWalkthrough();
            Refresh();
        }

        void OnBack()
        {
            var active = Active;
            var target = Mathf.Max(active.StepIndex - 1, 0);
            active.ResetWalkthrough();
            for (var i = 0; i < target; i++) active.Advance();
            Refresh();
        }

        void Refresh()
        {
            var active = Active;
            var complete = active.IsComplete;
            var step = active.StepIndex;
            var count = ActiveStepCount;

            if (cue != null)
            {
                // Completion: Harley's completion message only, no counter.
                cue.SetText(complete
                    ? active.CurrentInstruction
                    : string.Format(stepFormat, step + 1, count, active.CurrentInstruction));
                cue.SetNextLabel(complete ? StartOverLabel : step == count - 1 ? DoneLabel : NextLabel);
            }

            HighlightRegistry.Highlight(complete ? Array.Empty<string>() : PartsFor(step));
        }

        string[] PartsFor(int step)
        {
            var steps = ActiveSteps;
            if (step >= 0 && step < steps.Count && steps[step]?.partIds != null)
                return steps[step].partIds;
            Debug.LogWarning($"[Forge] No part ids mapped for {flow} step {step}");
            return Array.Empty<string>();
        }
    }
}
