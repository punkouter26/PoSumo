using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PoSumo.EditorTools
{
    /// Builds the signed release Android App Bundle (.aab) for Google Play.
    ///
    /// The upload keystore and its password live OUTSIDE the repo so neither can
    /// be committed:
    ///     C:/Users/punko/Downloads/PoSumo-Release/posumo-upload.jks
    ///     C:/Users/punko/Downloads/PoSumo-Release/keystore.pass   (password, one line)
    ///
    /// Unity deliberately does not serialize keystore passwords into
    /// ProjectSettings.asset, so they must be supplied at build time — that is what
    /// the .pass file (or the POSUMO_KEYSTORE_PASS environment variable, which wins
    /// if set) is for.
    ///
    /// Invoked from the PoSumo menu or via MCP script-execute. The MCP call times
    /// out while the build runs; the outcome is reported through the
    /// "AAB BUILD RESULT:" line in Logs/Editor.log.
    public static class BuildAndroidAAB
    {
        private const string OUTPUT_PATH = "Builds/Android/PoSumo.aab";
        private const string APP_ID = "com.punkoutersoftware.posumo";
        // internal, not private: BuildAndroid signs its test APK with the same
        // upload key. One copy of these facts, so the two builders cannot diverge.
        internal const string KEYSTORE_PATH = "C:/Users/punko/Downloads/PoSumo-Release/posumo-upload.jks";
        internal const string KEYALIAS = "posumo-upload";

        // Google Play requires new apps and updates to target API 36 from 2026-08-31.
        private const int TARGET_SDK = 36;
        private const int MIN_SDK = 26;

        /// Scripting defines that gate editor tooling whose assemblies would
        /// otherwise be compiled into the shipped player. See the comment at the
        /// strip site in Build().
        private static readonly HashSet<string> EDITOR_ONLY_DEFINES = new HashSet<string>
        {
            "UNITY_MCP_READY",
        };

        [MenuItem("PoSumo/Build Android AAB (Play release)")]
        public static void Build()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError("AAB BUILD RESULT: Aborted — exit Play mode first.");
                return;
            }

            string password = ResolveKeystorePassword();
            if (string.IsNullOrEmpty(password))
            {
                Debug.LogError(
                    "AAB BUILD RESULT: Aborted — no keystore password. Set POSUMO_KEYSTORE_PASS " +
                    "or put the password on the first line of " +
                    Path.ChangeExtension(KEYSTORE_PATH, null) + ".pass");
                return;
            }

            if (!File.Exists(KEYSTORE_PATH))
            {
                Debug.LogError("AAB BUILD RESULT: Aborted — keystore not found at " + KEYSTORE_PATH);
                return;
            }

            // --- Identity ------------------------------------------------------
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, APP_ID);
            PlayerSettings.companyName = "Punkouter Software";
            PlayerSettings.productName = "PoSumo";

            // --- Signing -------------------------------------------------------
            PlayerSettings.Android.useCustomKeystore = true;
            PlayerSettings.Android.keystoreName = KEYSTORE_PATH;
            PlayerSettings.Android.keystorePass = password;
            PlayerSettings.Android.keyaliasName = KEYALIAS;
            PlayerSettings.Android.keyaliasPass = password;

            // --- Play requirements ---------------------------------------------
            PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)MIN_SDK;
            PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)TARGET_SDK;
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetIl2CppCompilerConfiguration(NamedBuildTarget.Android, Il2CppCompilerConfiguration.Release);

            // .aab, not .apk. This is the switch Play cares about most.
            EditorUserBuildSettings.buildAppBundle = true;
            EditorUserBuildSettings.androidBuildType = AndroidBuildType.Release;
            EditorUserBuildSettings.development = false;

            List<string> scenes = EnabledScenes();

            if (scenes.Count == 0)
            {
                Debug.LogError("AAB BUILD RESULT: Aborted — no enabled scenes in build settings.");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(OUTPUT_PATH));

            // Strip editor-tooling defines for the duration of the build.
            //
            // com.ivanmurzak.unity.mcp's Runtime asmdef has includePlatforms [] and
            // defineConstraints ["UNITY_MCP_READY"], so while that symbol is defined
            // the MCP runtime — plus McpPlugin, ReflectorNet, a SignalR client and
            // System.Net.Http — is compiled into the PLAYER, which is what pulled
            // android.permission.INTERNET into the manifest. Verified by inspecting
            // global-metadata.dat of the first 1.0.0 bundle.
            //
            // The symbol cannot simply be deleted: the package's DependencyResolver
            // re-adds it on every domain reload. So it is removed here and restored
            // in the finally block — BuildPlayer compiles player assemblies inline
            // with whatever PlayerSettings says right now, so the player build sees
            // the stripped set while the editor gets its symbol straight back.
            string definesBefore = StripEditorOnlyDefines("AAB BUILD");

            var options = new BuildPlayerOptions
            {
                scenes = scenes.ToArray(),
                locationPathName = OUTPUT_PATH,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
            };

            Debug.Log($"AAB BUILD START: {APP_ID} v{PlayerSettings.bundleVersion} " +
                      $"(code {PlayerSettings.Android.bundleVersionCode}) " +
                      $"target={TARGET_SDK} min={MIN_SDK} scenes={scenes.Count}");

            BuildReport report;
            try
            {
                report = BuildPipeline.BuildPlayer(options);
            }
            finally
            {
                // Restore the editor's defines even if the build threw, or the MCP
                // tooling stays dark until the next domain reload.
                RestoreDefines(definesBefore);
                // Do not leave the password sitting in the in-memory PlayerSettings.
                PlayerSettings.Android.keystorePass = string.Empty;
                PlayerSettings.Android.keyaliasPass = string.Empty;
            }

            BuildSummary summary = report.summary;

            Debug.Log($"AAB BUILD RESULT: {summary.result} | errors={summary.totalErrors} | " +
                      $"size={summary.totalSize / (1024 * 1024)}MB | " +
                      $"time={summary.totalTime.TotalMinutes:F1}min | {summary.outputPath}");
        }

        /// Removes the editor-only defines for the duration of a player build and
        /// returns the ORIGINAL define string, which the caller must hand back to
        /// RestoreDefines in a finally block. See the comment at the call site in
        /// Build() for why the player must not be compiled with these defined.
        ///
        /// Both Android builders need this: the NuGet DLLs the MCP runtime links
        /// against are excluded from Android, so compiling that runtime into the
        /// player fails with a few hundred missing-type errors.
        internal static string StripEditorOnlyDefines(string logPrefix)
        {
            string definesBefore = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Android);
            string definesForBuild = string.Join(";", definesBefore
                .Split(';')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0 && !EDITOR_ONLY_DEFINES.Contains(s)));

            if (definesForBuild != definesBefore)
            {
                PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.Android, definesForBuild);
                Debug.Log($"{logPrefix}: stripped editor-only defines for this build. " +
                          $"was [{definesBefore}] now [{definesForBuild}]");
            }

            return definesBefore;
        }

        /// Puts back whatever StripEditorOnlyDefines returned.
        internal static void RestoreDefines(string definesBefore)
        {
            if (PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Android) != definesBefore)
            {
                PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.Android, definesBefore);
            }
        }

        /// Scene paths ticked in Build Settings, in order. Shared with the APK
        /// builder so both Android artifacts always contain the same scene list —
        /// this was the one piece of build setup the two tools each did themselves.
        internal static List<string> EnabledScenes()
        {
            var scenes = new List<string>();
            foreach (var scene in EditorBuildSettings.scenes)
            {
                if (scene.enabled)
                {
                    scenes.Add(scene.path);
                }
            }
            return scenes;
        }

        /// Environment variable wins; otherwise read <keystore>.pass next to the keystore.
        internal static string ResolveKeystorePassword()
        {
            string fromEnv = Environment.GetEnvironmentVariable("POSUMO_KEYSTORE_PASS");
            if (!string.IsNullOrEmpty(fromEnv))
            {
                return fromEnv.Trim();
            }

            string passFile = Path.ChangeExtension(KEYSTORE_PATH, null) + ".pass";
            if (File.Exists(passFile))
            {
                return File.ReadAllText(passFile).Trim();
            }

            // Also accept a plain "keystore.pass" in the same folder.
            string sibling = Path.Combine(Path.GetDirectoryName(KEYSTORE_PATH) ?? ".", "keystore.pass");
            return File.Exists(sibling) ? File.ReadAllText(sibling).Trim() : null;
        }
    }
}
