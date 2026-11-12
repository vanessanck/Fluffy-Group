using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Editor-only helper (lives in Assets/Editor, so it is never part of the game itself).
// Keeps the project ready to open, play and build on BOTH Mac and Windows:
//   - creates Assets/Resources/HexMaterial.mat (URP Lit) if it is missing, so built games are never pink/magenta
//   - makes sure SampleScene is in the build list
//   - before every build, prints what will be built and warns about things that break on the other OS
// Menu: Fluffy War > Check Mac + Windows setup
[InitializeOnLoad]
public class FluffyProjectSetup : IPreprocessBuildWithReport
{
    const string MaterialPath = "Assets/Resources/HexMaterial.mat";
    const string ScenePath = "Assets/Scenes/SampleScene.unity";

    static FluffyProjectSetup()
    {
        // Editor Play mode only keeps running in the background when this Player Setting is on,
        // so make sure it is on every time you press Play
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.ExitingEditMode) EnsureRunInBackground();
        };

        // Wait until Unity has finished loading/importing before touching assets
        EditorApplication.delayCall += () =>
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            EnsureMaterial();
            EnsureSceneInBuild();
            EnsureRunInBackground();
        };
    }

    public int callbackOrder { get { return 0; } }

    public void OnPreprocessBuild(BuildReport report)
    {
        EnsureMaterial();
        EnsureSceneInBuild();
        EnsureRunInBackground();
        Report(report.summary.platform);
    }

    [MenuItem("Fluffy War/Check Mac + Windows setup")]
    static void CheckFromMenu()
    {
        EnsureMaterial();
        EnsureSceneInBuild();
        EnsureRunInBackground();
        Report(EditorUserBuildSettings.activeBuildTarget);
    }

    // Player Settings > Resolution and Presentation > Run In Background: the built game keeps playing
    // (AI vs AI, auto next match) when you Alt+Tab / Cmd+Tab to another window.
    static void EnsureRunInBackground()
    {
        if (PlayerSettings.runInBackground) return;
        PlayerSettings.runInBackground = true;
        Debug.Log("Fluffy War: turned on Player Settings > Run In Background (the game keeps running after Alt+Tab).");
    }

    // The tiles, warriors and buildings all use this material. Unity always packs files in a
    // Resources folder (and the shader they use) into the build, on Mac and on Windows.
    static void EnsureMaterial()
    {
        if (AssetDatabase.LoadAssetAtPath<Material>(MaterialPath) != null) return;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Simple Lit");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null)
        {
            Debug.LogWarning("Fluffy War: could not find the URP Lit shader to make " + MaterialPath + ". Make it by hand (see HOW-TO).");
            return;
        }

        if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
        Material m = new Material(shader);
        m.color = Color.white;   // the code sets each tile's own color
        AssetDatabase.CreateAsset(m, MaterialPath);
        AssetDatabase.SaveAssets();
        Debug.Log("Fluffy War: created " + MaterialPath + " (" + shader.name + "). Commit it so your teammates get it too.");
    }

    static void EnsureSceneInBuild()
    {
        if (!File.Exists(ScenePath)) return;
        EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
        if (scenes.Any(s => s.path == ScenePath && s.enabled)) return;

        var list = scenes.Where(s => s.path != ScenePath).ToList();
        list.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
        EditorBuildSettings.scenes = list.ToArray();
        Debug.Log("Fluffy War: added " + ScenePath + " to the build list.");
    }

    static void Report(BuildTarget target)
    {
        string os = SystemInfo.operatingSystemFamily.ToString();
        ScriptingImplementation backend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone);
        bool macOk = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneOSX);
        bool winOk = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);

        string msg = "Fluffy War setup check\n" +
                     "  This computer: " + os + "   Building for: " + target + "   Scripting backend: " + backend + "\n" +
                     "  HexMaterial: " + (AssetDatabase.LoadAssetAtPath<Material>(MaterialPath) != null ? "OK" : "MISSING") +
                     "   Run in background: " + (PlayerSettings.runInBackground ? "OK" : "OFF") +
                     "   Scene in build list: " + (EditorBuildSettings.scenes.Any(s => s.path == ScenePath && s.enabled) ? "OK" : "MISSING") + "\n" +
                     "  Mac build support installed: " + (macOk ? "yes" : "NO (Unity Hub > Installs > Add modules > Mac Build Support (Mono))") + "\n" +
                     "  Windows build support installed: " + (winOk ? "yes" : "NO (Unity Hub > Installs > Add modules > Windows Build Support (Mono))");
        Debug.Log(msg);

        // IL2CPP needs the matching OS (Xcode on a Mac, Visual Studio C++ on Windows). Mono builds both from either.
        if (backend == ScriptingImplementation.IL2CPP)
        {
            bool crossBuild = (target == BuildTarget.StandaloneOSX && os != "MacOSX") ||
                              ((target == BuildTarget.StandaloneWindows || target == BuildTarget.StandaloneWindows64) && os != "Windows");
            if (crossBuild)
                Debug.LogWarning("Fluffy War: IL2CPP can't build " + target + " from this computer. " +
                                 "Set Project Settings > Player > Other Settings > Scripting Backend to Mono, or build on that OS.");
        }
    }
}
