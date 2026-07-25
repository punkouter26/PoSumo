using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PoSumo.EditorTools
{
    /// Builds the Android APK for on-device testing. Invoked via MCP
    /// script-execute; the MCP call times out while the build runs, so the
    /// result is reported through the "BUILD RESULT:" line in Logs/Editor.log.
    public static class BuildAndroid
    {
        const string OUTPUT_PATH = "Builds/Android/PoSumo.apk";
        const string APP_ID = "com.punkouter26.posumo";

        [MenuItem("PoSumo/Build Android APK")]
        public static void Build()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError("BUILD RESULT: Aborted — exit Play mode first.");
                return;
            }

            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, APP_ID);

            var scenes = new System.Collections.Generic.List<string>();
            foreach (var scene in EditorBuildSettings.scenes)
            {
                if (scene.enabled)
                {
                    scenes.Add(scene.path);
                }
            }

            var options = new BuildPlayerOptions
            {
                scenes = scenes.ToArray(),
                locationPathName = OUTPUT_PATH,
                target = BuildTarget.Android,
                options = BuildOptions.None,
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;
            Debug.Log($"BUILD RESULT: {summary.result} | errors={summary.totalErrors} | " +
                      $"size={summary.totalSize / (1024 * 1024)}MB | " +
                      $"time={summary.totalTime.TotalMinutes:F1}min | {OUTPUT_PATH}");
        }
    }
}
