using System.Collections;
using UnityEngine;

namespace Forge
{
    /// <summary>
    /// Celebrates an automatically detected step completion and advances the walkthrough.
    ///
    /// The relay (tools/vision-relay/step_check.py) decides when a step is assembled (geometry + VLM yes/no) and attaches
    /// "stepComplete" to its /detect answers; <see cref="CloudVisionClassifier.StepCompleted"/> forwards it here (only
    /// for the current walkthrough state). Then, once per state:
    ///   - a procedural chime (no audio asset) from the assembled piece,
    ///   - a confetti burst at the piece (PieceTracker position of the anchor part, else the step's other part, else
    ///     in front of the user); a bigger burst and a longer chime when the walkthrough is finished,
    ///   - after <see cref="advanceDelay"/> s, NextInput.RaiseNext() (exactly what the Next button does), <c>advance</c>
    ///     times; cancelled if the state changed meanwhile (the user pressed Next/Reset or the walkthrough switched).
    /// Live-tunable through RemoteConfig ("StepCelebration.autoAdvance": false, ...).
    /// </summary>
    public class StepCelebration : MonoBehaviour
    {
        [SerializeField] CloudVisionClassifier cloud;
        [SerializeField] NextInput input;
        [SerializeField] PieceTracker tracker;
        [SerializeField] AudioSource audioSource;
        [SerializeField] ParticleSystem confetti;

        [SerializeField] bool celebrate = true;
        [SerializeField] bool autoAdvance = true;
        [Tooltip("Seconds between the celebration and advancing (so the burst is seen before the text changes).")]
        [SerializeField] float advanceDelay = 1f;
        [Tooltip("Seconds between Next presses when one event completes several steps (> NextInput debounce 0.3 s).")]
        [SerializeField] float multiAdvanceGap = 0.45f;
        [SerializeField, Range(0f, 1f)] float volume = 0.8f;
        [SerializeField] int burstCount = 60;
        [SerializeField] int finalBurstCount = 150;
        [Tooltip("Confetti starts this far (m) above the piece.")]
        [SerializeField] float burstHeight = 0.04f;
        [Tooltip("No tracked piece: burst this far (m) in front of the head, a little below eye level.")]
        [SerializeField] float fallbackDistance = 0.35f;

        string lastCelebratedState;
        AudioClip chime, finalChime;
        Coroutine pending;

        public void Configure(CloudVisionClassifier c, NextInput n, PieceTracker t, AudioSource a, ParticleSystem p)
        {
            cloud = c;
            input = n;
            tracker = t;
            audioSource = a;
            confetti = p;
        }

        void Awake()
        {
            if (cloud == null) cloud = FindAnyObjectByType<CloudVisionClassifier>();
            if (input == null) input = FindAnyObjectByType<NextInput>();
            if (tracker == null) tracker = FindAnyObjectByType<PieceTracker>();
            if (audioSource == null) audioSource = GetComponent<AudioSource>();
            if (confetti == null) confetti = GetComponentInChildren<ParticleSystem>(true);
            if (confetti != null) ConfigureConfetti(confetti);
            chime = BuildChime(false);
            finalChime = BuildChime(true);
        }

        void OnEnable()
        {
            if (cloud != null) cloud.StepCompleted += OnStepCompleted;
        }

        void OnDisable()
        {
            if (cloud != null) cloud.StepCompleted -= OnStepCompleted;
        }

        void OnDestroy()
        {
            if (chime != null) Destroy(chime);
            if (finalChime != null) Destroy(finalChime);
        }

        void Update()
        {
            // Once the walkthrough has left a celebrated state, that state may be celebrated again (e.g. after Reset).
            if (lastCelebratedState != null && cloud != null && cloud.WalkthroughState != lastCelebratedState)
                lastCelebratedState = null;
        }

        void OnStepCompleted(RelayStepComplete step)
        {
            if (step == null || string.IsNullOrEmpty(step.state) || step.state == lastCelebratedState) return;
            lastCelebratedState = step.state;
            var pos = AnchorPosition(step, out var source);
            ForgeLog.Diag($"STEP complete {step.state} via {step.via} (advance {step.advance}, final {step.final}) " +
                          $"at {source} {pos}");
            if (celebrate) Celebrate(pos, step.final);
            if (autoAdvance && input != null)
            {
                if (pending != null) StopCoroutine(pending);
                pending = StartCoroutine(Advance(step));
            }
        }

        IEnumerator Advance(RelayStepComplete step)
        {
            yield return new WaitForSecondsRealtime(advanceDelay);
            var expected = step.state;
            var n = Mathf.Clamp(step.advance, 1, 4);
            for (var i = 0; i < n; i++)
            {
                if (i > 0) yield return new WaitForSecondsRealtime(multiAdvanceGap);
                if (cloud == null || cloud.WalkthroughState != expected)
                {
                    ForgeLog.Diag($"STEP auto-advance stopped: state is now {cloud?.WalkthroughState} (expected {expected})");
                    break;
                }
                input.RaiseNext(); // QuestWalkthroughDriver.OnNext runs synchronously and updates WalkthroughState
                if (cloud.WalkthroughState == expected)
                {
                    ForgeLog.Diag("STEP auto-advance: Next was ignored (debounce?)");
                    break;
                }
                expected = cloud.WalkthroughState;
            }
            pending = null;
        }

