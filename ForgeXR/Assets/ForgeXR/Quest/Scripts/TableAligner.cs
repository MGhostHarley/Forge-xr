using UnityEngine;
using UnityEngine.Events;

namespace Forge
{
    /// <summary>
    /// Places the staging mat on the real table (point-and-drag), then locks it.
    /// CONTRACT STUB: chunk 3 (ps/table-align) implements alignment without changing these public members.
    /// Other components must treat "no TableAligner in the scene" as already locked (see IsSceneLocked),
    /// so chunks 2 and 4 work without chunk 3.
    /// </summary>
    public class TableAligner : MonoBehaviour
    {
        [Tooltip("The StagingMat transform that gets moved onto the table.")]
        [SerializeField] protected Transform mat;

        public UnityEvent onAligned = new();
        public UnityEvent onRealign = new();

        public Transform Mat => mat;
        public bool IsLocked { get; protected set; }

        public void SetMat(Transform t) => mat = t;

        /// <summary>True if no TableAligner exists or the existing one is locked.</summary>
        public static bool IsSceneLocked()
        {
            var a = FindAnyObjectByType<TableAligner>();
            return a == null || a.IsLocked;
        }
    }
}
