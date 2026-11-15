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

        public enum Flow { Driver, Wheel, Generated }

        public const string NextLabel = "Next";
        public const string DoneLabel = "Done";
        public const string StartOverLabel = "Start over";

        [SerializeField] ForgeXR.DemoWalkthrough demo;
        [SerializeField] ForgeXR.DriverWalkthrough driver;
        [SerializeField] ForgeXR.WheelWalkthrough wheel;
        [SerializeField] NextInput input;
        [SerializeField] FloatingCue cue;
        [SerializeField] PieceTracker tracker;
        [Tooltip("Walkthrough generated from a booklet page on the laptop (optional; operator-selected only).")]
        [SerializeField] GeneratedWalkthrough generated;
        [SerializeField] WalkthroughLoader loader;
        [Tooltip("Live config: set to 0 Driver / 1 Wheel / 2 Generated to switch now (-1 = no request).")]
        [SerializeField] int forceFlow = -1;
        CloudVisionClassifier cloud;
        int lastForceFlow = -1;

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
        [SerializeField] string waitingText = "Waiting for a walkthrough from the laptop...";

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
        ForgeXR.DesktopWalkthrough Active =>
            flow == Flow.Generated && generated != null ? generated : flow == Flow.Wheel ? wheel : driver;
        int ActiveStepCount =>
            flow == Flow.Generated && generated != null ? generated.Count : flow == Flow.Wheel ? wheelStepCount : driverStepCount;
        List<StepTargets> ActiveSteps => flow == Flow.Wheel ? wheelSteps : driverSteps;
        bool GeneratedReady => generated != null && generated.HasSpec;

        public void ConfigureGenerated(GeneratedWalkthrough g, WalkthroughLoader l)
        {
            generated = g;
            loader = l;
        }

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
            if (generated == null) generated = GetComponent<GeneratedWalkthrough>();
            if (loader == null) loader = GetComponent<WalkthroughLoader>();
            if (generated != null) generated.enabled = false; // desktop IMGUI off, like Harley's

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
            if (loader != null)
            {
                loader.Loaded += OnGeneratedLoaded;
                loader.SelectRequested += OnGeneratedSelectRequested;
            }
            toggleAction?.Enable();
            if (input == null) return;
            input.onNext.AddListener(OnNext);
            input.onBack.AddListener(OnBack);
            input.onReset.AddListener(OnReset);
        }

        void OnDisable()
        {
            if (loader != null)
            {
                loader.Loaded -= OnGeneratedLoaded;
                loader.SelectRequested -= OnGeneratedSelectRequested;
            }
            toggleAction?.Disable();
            if (input == null) return;
            input.onNext.RemoveListener(OnNext);
            input.onBack.RemoveListener(OnBack);
            input.onReset.RemoveListener(OnReset);
        }

        void Start() => Select(Flow.Driver, "start");

        void Update()
        {
            // Live-config request (RemoteConfig sets forceFlow): switch once per new value.
            if (forceFlow != lastForceFlow)
            {
                lastForceFlow = forceFlow;
                if (forceFlow >= 0 && forceFlow <= (int)Flow.Generated)
                {
                    manualLock = true;
                    Select((Flow)forceFlow, "live config forceFlow");
                }
            }
            // A generated walkthrough is only ever chosen by the operator; never auto-switch away from it.
            if (!autoSelect || manualLock || tracker == null || flow == Flow.Generated) return;
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
            // Driver -> Wheel -> Generated (when one is loaded) -> Driver.
            var next = flow == Flow.Driver ? Flow.Wheel
                : flow == Flow.Wheel && GeneratedReady ? Flow.Generated
                : Flow.Driver;
            Select(next, "operator toggle (auto-select paused until complete)");
        }

        void OnGeneratedLoaded(GeneratedWalkthrough w)
        {
            if (flow == Flow.Generated) Refresh(); // the laptop changed it: restart it at step 1 (Load reset it)
        }

        void OnGeneratedSelectRequested()
        {
            manualLock = true;
            Select(Flow.Generated, "operator selected the generated walkthrough on the laptop");
        }

        void Select(Flow f, string reason)
        {
            if (f == Flow.Generated && generated == null) f = Flow.Driver;
            flow = f;
            if (demo != null && f != Flow.Generated)
            {
                if (f == Flow.Wheel) demo.SelectWheel();
                else demo.SelectDriver();
            }
            Active.ResetWalkthrough();
            if (tracker != null) tracker.SetShownParts(FlowParts(f));
            Debug.Log($"[Forge] Walkthrough: {f} ({reason})");
            Refresh();
        }

        void OnNext()
        {
            var active = Active;
            if (active.IsComplete) active.ResetWalkthrough();
            else active.Advance();
            // Auto-select may pick the next walkthrough (never away from an operator-chosen generated one).
            if (active.IsComplete && flow != Flow.Generated) manualLock = false;
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
                if (flow == Flow.Generated && !GeneratedReady)
                {
                    cue.SetText(waitingText);
                    cue.SetNextLabel(NextLabel);
                }
                else
                {
                    // Completion: the completion message only, no counter.
                    var text = complete
                        ? active.CurrentInstruction
                        : string.Format(stepFormat, step + 1, count, active.CurrentInstruction);
                    // Generated steps carry what to verify (orientation, seating): a smaller second line.
                    var check = flow == Flow.Generated && !complete ? generated.CheckFor(step) : "";
                    if (!string.IsNullOrEmpty(check)) text += $"\n<size=70%>Check: {check}</size>";
                    cue.SetText(text);
                    cue.SetNextLabel(complete ? StartOverLabel : step == count - 1 ? DoneLabel : NextLabel);
                }
            }

            HighlightRegistry.Highlight(complete ? Array.Empty<string>() : PartsFor(step));

            // Ask the vision model only for this walkthrough's parts while building; all parts before step 1 and
            // after completion, so auto-select can still tell the driver and wheel pieces apart.
            if (cloud == null) cloud = FindAnyObjectByType<CloudVisionClassifier>();
            if (cloud != null)
            {
                // Always this walkthrough's parts (other labels get confused onto our pieces and hide them); at step 1
                // and on the completion screen, 1 request in 4 asks for every part so auto-select can switch.
                cloud.RequestedParts = FlowParts(flow);
                cloud.ProbeAllEveryN = flow != Flow.Generated && (step == 0 || complete) ? 4 : 0;
                var flowName = flow == Flow.Generated ? "gen" : flow == Flow.Wheel ? "wheel" : "driver";
                cloud.WalkthroughState = $"{flowName},step={step},complete={(complete ? "true" : "false")}";
            }
        }

        string[] FlowParts(Flow f) =>
            f == Flow.Generated && generated != null ? generated.Detectable
            : f == Flow.Wheel ? PartIds.WheelStaging : PartIds.DriverStaging;

        string[] PartsFor(int step)
        {
            if (flow == Flow.Generated) return generated != null ? generated.HighlightFor(step) : Array.Empty<string>();
            var steps = ActiveSteps;
            if (step >= 0 && step < steps.Count && steps[step]?.partIds != null)
                return steps[step].partIds;
            Debug.LogWarning($"[Forge] No part ids mapped for {flow} step {step}");
            return Array.Empty<string>();
        }
    }
}
