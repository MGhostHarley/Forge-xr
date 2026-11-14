using System.Text;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace Forge
{
    /// <summary>
    /// TEMPORARY headset diagnostics (logcat tag Unity, prefix [ForgeDiag]). Once per second logs every
    /// interactor (poke + near-far): enabled, world position, hover/select counts, and distance to the
    /// nearest PokeButton. Also logs hover/select events on every PokeButton. Remove after debugging.
    /// </summary>
    public class ForgeDiagnostics : MonoBehaviour
    {
        [SerializeField] float interval = 1f;
        float next;
        PokeButton[] buttons;

        void Start()
        {
            buttons = FindObjectsByType<PokeButton>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var b in buttons)
            {
                var label = b.name;
                if (b.TryGetComponent<XRSimpleInteractable>(out var si))
                {
                    si.hoverEntered.AddListener(a => Debug.Log($"[ForgeDiag] HOVER+ {label} by {a.interactorObject?.transform.name} ({a.interactorObject?.GetType().Name})"));
                    si.hoverExited.AddListener(a => Debug.Log($"[ForgeDiag] HOVER- {label}"));
                    si.selectEntered.AddListener(a => Debug.Log($"[ForgeDiag] SELECT {label} by {a.interactorObject?.transform.name} ({a.interactorObject?.GetType().Name})"));
                    Debug.Log($"[ForgeDiag] button {label} active={b.isActiveAndEnabled} layers={si.interactionLayers.value} colliders={si.colliders.Count} pos={b.transform.position}");
                }
                else Debug.Log($"[ForgeDiag] button {label} has NO XRSimpleInteractable");
                b.onPressed.AddListener(() => Debug.Log($"[ForgeDiag] PRESSED {label}"));
            }
        }

        void Update()
        {
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + interval;

            var sb = new StringBuilder("[ForgeDiag] ");
            foreach (var i in FindObjectsByType<XRBaseInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (i is not XRPokeInteractor && i is not NearFarInteractor) continue;
                var p = i.transform.position;
                var nearest = float.PositiveInfinity;
                if (buttons != null)
                    foreach (var b in buttons)
                        if (b != null) nearest = Mathf.Min(nearest, Vector3.Distance(p, b.transform.position));
                sb.Append($"{i.transform.parent?.name}/{i.GetType().Name} en={i.isActiveAndEnabled} pos=({p.x:F2},{p.y:F2},{p.z:F2}) hov={i.interactablesHovered.Count} sel={i.interactablesSelected.Count} nearBtn={nearest:F2} | ");
            }
            var aligner = FindAnyObjectByType<TableAligner>();
            sb.Append($"locked={(aligner == null ? "noAligner" : aligner.IsLocked.ToString())}");
            Debug.Log(sb.ToString());
        }
    }
}
