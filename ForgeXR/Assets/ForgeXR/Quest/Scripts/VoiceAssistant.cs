using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;

namespace Forge
{
    // Voice observes the walkthrough. All progress and highlights belong to the existing driver.
    internal sealed class VoiceAssistant : MonoBehaviour
    {
        const float MinimumHold = 0.3f;
        const int MaxSeconds = 6;
        const int ReplyRate = 24000;

        QuestWalkthroughDriver driver;
        CloudVisionClassifier relay;
        CameraFrameSource cameraSource;
        AudioSource speaker;
        InputAction ask, hear;
        AudioClip recording, generatedReply;
        UnityWebRequest pending;
        Coroutine question;
        byte[] frame;
        float pressedAt = -1;
        int revision;

        [Serializable]
        sealed class Question
        {
            public string audio, image, walkthrough, instruction, part;
            public int step;
        }

        internal void Initialize(QuestWalkthroughDriver walkthrough)
        {
            driver = walkthrough;
            relay = FindAnyObjectByType<CloudVisionClassifier>();
            cameraSource = FindAnyObjectByType<CameraFrameSource>();
            speaker = gameObject.AddComponent<AudioSource>();
            speaker.playOnAwake = false;
            speaker.spatialBlend = 0;
            revision = driver.VoiceRevision;

            ask = new InputAction("Forge Ask", InputActionType.Button);
            ask.AddBinding("<XRController>{LeftHand}/{GripButton}");
            ask.AddBinding("<Keyboard>/v");
            ask.performed += _ => Press();
            ask.canceled += _ => Release();
            hear = new InputAction("Forge Hear Step", InputActionType.Button, "<Keyboard>/h");
            hear.performed += _ => HearStep();
            ask.Enable();
            hear.Enable();
        }

        void OnEnable()
        {
            ask?.Enable();
            hear?.Enable();
        }

        void Update()
        {
            if (driver == null) return;
            if (revision != driver.VoiceRevision)
            {
                Cancel();
                revision = driver.VoiceRevision;
            }
            if (pressedAt < 0) return;
            var held = Time.unscaledTime - pressedAt;
            if (held >= MaxSeconds)
                Release();
            else if (held >= MinimumHold && recording == null && !micAttempted)
                StartRecording();
        }

        bool micAttempted;

        void Press()
        {
            Cancel();
            revision = driver.VoiceRevision;
            pressedAt = Time.unscaledTime;
        }

        void StartRecording()
        {
            micAttempted = true;
            StopReply();
            if (relay == null || question != null) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
            {
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
                return;
            }
#endif
            try
            {
                if (Microphone.devices.Length != 0)
                    recording = Microphone.Start(null, false, MaxSeconds + 1, 16000);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Forge] Voice microphone unavailable: {e.Message}");
            }
        }

        void Release()
        {
            if (pressedAt < 0) return;
            bool tapped = Time.unscaledTime - pressedAt < MinimumHold;
            pressedAt = -1;
            micAttempted = false;
            if (revision != driver.VoiceRevision)
            {
                Cancel();
                return;
            }
            if (recording == null)
            {
                HearStep();
                return;
            }
            int frames = Microphone.GetPosition(null);
            Microphone.End(null);
            var clip = recording;
            recording = null;
            byte[] wav = !tapped && frames >= clip.frequency / 5
                ? EncodeWav(clip, Math.Min(frames, clip.samples)) : null;
            Destroy(clip);
            if (wav == null)
                HearStep();
            else
                question = StartCoroutine(Ask(wav));
        }

