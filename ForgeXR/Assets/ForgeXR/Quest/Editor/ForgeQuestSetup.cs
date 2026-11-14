using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Forge.EditorTools
{
    /// <summary>
    /// Marks a static, parameterless method as one step of the Quest scene setup.
    /// Steps run in ascending <see cref="Order"/>. Ranges by chunk:
    ///   100-199 passthrough/project settings, 200-299 pickup spots/staging mat,
    ///   300-399 table alignment, 400-499 next input/walkthrough.
    /// Every step must be idempotent (find-or-create by name, safe to run twice).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ForgeSetupStepAttribute : Attribute
    {
        public int Order { get; }
        public ForgeSetupStepAttribute(int order) { Order = order; }
    }

    /// <summary>
    /// Runs every [ForgeSetupStep] against the Quest scene, then saves the scene and assets.
    /// Scene and settings YAML are only ever changed through these steps, so feature branches
    /// commit code only and the merged branch regenerates the scene once.
    ///   Unity -batchmode -projectPath ForgeXR -buildTarget Android -executeMethod Forge.EditorTools.ForgeQuestSetup.Run -quit
    /// </summary>
    public static class ForgeQuestSetup
    {
        /// <summary>The Quest scene all setup steps act on. Becomes the only build scene.</summary>
        public const string ScenePath = "Assets/Scenes/ForgeQuest.unity";

        /// <summary>Clean scene (Hands rig at the origin) that ForgeQuest is copied from once.
        /// ForgeBuild.CreateHelloScene regenerates this file from scratch, so setup never edits it.</summary>
        public const string SourceScenePath = "Assets/Scenes/HelloWorld.unity";

        [MenuItem("Forge/Setup Passthrough Scene")]
        public static void Run()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
            {
                if (!AssetDatabase.CopyAsset(SourceScenePath, ScenePath))
                    throw new System.IO.FileNotFoundException("Could not copy source scene", SourceScenePath);
                Debug.Log($"[ForgeSetup] Created {ScenePath} from {SourceScenePath}");
            }

            var scene = EditorSceneManager.OpenScene(ScenePath);

            var steps = TypeCache.GetMethodsWithAttribute<ForgeSetupStepAttribute>()
                .Where(m => m.IsStatic && m.GetParameters().Length == 0)
                .OrderBy(m => m.GetCustomAttribute<ForgeSetupStepAttribute>().Order)
                .ToList();

            var failed = false;
            foreach (var step in steps)
            {
                var order = step.GetCustomAttribute<ForgeSetupStepAttribute>().Order;
                var name = $"{step.DeclaringType?.Name}.{step.Name} ({order})";
                try
                {
                    step.Invoke(null, null);
                    Debug.Log($"[ForgeSetup] OK {name}");
                }
                catch (Exception e)
                {
                    failed = true;
                    Debug.LogError($"[ForgeSetup] FAILED {name}: {e.InnerException ?? e}");
                }
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log($"[ForgeSetup] Ran {steps.Count} step(s), failed={failed}");

            if (Application.isBatchMode && failed)
                EditorApplication.Exit(1);
        }
    }

    /// <summary>Small helpers shared by setup steps.</summary>
    public static class ForgeSetupUtil
    {
        /// <summary>Finds a root GameObject by name in the open scene, or creates it.</summary>
        public static GameObject GetOrCreateRoot(string name)
        {
            foreach (var go in EditorSceneManager.GetActiveScene().GetRootGameObjects())
                if (go.name == name) return go;
            return new GameObject(name);
        }

        /// <summary>Finds a direct child by name, or creates it under <paramref name="parent"/>.</summary>
        public static GameObject GetOrCreateChild(Transform parent, string name)
        {
            var t = parent.Find(name);
            if (t != null) return t.gameObject;
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go;
        }

        public static T GetOrAdd<T>(GameObject go) where T : Component
        {
            return go.TryGetComponent<T>(out var c) ? c : go.AddComponent<T>();
        }

        /// <summary>ForgeRig/StagingMat, created if missing.</summary>
        public static GameObject GetOrCreateStagingMat()
        {
            var rig = GetOrCreateRoot(Forge.ForgeScene.RigRoot);
            return GetOrCreateChild(rig.transform, Forge.ForgeScene.StagingMat);
        }
    }
}
