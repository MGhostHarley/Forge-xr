using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace Forge
{
    /// <summary>
    /// Downloads the active generated walkthrough from the laptop relay (GET {relay}/walkthrough) every few seconds
    /// and loads it into <see cref="GeneratedWalkthrough"/> when it changes. The operator switches walkthroughs on the
    /// laptop (POST /walkthrough?id=...); with &amp;select=1 the relay bumps selectNonce and this raises
    /// <see cref="SelectRequested"/> so QuestWalkthroughDriver switches the headset to it.
    /// </summary>
    public class WalkthroughLoader : MonoBehaviour
    {
        [SerializeField] GeneratedWalkthrough target;
        [SerializeField] CloudVisionClassifier cloud;
        [SerializeField] float pollSeconds = 4f;

        /// <summary>A new or changed walkthrough was loaded.</summary>
        public event Action<GeneratedWalkthrough> Loaded;
        /// <summary>The operator asked (on the laptop) to show the generated walkthrough now.</summary>
        public event Action SelectRequested;

        string lastJson;
        int lastNonce = -1;

        public void Configure(GeneratedWalkthrough t, CloudVisionClassifier c)
        {
            target = t;
            cloud = c;
        }

        void Start()
        {
            if (target == null) target = GetComponent<GeneratedWalkthrough>();
            if (cloud == null) cloud = FindAnyObjectByType<CloudVisionClassifier>();
            StartCoroutine(Poll());
        }

        IEnumerator Poll()
        {
            while (true)
            {
                if (target != null && cloud != null && !string.IsNullOrEmpty(cloud.RelayUrl))
                {
                    using var req = UnityWebRequest.Get(cloud.RelayUrl + "/walkthrough");
                    req.timeout = 4;
                    yield return req.SendWebRequest();
                    if (req.result == UnityWebRequest.Result.Success) Apply(req.downloadHandler.text);
                }
                yield return new WaitForSecondsRealtime(pollSeconds);
            }
        }

        void Apply(string json)
        {
            GeneratedWalkthrough.Spec spec;
            try { spec = JsonUtility.FromJson<GeneratedWalkthrough.Spec>(json); }
            catch (ArgumentException e) { ForgeLog.Diag("WALKTHROUGH bad JSON: " + e.Message); return; }
            if (spec == null) return;

            // The select request is only a nonce: compare it, then ignore it for the "changed?" test.
            var nonce = spec.selectNonce;
            var body = json.Replace("\"selectNonce\": " + nonce, "");
            if (body != lastJson)
            {
                lastJson = body;
                target.Load(spec);
                ForgeLog.Diag($"WALKTHROUGH loaded {spec.id} \"{spec.title}\" steps={spec.steps?.Length ?? 0} " +
                              $"detectable=[{string.Join(",", spec.detectable ?? Array.Empty<string>())}]");
                Loaded?.Invoke(target);
            }
            if (lastNonce >= 0 && nonce != lastNonce && target.HasSpec)
            {
                ForgeLog.Diag($"WALKTHROUGH select requested ({spec.id})");
                SelectRequested?.Invoke();
            }
            lastNonce = nonce;
        }
    }
}
