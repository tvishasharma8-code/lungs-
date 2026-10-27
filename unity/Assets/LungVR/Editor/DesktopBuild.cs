using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// BioGears > Build Desktop App (Windows): builds the scene as a normal Windows program
/// in Builds/Desktop/LungVisionVR.exe. With no headset it runs in DesktopMode (mouse and
/// keyboard, all panels in view, real BioGears on this PC). The LungVision website's
/// "View in Unity" button starts it through the backend (backend/services/unity_service.py).
///
/// The Quest (Android) build target is restored afterwards.
/// </summary>
public static class DesktopBuild
{
    public const string ExeRelativePath = "Builds/Desktop/LungVisionVR.exe";

    [MenuItem("BioGears/Build Desktop App (Windows)")]
    public static void Build()
    {
        BuildTarget previousTarget = EditorUserBuildSettings.activeBuildTarget;
        BuildTargetGroup previousGroup = BuildPipeline.GetBuildTargetGroup(previousTarget);

        string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        if (scenes.Length == 0)
        {
            EditorUtility.DisplayDialog("Build Desktop App", "No scenes are enabled in Build Settings.", "OK");
            return;
        }

        string exe = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ExeRelativePath));

        // Only the window settings change, and only for Windows; the name is put back after.
        string productName = PlayerSettings.productName;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 1600;
        PlayerSettings.defaultScreenHeight = 900;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.runInBackground = true;  // keep breathing while the browser has focus

        BuildReport report;
        try
        {
            PlayerSettings.productName = "LungVision VR";
            report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = exe,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            });
        }
        finally
        {
            PlayerSettings.productName = productName;
            if (EditorUserBuildSettings.activeBuildTarget != previousTarget)
                EditorUserBuildSettings.SwitchActiveBuildTarget(previousGroup, previousTarget);
        }

        if (report.summary.result != BuildResult.Succeeded)
        {
            Debug.LogError($"[DesktopBuild] Build failed: {report.summary.result} ({report.summary.totalErrors} errors) - see the Console.");
            return;
        }

        EnsureBioGearsPlugins(exe);
        Debug.Log($"[DesktopBuild] Built {exe} ({report.summary.totalSize / (1024 * 1024)} MB). " +
                  "The website's \"View in Unity\" button can now open it.");
        EditorUtility.RevealInFinder(exe);
    }

    // BioGears is five native DLLs; if the importer left any out of the player, copy them
    // next to biogears_unity.dll where BioGearsNativeRespiratory looks for them.
    static void EnsureBioGearsPlugins(string exe)
    {
        string source = Path.Combine(Application.dataPath, "Plugins", "x86_64");
        string target = Path.Combine(Path.GetDirectoryName(exe), Path.GetFileNameWithoutExtension(exe) + "_Data", "Plugins", "x86_64");
        Directory.CreateDirectory(target);
        foreach (string dll in Directory.GetFiles(source, "*.dll"))
        {
            string dest = Path.Combine(target, Path.GetFileName(dll));
            if (!File.Exists(dest))
            {
                File.Copy(dll, dest);
                Debug.Log($"[DesktopBuild] Copied missing plugin {Path.GetFileName(dll)} into the build.");
            }
        }
    }
}
