using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;

namespace Forge
{
    /// <summary>
    /// Diagnostics sink: logcat (tag Unity, prefix [ForgeDiag]) plus a file copy that survives a short
    /// logcat buffer: Application.persistentDataPath/forge_diag.log, truncated at app start.
    /// Pull it with: adb pull /sdcard/Android/data/&lt;package&gt;/files/forge_diag.log
    /// </summary>
    public static class ForgeLog
    {
        public const string FileName = "forge_diag.log";
        static StreamWriter writer;
        static bool fileFailed;

        /// <summary>Logs to logcat and the file.</summary>
        public static void Diag(string message)
        {
            Debug.Log("[ForgeDiag] " + message);
            ToFile(message);
        }

        /// <summary>File only (for the periodic summary, so logcat stays readable).</summary>
        public static void ToFile(string message)
        {
            if (fileFailed) return;
            try
            {
                if (writer == null)
                {
                    var path = System.IO.Path.Combine(Application.persistentDataPath, FileName);
                    writer = new StreamWriter(path, append: false) { AutoFlush = true };
                    writer.WriteLine($"# Forge diagnostics {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                }
                writer.WriteLine($"{Time.unscaledTime,9:F2} {message}");
            }
            catch (System.Exception e)
            {
                fileFailed = true;
                Debug.LogWarning($"[ForgeDiag] file log disabled: {e.Message}");
            }
        }

        /// <summary>"Parent/Name" for readable interactor ids.</summary>
        public static string Path(Transform t) => t == null ? "-" : (t.parent != null ? t.parent.name + "/" : "") + t.name;

#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            writer?.Dispose();
            writer = null;
            fileFailed = false;
        }
#endif
    }

    /// <summary>
    /// TEMPORARY headset diagnostics. Logs CHANGES only (to logcat and forge_diag.log):
    /// interactor enable/disable, hover target (with near/far source for NearFar), select target,
    /// pinch/select-input edges, button hover/select/press, highlight-set changes and FloatingCue
    /// mode/moves. Pinch-filter rejections are logged by PokeButton. A once-per-second summary
    /// (cast origins, hovered targets, distance to the nearest button) goes to the file only.
    /// Remove after debugging.
    /// </summary>
    public class ForgeDiagnostics : MonoBehaviour
    {
        [SerializeField] float summaryInterval = 1f;
        [SerializeField] float rescanInterval = 2f;

        class State
        {
            public bool enabled;
            public string hover = "", select = "";
            public bool pinch;
        }

        readonly Dictionary<XRBaseInteractor, State> states = new();
        XRBaseInteractor[] interactors = System.Array.Empty<XRBaseInteractor>();
        PokeButton[] buttons = System.Array.Empty<PokeButton>();
        float nextSummary, nextRescan;
        string highlightSig;

        void Start()
        {
            ForgeLog.Diag($"start {Application.productName} {Application.version} log={System.IO.Path.Combine(Application.persistentDataPath, ForgeLog.FileName)}");
            buttons = FindObjectsByType<PokeButton>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var b in buttons)
            {
                var label = b.name;
                if (b.TryGetComponent<XRSimpleInteractable>(out var si))
                {
                    si.hoverEntered.AddListener(a => ForgeLog.Diag($"BTN HOVER+ {label} by {ForgeLog.Path(a.interactorObject?.transform)}"));
                    si.hoverExited.AddListener(a => ForgeLog.Diag($"BTN HOVER- {label} by {ForgeLog.Path(a.interactorObject?.transform)}"));
                    si.selectEntered.AddListener(a => ForgeLog.Diag($"BTN SELECT {label} by {ForgeLog.Path(a.interactorObject?.transform)}"));
                    ForgeLog.Diag($"button {label} active={b.isActiveAndEnabled} layers={si.interactionLayers.value} colliders={si.colliders.Count} pos={b.transform.position:F2}");
                }
                else ForgeLog.Diag($"button {label} has NO XRSimpleInteractable");
                b.onPressed.AddListener(() => ForgeLog.Diag($"BTN PRESSED {label}"));
            }
            Rescan();
        }

        void Rescan()
        {
            var list = new List<XRBaseInteractor>();
            foreach (var i in FindObjectsByType<XRBaseInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (i is XRPokeInteractor || i is NearFarInteractor) list.Add(i);
            interactors = list.ToArray();
        }

        void Update()
        {
            var now = Time.unscaledTime;
            if (now >= nextRescan) { nextRescan = now + rescanInterval; Rescan(); }

            foreach (var i in interactors)
            {
                if (i == null) continue;
                if (!states.TryGetValue(i, out var s)) { s = new State(); states[i] = s; s.enabled = !i.isActiveAndEnabled; }
                var id = ForgeLog.Path(i.transform);

                var en = i.isActiveAndEnabled;
                if (en != s.enabled) { s.enabled = en; ForgeLog.Diag($"{id} {(en ? "ENABLED" : "DISABLED")}"); }

                var hover = HoverName(i);
                if (hover != s.hover) { s.hover = hover; ForgeLog.Diag($"{id} hover={(hover == "" ? "-" : hover)}"); }

                var sel = i.interactablesSelected.Count > 0 ? i.interactablesSelected[0].transform.name : "";
                if (sel != s.select) { s.select = sel; ForgeLog.Diag($"{id} select={(sel == "" ? "-" : sel)}"); }

                if (i is XRBaseInputInteractor input && input.selectInput != null)
                {
                    var pinch = en && input.selectInput.ReadIsPerformed();
                    if (pinch != s.pinch) { s.pinch = pinch; ForgeLog.Diag($"{id} selectInput={(pinch ? "DOWN" : "up")} hover={(hover == "" ? "-" : hover)}"); }
                }
            }

            var sig = string.Join(",", HighlightRegistry.ActiveIds);
            if (sig != highlightSig) { highlightSig = sig; ForgeLog.Diag($"highlight=[{sig}]"); }

            if (now >= nextSummary)
            {
                nextSummary = now + summaryInterval;
                ForgeLog.ToFile(Summary());
            }
        }

        static string HoverName(XRBaseInteractor i)
        {
            if (!i.isActiveAndEnabled || i.interactablesHovered.Count == 0) return "";
            var name = i.interactablesHovered[0].transform.name;
            if (i is NearFarInteractor nf)
                name += nf.TryGetCurveEndPoint(out _) == EndPointType.ValidCastHit ? "(far)" : "(near)";
            return name;
        }

        string Summary()
        {
            var sb = new StringBuilder("SUM ");
            foreach (var i in interactors)
            {
                if (i == null || !i.isActiveAndEnabled) continue;
                var origin = i.transform.position;
                if (i is NearFarInteractor nf && nf.nearInteractionCaster?.effectiveCastOrigin != null)
                    origin = nf.nearInteractionCaster.effectiveCastOrigin.position;
                var nearest = float.PositiveInfinity;
                foreach (var b in buttons)
                    if (b != null) nearest = Mathf.Min(nearest, Vector3.Distance(origin, b.transform.position));
                sb.Append($"{ForgeLog.Path(i.transform)} o={origin:F2} hov={HoverName(i)} sel={i.interactablesSelected.Count} nearBtn={nearest:F2}");
                if (i is NearFarInteractor nf2 && nf2.TryGetCurveEndPoint(out var end) != EndPointType.None)
                    sb.Append($" rayEnd={end:F2}");
                sb.Append(" | ");
            }
            var cue = FindAnyObjectByType<FloatingCue>();
            if (cue != null) sb.Append($"cue={cue.CurrentMode} at {cue.transform.position:F2}");
            return sb.ToString();
        }
    }
}
