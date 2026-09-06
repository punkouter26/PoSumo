using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace PoSumo.EditorTools
{
    /// The Android player settings this game actually wants, applied from code so
    /// they cannot quietly drift.
    ///
    /// Most of what matters here was already right and is asserted rather than
    /// changed — IL2CPP, ARM64, Release, engine-code stripping, Swappy frame
    /// pacing, portrait-only, render-outside-safe-area. What was NOT right was
    /// invisible in the Inspector because it was ABSENT rather than wrong:
    ///
    ///  - `managedStrippingLevel` in ProjectSettings.asset carried a Standalone
    ///    entry and no Android entry at all, so the level an APK built with was
    ///    whatever the editor version's default happened to be. This project has
    ///    already lost one Android build to managed stripping (the McpPlugin
    ///    resolve failure that ExcludeMcpPluginsFromPlayer exists to prevent), and
    ///    an unpinned stripping level is the setting you least want implicit.
    ///  - there was no Android `textureCompressionFormats` entry, so texture
    ///    compression was whatever `EditorUserBuildSettings.androidBuildSubtarget`
    ///    was last left on. On a minSdk-26 ARM64-only build that should be ASTC;
    ///    it is universally supported at that API level and is the single biggest
    ///    lever on both APK size and texture bandwidth.
    ///
    /// Called from both Android builders, so a build always ships this set, and
    /// exposed as a menu item so it can be applied and inspected on its own.
    ///
    /// TWO THINGS ARE DELIBERATELY NOT DONE, and both look like obvious wins:
    ///
    ///  - **managed stripping is set to Low, not High or Medium.** The game's whole
    ///    point is running ONNX policies through com.unity.ai.inference at runtime,
    ///    and that path plus ML-Agents reaches types by reflection. Higher stripping
    ///    fails by REMOVING a type that is only ever named in a string, which does
    ///    not error at build time — it produces a fighter that stands still on the
    ///    phone and works perfectly in the Editor. That is the exact failure this
    ///    project has been bitten by before from the other direction, and it is not
    ///    worth a few MB without an on-device inference test to back it.
    ///  - **R8/minify is left alone.** Same class of risk on the Java side, and
    ///    with no third-party Android SDKs in this project there is very little
    ///    Java to shrink.
    public static class AndroidBuildSettings
    {
        /// ASTC everywhere. minSdk is 26 and the build is ARM64-only, so every
        /// device that can install this supports ASTC; ETC2 is the fallback for
        /// hardware this build already excludes.
        private const MobileTextureSubtarget TEXTURE_FORMAT = MobileTextureSubtarget.ASTC;

        /// Low, and the class comment above says why not higher.
        private const ManagedStrippingLevel STRIPPING = ManagedStrippingLevel.Low;

        [MenuItem("PoSumo/Apply Android Build Settings")]
        public static void ApplyMenu()
        {
            Apply();
            AssetDatabase.SaveAssets();
        }

        /// Applies the set and logs what it did. Safe to call repeatedly; every
        /// write here is idempotent.
        public static void Apply()
        {
            NamedBuildTarget android = NamedBuildTarget.Android;

            // ---- Scripting and stripping ------------------------------------
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetIl2CppCompilerConfiguration(android, Il2CppCompilerConfiguration.Release);
            PlayerSettings.SetManagedStrippingLevel(android, STRIPPING);
            PlayerSettings.stripEngineCode = true;

            // ARM64 only. ARMv7 devices cannot meet this game's physics budget
            // (13 hinge motors per fighter at a 50 Hz fixed step) and shipping the
            // second ABI would roughly double the APK for hardware that cannot run
            // it well. Play requires a 64-bit binary regardless.
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

            // ---- Textures ---------------------------------------------------
            EditorUserBuildSettings.androidBuildSubtarget = TEXTURE_FORMAT;

            // ---- Frame pacing and threading ---------------------------------
            // Swappy. Without it a game that renders faster than the display can
            // show reads as judder rather than as headroom, which on a 60 Hz phone
            // is exactly this game's situation.
            PlayerSettings.Android.optimizedFramePacing = true;
            PlayerSettings.SetMobileMTRendering(android, true);
            PlayerSettings.gpuSkinning = false;   // 2D sprites; nothing to skin.

            // ---- Orientation and display ------------------------------------
            // Portrait both ways, no landscape. The whole HUD is authored against a
            // 720x1280 portrait panel and Systems_HudRoot's band proportions assume
            // it; a landscape rotation does not degrade, it breaks.
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
            PlayerSettings.allowedAutorotateToPortrait = true;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = true;
            PlayerSettings.allowedAutorotateToLandscapeLeft = false;
            PlayerSettings.allowedAutorotateToLandscapeRight = false;
            // Draw into the cutout and the gesture area; Systems_SafeArea is what
            // keeps content out of them, and it needs the full window to do it.
            PlayerSettings.Android.renderOutsideSafeArea = true;

            // ---- Small wins with no downside --------------------------------
            // Nothing reads the accelerometer, and sampling it costs a wakeup.
            PlayerSettings.accelerometerFrequency = 0;
            // No analytics, no leaderboards, no remote config. Systems_Telemetry
            // opens a socket, but it only spawns in the Editor and in development
            // builds, so a release APK does not need the permission and should not
            // ask for it.
            PlayerSettings.Android.forceInternetPermission = false;
            PlayerSettings.Android.forceSDCardPermission = false;

            Debug.Log($"ANDROID SETTINGS RESULT: backend=IL2CPP/Release " +
                      $"abi={PlayerSettings.Android.targetArchitectures} " +
                      $"stripping={PlayerSettings.GetManagedStrippingLevel(android)} " +
                      $"engineStrip={PlayerSettings.stripEngineCode} " +
                      $"texture={EditorUserBuildSettings.androidBuildSubtarget} " +
                      $"swappy={PlayerSettings.Android.optimizedFramePacing} " +
                      $"mtRendering={PlayerSettings.GetMobileMTRendering(android)} " +
                      $"orientation={PlayerSettings.defaultInterfaceOrientation} " +
                      $"safeArea={PlayerSettings.Android.renderOutsideSafeArea} " +
                      $"version={PlayerSettings.bundleVersion}");
        }
    }
}
