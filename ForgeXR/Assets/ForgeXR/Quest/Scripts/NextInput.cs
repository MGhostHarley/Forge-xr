using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

namespace Forge
{
    /// <summary>
    /// Walkthrough advance input. Harley's walkthrough controller subscribes to onNext/onBack/onReset
    /// (Space advances in the desktop scene).
    ///
    /// Sources, all funnelled through the same gate (scene locked, grace window after lock, shared debounce):
    ///   Primary (hand tracking): the floating Next and Reset buttons (PokeButton, pressed by pinch)
    ///                            on the FloatingCue, wired in the inspector by Setup_NextInput.
    ///   Backup (controllers):    right A or trigger = Next (on press), right B tap = Back,
    ///                            left X held 1 s = Reset.
    ///   Editor:                  Space = Next, Backspace = Back, R = Reset.
    /// RaiseNext/RaiseBack/RaiseReset are gated too, so a uGUI button calling RaiseNext() and a
    /// controller press in the same moment still count as one advance.
    /// Input is ignored while TableAligner.IsLocked is false (no TableAligner = locked). TableAligner is
    /// now a no-op that always reports locked, so nothing here depends on alignment.
    /// </summary>
    public class NextInput : MonoBehaviour
    {
        public UnityEvent onNext = new();
        public UnityEvent onBack = new();
        /// <summary>Back to step 1 (Harley's Reset). Primary source: the floating Reset button.</summary>
        public UnityEvent onReset = new();

        [Header("Virtual buttons (hand tracking)")]
        [SerializeField] PokeButton nextButton;
        [SerializeField] PokeButton resetButton;

        [Header("Gating")]
        [Tooltip("Minimum seconds between two raised events (shared by Next, Back and Reset).")]
        [SerializeField] float debounceSeconds = 0.3f;
        [Tooltip("Seconds after the mat locks during which input is ignored, so the lock press doesn't also advance.")]
        [SerializeField] float graceAfterLockSeconds = 0.5f;
        [Tooltip("Back fires only if B is released within this many seconds (longer = chunk 3's realign long-press).")]
        [SerializeField] float backTapSeconds = 0.35f;
        [Tooltip("Seconds to hold left X for Reset.")]
        [SerializeField] float resetHoldSeconds = 1f;

        InputAction nextAction, backAction, resetAction;
        TableAligner aligner;
        bool alignerSearched;
        bool wasLocked;
        float lockedAt = float.NegativeInfinity;
        float lastRaisedAt = float.NegativeInfinity;

        public void RaiseNext() => TryRaise(onNext, "Next");
        public void RaiseBack() => TryRaise(onBack, "Back");
        public void RaiseReset() => TryRaise(onReset, "Reset");

        void Awake()
        {
            nextAction = new InputAction("Forge Next", InputActionType.Button);
            nextAction.AddBinding("<XRController>{RightHand}/{PrimaryButton}");
            nextAction.AddBinding("<XRController>{RightHand}/{TriggerButton}");
            nextAction.AddBinding("<Keyboard>/space");

            backAction = new InputAction("Forge Back", InputActionType.Button);
            backAction.AddBinding("<XRController>{RightHand}/{SecondaryButton}",
                interactions: $"tap(duration={F(backTapSeconds)})");
            backAction.AddBinding("<Keyboard>/backspace");

            resetAction = new InputAction("Forge Reset", InputActionType.Button);
            resetAction.AddBinding("<XRController>{LeftHand}/{PrimaryButton}",
                interactions: $"hold(duration={F(resetHoldSeconds)})");
            resetAction.AddBinding("<Keyboard>/r");

            nextAction.performed += _ => RaiseNext();
            backAction.performed += _ => RaiseBack();
            resetAction.performed += _ => RaiseReset();
        }

        static string F(float v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

        void OnEnable()
        {
            nextAction?.Enable();
            backAction?.Enable();
            resetAction?.Enable();
            if (nextButton != null) nextButton.onPressed.AddListener(RaiseNext);
            if (resetButton != null) resetButton.onPressed.AddListener(RaiseReset);
        }

        void OnDisable()
        {
            nextAction?.Disable();
            backAction?.Disable();
            resetAction?.Disable();
            if (nextButton != null) nextButton.onPressed.RemoveListener(RaiseNext);
            if (resetButton != null) resetButton.onPressed.RemoveListener(RaiseReset);
        }

        void OnDestroy()
        {
            if (aligner != null) aligner.onAligned.RemoveListener(OnAligned);
            nextAction?.Dispose();
            backAction?.Dispose();
            resetAction?.Dispose();
        }

        void Start()
        {
            FindAligner();
            wasLocked = IsLocked();
        }

        void Update()
        {
            // Catch the unlocked -> locked edge even if onAligned isn't raised.
            var locked = IsLocked();
            if (locked && !wasLocked) lockedAt = Time.unscaledTime;
            wasLocked = locked;
        }

        void FindAligner()
        {
            if (alignerSearched) return;
            alignerSearched = true;
            aligner = FindAnyObjectByType<TableAligner>();
            if (aligner != null) aligner.onAligned.AddListener(OnAligned);
        }

        void OnAligned() => lockedAt = Time.unscaledTime;

        bool IsLocked()
        {
            FindAligner();
            return aligner == null || aligner.IsLocked;
        }

        void TryRaise(UnityEvent evt, string what)
        {
            var now = Time.unscaledTime;
            if (!IsLocked()) return;
            // The press that locked the mat may land in the same frame as the lock; treat it as inside the window.
            if (!wasLocked) { lockedAt = now; wasLocked = true; }
            if (now - lockedAt < graceAfterLockSeconds) return;
            if (now - lastRaisedAt < debounceSeconds) return;
            lastRaisedAt = now;
            Debug.Log($"[Forge] {what}");
            evt.Invoke();
        }
    }
}
