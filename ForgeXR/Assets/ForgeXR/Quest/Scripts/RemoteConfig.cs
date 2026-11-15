using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace Forge
{
    /// <summary>
    /// Live tuning from the laptop: every few seconds GET {relay}/config, a flat JSON object such as
    ///   { "PieceTracker.maxMissedAnswers": 4, "ColorPieceTracker.minMatch": 0.15, "PickupSpot.activeLineWidth": 0.005 }
    /// and set those fields (by name, public or [SerializeField] private) on every component of that type in the scene.
    /// Numbers and true/false only. Unknown names are logged once. Lets tracking/display be tuned without a Quest rebuild.
    /// </summary>
    public class RemoteConfig : MonoBehaviour
    {
        [SerializeField] CloudVisionClassifier cloud;
        [SerializeField] float pollSeconds = 3f;

        static readonly Regex Pair = new("\"([A-Za-z_][\\w]*)\\.([A-Za-z_][\\w]*)\"\\s*:\\s*(true|false|-?[0-9.eE+-]+)");
        static readonly Dictionary<string, Type> Types = new()
        {
            ["PieceTracker"] = typeof(PieceTracker), ["ColorPieceTracker"] = typeof(ColorPieceTracker),
            ["CloudVisionClassifier"] = typeof(CloudVisionClassifier), ["TableSurface"] = typeof(TableSurface),
            ["PickupSpot"] = typeof(PickupSpot), ["CameraFrameSource"] = typeof(CameraFrameSource),
            ["PieceLocator"] = typeof(PieceLocator), ["FloatingCue"] = typeof(FloatingCue),
            ["StepCelebration"] = typeof(StepCelebration),
            ["QuestWalkthroughDriver"] = typeof(QuestWalkthroughDriver),
        };

        string lastJson;
        readonly HashSet<string> warned = new();

        void Start()
        {
            if (cloud == null) cloud = FindAnyObjectByType<CloudVisionClassifier>();
            StartCoroutine(Poll());
        }

        IEnumerator Poll()
        {
            while (true)
            {
                if (cloud != null && !string.IsNullOrEmpty(cloud.RelayUrl))
                {
                    using var req = UnityWebRequest.Get(cloud.RelayUrl + "/config");
                    req.timeout = 3;
                    yield return req.SendWebRequest();
                    if (req.result == UnityWebRequest.Result.Success && req.downloadHandler.text != lastJson)
                    {
                        lastJson = req.downloadHandler.text;
                        Apply(lastJson);
                    }
                }
                yield return new WaitForSecondsRealtime(pollSeconds);
            }
        }

        void Apply(string json)
        {
            var applied = new List<string>();
            foreach (Match m in Pair.Matches(json))
            {
                string typeName = m.Groups[1].Value, field = m.Groups[2].Value, raw = m.Groups[3].Value;
                if (!Types.TryGetValue(typeName, out var type)) { Warn($"unknown component {typeName}"); continue; }
                var fi = type.GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fi == null) { Warn($"unknown field {typeName}.{field}"); continue; }
                object value;
                try
                {
                    if (fi.FieldType == typeof(float)) value = float.Parse(raw, CultureInfo.InvariantCulture);
                    else if (fi.FieldType == typeof(int)) value = (int)Math.Round(double.Parse(raw, CultureInfo.InvariantCulture));
                    else if (fi.FieldType == typeof(bool)) value = raw == "true";
                    else { Warn($"{typeName}.{field} is {fi.FieldType.Name}, not number/bool"); continue; }
                }
                catch (FormatException) { Warn($"bad value {raw} for {typeName}.{field}"); continue; }

                var n = 0;
                foreach (var c in FindObjectsByType(type, FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    fi.SetValue(c, value);
                    n++;
                }
                applied.Add($"{typeName}.{field}={raw} (x{n})");
            }
            ForgeLog.Diag("CONFIG applied " + (applied.Count > 0 ? string.Join(", ", applied) : "nothing"));
        }

        void Warn(string msg)
        {
            if (warned.Add(msg)) ForgeLog.Diag("CONFIG " + msg);
        }
    }
}
