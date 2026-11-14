using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

// Command-line helpers for the Quest hello world.
//   Unity -batchmode -projectPath ForgeXR -buildTarget Android -executeMethod ForgeBuild.AddHelloWorld -quit
//   Unity -batchmode -projectPath ForgeXR -buildTarget Android -executeMethod ForgeBuild.BuildQuest -quit
// Add -forgeSetup to BuildQuest to run Forge.EditorTools.ForgeQuestSetup.Run() (all [ForgeSetupStep]s) first:
//   Unity -batchmode -projectPath ForgeXR -buildTarget Android -executeMethod ForgeBuild.BuildQuest -forgeSetup -quit
// All are also available from the "Forge" menu in the editor.
public static class ForgeBuild
{
    const string ScenePath = "Assets/Scenes/SampleScene.unity";
    const string ApkPath = "Builds/ForgeXR.apk";

    [MenuItem("Forge/Add Hello World Sign")]
    public static void AddHelloWorld()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath);
        // Scan root objects instead of GameObject.Find, which misses the sign once setup has deactivated it.
        var exists = false;
        foreach (var root in scene.GetRootGameObjects())
            if (root.name == "HelloWorld") { exists = true; break; }
        if (!exists)
        {
            var go = new GameObject("HelloWorld");
            go.transform.position = new Vector3(0f, 1.8f, 2f); // ~2 m in front, slightly above eye level
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.text = "Hello World!";
            tmp.fontSize = 3f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.rectTransform.sizeDelta = new Vector2(3f, 1f);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[ForgeBuild] Added HelloWorld sign to " + ScenePath);
        }
        else
        {
            Debug.Log("[ForgeBuild] HelloWorld sign already present");
        }
    }

    [MenuItem("Forge/Setup + Build Quest APK")]
    public static void SetupAndBuildQuest()
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;
        Build(runSetup: true);
    }

    [MenuItem("Forge/Build Quest APK")]
    public static void BuildQuest()
    {
        Build(runSetup: System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-forgeSetup") >= 0);
    }

    static void Build(bool runSetup)
    {
        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

        if (runSetup)
        {
            Debug.Log("[ForgeBuild] Running Forge setup before build");
            Forge.EditorTools.ForgeQuestSetup.Run();
        }
        EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;

        // Quest requires 64-bit ARM, which requires IL2CPP.
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.forge.forgexr");
        PlayerSettings.productName = "ForgeXR";

        Directory.CreateDirectory(Path.GetDirectoryName(ApkPath));
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = ApkPath,
            target = BuildTarget.Android,
            options = BuildOptions.None,
        });

        var s = report.summary;
        Debug.Log($"[ForgeBuild] Result: {s.result}, size: {s.totalSize / (1024 * 1024)} MB, errors: {s.totalErrors}");
        if (Application.isBatchMode && s.result != BuildResult.Succeeded)
            EditorApplication.Exit(1);
    }
}
