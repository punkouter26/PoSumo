using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PoSumo.EditorTools
{
    /// Builds the Android APK for on-device testing. Invoked via MCP
    /// script-execute; the MCP call times out while the build runs, so the
    /// result is reported through the "BUILD RESULT:" line in Logs/Editor.log.
    public static class BuildAndroid
    {
        private const string OUTPUT_PATH = "Builds/Android/PoSumo.apk";
        private const string APP_ID = "com.punkoutersoftware.posumo";

        [MenuItem("PoSumo/Build Android APK")]
        public static void Build()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError("BUILD RESULT: Aborted — exit Play mode first.");
                return;
            }

            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, APP_ID);

            // Signing. ProjectSettings serializes androidUseCustomKeystore: 1 and the
            // keystore path, but Unity deliberately does NOT serialize the passwords,
            // so a build that does not supply them dies with "Unable to sign the
            // Android application". Same upload key as the Play bundle, so an APK
            // installed over a Play build (or vice versa) does not hit a signature
            // mismatch.
            string password = BuildAndroidAAB.ResolveKeystorePassword();
            if (string.IsNullOrEmpty(password))
            {
                Debug.LogError(
                    "BUILD RESULT: Aborted — no keystore password. Set POSUMO_KEYSTORE_PASS " +
                    "or put the password on the first line of keystore.pass next to " +
                    BuildAndroidAAB.KEYSTORE_PATH);
                return;
            }

            // The same guard the AAB builder has. A present keystore.pass with a
            // missing .jks — a half-restored release folder — satisfied the check
            // above, so the build ran for minutes and then died inside Gradle on a
            // signing error, leaving keystoreName pointing at a file that is not
            // there. Aborting costs a second and prints the line the tooling greps.
            if (!System.IO.File.Exists(BuildAndroidAAB.KEYSTORE_PATH))
            {
                Debug.LogError("BUILD RESULT: Aborted — keystore not found at " +
                               BuildAndroidAAB.KEYSTORE_PATH);
                return;
            }

            PlayerSettings.Android.useCustomKeystore = true;
            PlayerSettings.Android.keystoreName = BuildAndroidAAB.KEYSTORE_PATH;
            PlayerSettings.Android.keystorePass = password;
            PlayerSettings.Android.keyaliasName = BuildAndroidAAB.KEYALIAS;
            PlayerSettings.Android.keyaliasPass = password;

            // The AAB builder leaves buildAppBundle = true persisted in
            // EditorUserBuildSettings. Without this the "APK" build silently emits an
            // app bundle to PoSumo.apk, which adb cannot install.
            EditorUserBuildSettings.buildAppBundle = false;

            var scenes = BuildAndroidAAB.EnabledScenes();

            var options = new BuildPlayerOptions
            {
                scenes = scenes.ToArray(),
                locationPathName = OUTPUT_PATH,
                target = BuildTarget.Android,
                options = BuildOptions.None,
            };

            // Keep the MCP runtime out of the player. The NuGet DLLs it links against
            // are excluded from Android, so compiling it in fails with a few hundred
            // missing-type errors. Same treatment the AAB build gives the Play bundle,
            // which also keeps this test APK representative of what ships.
            string definesBefore = BuildAndroidAAB.StripEditorOnlyDefines("BUILD");

            BuildReport report;
            try
            {
                report = BuildPipeline.BuildPlayer(options);
            }
            finally
            {
                BuildAndroidAAB.RestoreDefines(definesBefore);
                // Do not leave the password sitting in the in-memory PlayerSettings.
                PlayerSettings.Android.keystorePass = string.Empty;
                PlayerSettings.Android.keyaliasPass = string.Empty;
            }

            BuildSummary summary = report.summary;
            Debug.Log($"BUILD RESULT: {summary.result} | errors={summary.totalErrors} | " +
                      $"size={summary.totalSize / (1024 * 1024)}MB | " +
                      $"time={summary.totalTime.TotalMinutes:F1}min | {OUTPUT_PATH}");
        }
    }
}
