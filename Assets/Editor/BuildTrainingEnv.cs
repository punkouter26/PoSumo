using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PoSumo.EditorTools
{
    /// Builds a headless Windows player containing exactly one training scene,
    /// for use as an `--env` target for mlagents-learn. Invoked via MCP
    /// script-execute; the MCP call times out while the build runs, so the
    /// result is reported through the "BUILD RESULT:" line in Logs/Editor.log.
    ///
    /// The build target platform must already be Windows Standalone — this is
    /// the editor's default here, and switching platforms mid-session costs a
    /// full reimport.
    public static class BuildTrainingEnv
    {
        // One entry per surviving training scene, named <Name>Env to match the
        // scene it builds. Env builds are disposable — they are gitignored and
        // rebuilt from here, so retired ones are deleted rather than kept.
        [MenuItem("PoSumo/Build Nick Training Env")]
        public static void BuildNick()
        {
            Build("Assets/Scenes/Training/SCN_TRAIN_NICK.unity", "Builds/NickEnv/NickEnv.exe");
        }

        [MenuItem("PoSumo/Build Kim Training Env")]
        public static void BuildKim()
        {
            Build("Assets/Scenes/Training/SCN_TRAIN_KIM.unity", "Builds/KimEnv/KimEnv.exe");
        }

        [MenuItem("PoSumo/Build Matt Training Env")]
        public static void BuildMatt()
        {
            Build("Assets/Scenes/Training/SCN_TRAIN_MATT.unity", "Builds/MattEnv/MattEnv.exe");
        }

        [MenuItem("PoSumo/Build Standard Training Env")]
        public static void BuildStandard()
        {
            Build("Assets/Scenes/Training/SCN_TRAIN_STANDARD.unity", "Builds/StandardEnv/StandardEnv.exe");
        }

        // The walk-school and recover-school env entries are gone. Walking is
        // trained inside each fighter's unified scene now — a lane of walk-mode
        // agents sharing the fighter's behavior name — so there is one env per
        // fighter covering both tasks, not one per task per fighter.

        public static void Build(string scenePath, string outputPath)
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError("BUILD RESULT: Aborted — exit Play mode first.");
                return;
            }

            var options = new BuildPlayerOptions
            {
                scenes = new[] { scenePath },
                locationPathName = outputPath,
                target = BuildTarget.StandaloneWindows64,
                // Standard player, not the Server subtarget — the Dedicated
                // Server module is not installed. mlagents-learn runs these
                // with --no-graphics, which is what makes them headless.
                subtarget = (int)StandaloneBuildSubtarget.Player,
                // Development, NOT None. `Systems_Telemetry.Spawn` bails unless
                // `Debug.isDebugBuild || Application.isEditor`, so under
                // BuildOptions.None the endpoint never started in a training env —
                // which is the ONLY place it is useful. Its port walk from 8787
                // exists specifically so 4-8 concurrent envs each get a socket, and
                // that code could never once have run. `Systems_Log.Info` is gated
                // on DEVELOPMENT_BUILD for the same reason: a headless env should
                // still be able to say what it is doing.
                //
                // The cost is a slightly larger player and the profiler being
                // available. Neither matters for a process that mlagents-learn runs
                // with --no-graphics and throws away.
                options = BuildOptions.Development,
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;
            Debug.Log($"BUILD RESULT: {summary.result} | errors={summary.totalErrors} | " +
                      $"size={summary.totalSize / (1024 * 1024)}MB | " +
                      $"time={summary.totalTime.TotalMinutes:F1}min | {outputPath}");
        }
    }
}
