using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;
using UnityEngine.XR.Interaction.Toolkit.UI;

// Command-line helpers for the Quest hello world.
//   Unity -batchmode -projectPath ForgeXR -buildTarget Android -executeMethod ForgeBuild.CreateHelloScene -quit
//   Unity -batchmode -projectPath ForgeXR -buildTarget Android -executeMethod ForgeBuild.BuildQuest -quit
// Add -forgeSetup to BuildQuest to run Forge.EditorTools.ForgeQuestSetup.Run() (all [ForgeSetupStep]s) first,
// which also makes ForgeQuest the only build scene:
//   Unity -batchmode -projectPath ForgeXR -buildTarget Android -executeMethod ForgeBuild.BuildQuest -forgeSetup -quit
// All are also available from the "Forge" menu in the editor.
public static class ForgeBuild
{
    const string ScenePath = "Assets/Scenes/HelloWorld.unity";
    const string RigPrefabPath = "Assets/VRTemplateAssets/Prefabs/Setup/Complete XR Origin Set Up Hands Variant.prefab";
    const string ApkPath = "Builds/ForgeXR-Capture.apk";

    // Builds a minimal scene: XR rig at the origin, teleportable floor, light,
    // and a "Hello World!" sign 1.5 m in front of the starting position.
    // Overwrites Assets/Scenes/HelloWorld.unity and makes it the only build scene.
    [MenuItem("Forge/Create Clean Hello World Scene")]
    public static void CreateHelloScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var light = new GameObject("Directional Light").AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.2f;
        light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Floor";
        floor.transform.localScale = new Vector3(2f, 1f, 2f); // 20 m x 20 m
        floor.AddComponent<TeleportationArea>();

        var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RigPrefabPath);
        if (rigPrefab == null)
            throw new FileNotFoundException("XR rig prefab not found", RigPrefabPath);
        var rig = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab, scene);
        rig.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

        if (Object.FindAnyObjectByType<XRInteractionManager>() == null)
            new GameObject("XR Interaction Manager").AddComponent<XRInteractionManager>();
        if (Object.FindAnyObjectByType<EventSystem>() == null)
        {
            var es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
            es.AddComponent<XRUIInputModule>();
        }

        var sign = new GameObject("HelloWorld");
        sign.transform.position = new Vector3(0f, 1.5f, 1.5f); // eye height, 1.5 m ahead
        var tmp = sign.AddComponent<TextMeshPro>();
        tmp.text = "Hello World!";
        tmp.fontSize = 2.5f;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = Color.white;
        tmp.rectTransform.sizeDelta = new Vector2(3f, 1f);

        Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        AssetDatabase.SaveAssets();
        Debug.Log("[ForgeBuild] Created " + ScenePath + " and set it as the only build scene");
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
            // Runs before the scene list below is read, so the build picks up ForgeQuest.
            Debug.Log("[ForgeBuild] Running Forge setup before build");
            Forge.EditorTools.ForgeQuestSetup.Run();
        }
        EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;

        // Quest requires 64-bit ARM, which requires IL2CPP.
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.forge.forgexr.capture");
        PlayerSettings.productName = "ForgeXR Capture";
        PlayerSettings.Android.bundleVersionCode = 4;
        PlayerSettings.bundleVersion = "1.3";

        Directory.CreateDirectory(Path.GetDirectoryName(ApkPath));
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            // Whatever is enabled in Build Settings: HelloWorld after CreateHelloScene,
            // ForgeQuest after Forge.EditorTools.ForgeQuestSetup.Run.
            scenes = System.Array.ConvertAll(
                System.Array.FindAll(EditorBuildSettings.scenes, s => s.enabled), s => s.path),
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
