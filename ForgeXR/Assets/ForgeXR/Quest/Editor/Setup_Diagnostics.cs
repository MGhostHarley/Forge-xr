using UnityEngine;

namespace Forge.EditorTools
{
    /// <summary>TEMPORARY: adds ForgeRig/Diagnostics (ForgeDiagnostics). Set Enabled = false and rerun setup to remove.</summary>
    public static class SetupDiagnostics
    {
        const bool Enabled = true;
        const string Name = "Diagnostics";

        [ForgeSetupStep(900)]
        public static void AddDiagnostics()
        {
            var rig = ForgeSetupUtil.GetOrCreateRoot(Forge.ForgeScene.RigRoot);
            var existing = rig.transform.Find(Name);
            if (!Enabled)
            {
                if (existing != null) Object.DestroyImmediate(existing.gameObject);
                return;
            }
            var go = ForgeSetupUtil.GetOrCreateChild(rig.transform, Name);
            ForgeSetupUtil.GetOrAdd<Forge.ForgeDiagnostics>(go);
        }
    }
}