        Vector3 AnchorPosition(RelayStepComplete step, out string source)
        {
            if (tracker != null)
            {
                if (!string.IsNullOrEmpty(step.anchorPart) && tracker.TryGetPosition(step.anchorPart, out var p))
                {
                    source = step.anchorPart;
                    return p;
                }
                if (step.parts != null)
                    foreach (var part in step.parts)
                        if (!string.IsNullOrEmpty(part) && tracker.TryGetPosition(part, out p))
                        {
                            source = part;
                            return p;
                        }
            }
            var cam = Camera.main;
            if (cam != null)
            {
                var fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
                if (fwd.sqrMagnitude < 1e-4f) fwd = cam.transform.forward;
                source = "in front of the user";
                return cam.transform.position + fwd.normalized * fallbackDistance + Vector3.down * 0.2f;
            }
            source = "origin";
            return transform.position;
        }

        void Celebrate(Vector3 pos, bool final)
        {
            transform.position = pos;
            if (audioSource != null)
            {
                var clip = final ? finalChime : chime;
                if (clip != null) audioSource.PlayOneShot(clip, volume);
            }
            if (confetti != null)
            {
                confetti.transform.position = pos + Vector3.up * burstHeight;
                confetti.Emit(final ? finalBurstCount : burstCount);
                if (final) StartCoroutine(SecondBurst());
            }
        }

        IEnumerator SecondBurst()
        {
            yield return new WaitForSecondsRealtime(0.35f);
            if (confetti != null) confetti.Emit(finalBurstCount / 2);
        }

        /// <summary>Confetti look: small fluttering coloured flakes thrown up and falling (used by setup and Awake).</summary>
        public static void ConfigureConfetti(ParticleSystem ps)
        {
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.duration = 1f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.0f, 1.8f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 1.1f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.005f, 0.011f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.gravityModifier = 0.35f;
            main.maxParticles = 600;
            var colors = new Gradient();
            colors.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.25f, 0.3f), 0f), new GradientColorKey(new Color(1f, 0.8f, 0.1f), 0.2f),
                    new GradientColorKey(new Color(0.3f, 0.9f, 0.35f), 0.4f), new GradientColorKey(new Color(0.2f, 0.75f, 1f), 0.6f),
                    new GradientColorKey(new Color(0.55f, 0.35f, 1f), 0.8f), new GradientColorKey(new Color(1f, 0.4f, 0.85f), 1f),
                },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            main.startColor = new ParticleSystem.MinMaxGradient(colors) { mode = ParticleSystemGradientMode.RandomColor };

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f; // bursts come from Emit()

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 30f;
            shape.radius = 0.01f;
            shape.rotation = new Vector3(-90f, 0f, 0f); // cone axis (+Z) pointing up

            var drag = ps.limitVelocityOverLifetime;
            drag.enabled = true;
            drag.limit = 10f;
            drag.drag = 1.5f;

            var spin = ps.rotationOverLifetime;
            spin.enabled = true;
            spin.z = new ParticleSystem.MinMaxCurve(-8f, 8f);

            var fade = ps.colorOverLifetime;
            fade.enabled = true;
            var alpha = new Gradient();
            alpha.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                          new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.7f), new GradientAlphaKey(0f, 1f) });
            fade.color = new ParticleSystem.MinMaxGradient(alpha);

            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.renderMode = ParticleSystemRenderMode.Billboard;
                if (renderer.sharedMaterial == null)
                {
                    var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
                    if (shader != null) renderer.sharedMaterial = new Material(shader);
                }
            }
        }

        /// <summary>Rising arpeggio of decaying sines with a soft octave shimmer (C major; 4 notes when final).</summary>
        static AudioClip BuildChime(bool final)
        {
            const int rate = 44100;
            float[] notes = final ? new[] { 523.25f, 659.25f, 783.99f, 1046.5f } : new[] { 659.25f, 783.99f, 1046.5f };
            var noteGap = final ? 0.11f : 0.085f;
            var tail = final ? 1.1f : 0.6f;
            var len = (int)(rate * (noteGap * notes.Length + tail));
            var data = new float[len];
            for (var n = 0; n < notes.Length; n++)
            {
                var start = (int)(n * noteGap * rate);
                var f = notes[n];
                for (var i = start; i < len; i++)
                {
                    var t = (i - start) / (float)rate;
                    var env = Mathf.Exp(-t * (final ? 4.5f : 6f)) * Mathf.Min(1f, t / 0.004f);
                    var s = Mathf.Sin(2f * Mathf.PI * f * t) + 0.25f * Mathf.Sin(4f * Mathf.PI * f * t) * Mathf.Exp(-t * 12f);
                    data[i] += 0.2f * env * s;
                }
            }
            var clip = AudioClip.Create(final ? "ForgeChimeFinal" : "ForgeChime", len, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
