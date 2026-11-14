using UnityEngine;

namespace Forge
{
    /// <summary>
    /// AR-minimal mat visuals: the mat frame, fill and builder arrow are only shown while a
    /// TableAligner exists and is not locked (i.e. while the wearer is aligning the mat).
    /// With no TableAligner, or once it locks, only the pickup spots remain visible.
    /// Also applies the per-part colours via MaterialPropertyBlock (one shared material).
    /// </summary>
    public class StagingMatVisuals : MonoBehaviour
    {
        [SerializeField] MeshRenderer frame;
        [SerializeField] MeshRenderer fill;
        [SerializeField] MeshRenderer arrow;

        [SerializeField] Color frameColor = new(1f, 1f, 1f, 0.45f);
        [SerializeField] Color fillColor = new(1f, 1f, 1f, 0.07f);
        [SerializeField] Color arrowColor = new(1f, 1f, 1f, 0.6f);

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        TableAligner aligner;
        bool shown = true;
        float nextSearch;

        void Awake()
        {
            var mpb = new MaterialPropertyBlock();
            Tint(frame, frameColor, mpb);
            Tint(fill, fillColor, mpb);
            Tint(arrow, arrowColor, mpb);
        }

        void OnEnable()
        {
            FindAligner();
            Refresh();
        }

        void OnDisable() => Unsubscribe();

        void Update()
        {
            // Cheap safety net in case the aligner appears later or changes state without an event.
            if (aligner == null && Time.unscaledTime >= nextSearch)
            {
                nextSearch = Time.unscaledTime + 1f;
                FindAligner();
            }
            Refresh();
        }

        void FindAligner()
        {
            var a = FindAnyObjectByType<TableAligner>();
            if (a == aligner) return;
            Unsubscribe();
            aligner = a;
            if (aligner != null)
            {
                aligner.onAligned.AddListener(OnAligned);
                aligner.onRealign.AddListener(OnRealign);
            }
        }

        void Unsubscribe()
        {
            if (aligner == null) return;
            aligner.onAligned.RemoveListener(OnAligned);
            aligner.onRealign.RemoveListener(OnRealign);
            aligner = null;
        }

        void OnAligned() => SetShown(false);
        void OnRealign() => SetShown(true);

        void Refresh() => SetShown(aligner != null && !aligner.IsLocked);

        void SetShown(bool on)
        {
            if (on == shown) return;
            shown = on;
            if (frame != null) frame.enabled = on;
            if (fill != null) fill.enabled = on;
            if (arrow != null) arrow.enabled = on;
        }

        static void Tint(Renderer r, Color c, MaterialPropertyBlock mpb)
        {
            if (r == null) return;
            r.GetPropertyBlock(mpb);
            mpb.SetColor(BaseColorId, c);
            r.SetPropertyBlock(mpb);
        }
    }
}