        IEnumerator Ask(byte[] wav)
        {
            int askedRevision = revision;
            var payload = new Question
            {
                audio = Convert.ToBase64String(wav),
                walkthrough = FlowName,
                step = driver.VoiceStep,
                instruction = driver.VoiceInstruction,
                part = driver.VoicePart
            };
            if (cameraSource != null)
            {
                cameraSource.FrameReady += Capture;
                float deadline = Time.unscaledTime + 0.6f;
                while (frame == null && Time.unscaledTime < deadline)
                    yield return null;
                cameraSource.FrameReady -= Capture;
                if (frame != null) payload.image = Convert.ToBase64String(frame);
                frame = null;
            }

            pending = new UnityWebRequest(relay.RelayUrl + "/ask", "POST");
            pending.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload)));
            pending.uploadHandler.contentType = "application/json";
            pending.downloadHandler = new DownloadHandlerBuffer();
            pending.timeout = 22;
            yield return pending.SendWebRequest();

            var pcm = pending.result == UnityWebRequest.Result.Success
                && pending.GetResponseHeader("Content-Type") == "application/octet-stream"
                ? pending.downloadHandler.data : null;
            pending.Dispose();
            pending = null;
            question = null;
            if (askedRevision == driver.VoiceRevision && !PlayPcm(pcm))
                PlayStep();
        }

        string FlowName => driver.CurrentFlow == QuestWalkthroughDriver.Flow.Wheel ? "wheel" : "driver";

        void Capture(CameraFrame image, CaptureInfo info)
        {
            if (frame == null) frame = relay.Encode(image);
        }

        void HearStep()
        {
            Cancel();
            revision = driver.VoiceRevision;
            PlayStep();
        }

        void PlayStep()
        {
            var clip = Resources.Load<AudioClip>($"ForgeVoice/{FlowName}-{driver.VoiceStep}");
            if (clip != null) speaker.PlayOneShot(clip);
        }

        bool PlayPcm(byte[] pcm)
        {
            if (pcm == null || pcm.Length == 0 || pcm.Length % 2 != 0 || pcm.Length > ReplyRate * 2 * 30)
                return false;
            var samples = new float[pcm.Length / 2];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = (short)(pcm[2 * i] | pcm[2 * i + 1] << 8) / 32768f;
            StopReply();
            generatedReply = AudioClip.Create("Forge voice reply", samples.Length, 1, ReplyRate, false);
            generatedReply.SetData(samples, 0);
            speaker.PlayOneShot(generatedReply);
            return true;
        }

        static byte[] EncodeWav(AudioClip clip, int frames)
        {
            var samples = new float[frames * clip.channels];
            if (!clip.GetData(samples, 0)) return null;
            float peak = 0;
            for (int i = 0; i < frames; i++)
                peak = Mathf.Max(peak, Mathf.Abs(samples[i * clip.channels]));
            if (peak < 0.005f) return null;

            using var buffer = new MemoryStream();
            using var writer = new BinaryWriter(buffer);
            int size = frames * 2;
            writer.Write(new[] { 'R', 'I', 'F', 'F' });
            writer.Write(36 + size);
            writer.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(clip.frequency);
            writer.Write(clip.frequency * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(new[] { 'd', 'a', 't', 'a' });
            writer.Write(size);
            for (int i = 0; i < frames; i++)
                writer.Write((short)(Mathf.Clamp(samples[i * clip.channels], -1f, 1f) * 32767));
            return buffer.ToArray();
        }

        void StopReply()
        {
            if (speaker != null) speaker.Stop();
            if (generatedReply != null) Destroy(generatedReply);
            generatedReply = null;
        }

        void Cancel()
        {
            pressedAt = -1;
            micAttempted = false;
            if (recording != null)
            {
                Microphone.End(null);
                Destroy(recording);
                recording = null;
            }
            if (cameraSource != null) cameraSource.FrameReady -= Capture;
            frame = null;
            var request = pending;
            pending = null;
            request?.Abort();
            if (question != null) StopCoroutine(question);
            question = null;
            request?.Dispose();
            StopReply();
        }

        void OnApplicationPause(bool paused)
        {
            if (paused) Cancel();
        }

        void OnDisable()
        {
            Cancel();
            ask?.Disable();
            hear?.Disable();
        }

        void OnDestroy()
        {
            ask?.Dispose();
            hear?.Dispose();
        }
    }
}
