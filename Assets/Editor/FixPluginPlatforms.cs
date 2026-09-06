using System.Text;
using UnityEditor;
using UnityEngine;

namespace PoSumo.EditorTools
{
    /// Repairs the MCP / NuGet tooling DLL import settings after an Android build.
    ///
    /// NOTE: the next four paragraphs are the ORIGINAL rationale and are kept as
    /// history. They describe what this tool used to do and why that reasoning was
    /// wrong; the REWRITTEN block below them is what it does now. Read both.
    ///
    /// WHY THIS ORIGINALLY EXISTED. `Assets/Plugins/NuGet/` holds ~42 DLLs that belong to the
    /// Unity-MCP editor tooling — Roslyn, SignalR, System.Text.Json, McpPlugin and
    /// friends. Every one of them ships with "Any Platform" import settings, and
    /// Unity includes an Any-Platform managed plugin in the player whether or not
    /// a single line of game code references it.
    ///
    /// On 2026-08-05 that broke the Android build outright:
    ///
    ///     ILLink: error IL1999: Failed to resolve base type
    ///     com.IvanMurzak.ReflectorNet.MethodWrapper for type
    ///     com.IvanMurzak.McpPlugin.RunPrompt in assembly McpPlugin.dll
    ///     when linking against the UnityAot-Linux profile
    ///     -> BUILD RESULT: Failed
    ///
    /// It is NOT a missing type or a version skew — both were checked, the type is
    /// present and both sides are v5.4.0.0. It is that these assemblies were never
    /// meant to survive IL2CPP's managed stripping, and they have no business being
    /// in a phone build in the first place.
    ///
    /// Nothing in the player wants them: the Android target does not define
    /// `UNITY_MCP_READY`, which every MCP runtime asmdef lists in its
    /// `defineConstraints`, so those assemblies are not even compiled for Android.
    /// `PoSumo.Runtime` references only Unity, ML-Agents and URP. Marking the folder
    /// editor-only therefore removes dead weight, not functionality — and takes a
    /// large amount of Roslyn and ASP.NET out of the APK on the way.
    ///
    /// ---------------------------------------------------------------------------
    /// REWRITTEN 2026-09-05. It no longer marks the folder editor-only, because
    /// doing that BREAKS every desktop build, and the comment above is wrong about
    /// why it was ever safe.
    ///
    /// The claim above — "the Android target does not define `UNITY_MCP_READY` ...
    /// so those assemblies are not even compiled" — does not hold for Standalone.
    /// `com.IvanMurzak.Unity.MCP.Runtime.asmdef` ships `includePlatforms: []` and
    /// lists `ReflectorNet.dll` / `McpPlugin.dll` in `precompiledReferences`, and it
    /// demonstrably DOES compile for StandaloneWindows64. Marking those DLLs
    /// editor-only therefore deletes the references out from under an assembly Unity
    /// is still compiling, and the training-env build dies with 351 ×
    /// `CS0234: The type or namespace name 'ReflectorNet' does not exist`.
    ///
    /// Keeping the MCP payload out of the APK is now owned entirely by
    /// `ExcludeMcpPluginsFromPlayer`, an IPreprocessBuildWithReport that runs INSIDE
    /// the build (after the last refresh, where the NuGet configurator cannot revert
    /// it) and is scoped to Android alone. That is the durable fix; this menu item
    /// could never be, because the configurator re-applies Any Platform on every
    /// restore and domain reload.
    ///
    /// So this is now a REPAIR tool rather than an exclusion tool: it puts the
    /// folder back into the state builds actually need — Any Platform on, Android
    /// excluded, editor on — which is what the preprocessor leaves un-restored after
    /// an Android build. Run it if a desktop build fails on ReflectorNet.
    /// ---------------------------------------------------------------------------
    internal static class FixPluginPlatforms
    {
        private const string PLUGIN_FOLDER = "Assets/Plugins/NuGet";

        [MenuItem("PoSumo/Fix Plugin Platforms (repair after an Android build)")]
        internal static void Run()
        {
            var report = new StringBuilder();
            int scanned = 0, changed = 0;

            string[] guids = AssetDatabase.FindAssets(string.Empty, new[] { PLUGIN_FOLDER });
            for (int guidIndex = 0; guidIndex < guids.Length; guidIndex++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[guidIndex]);
                if (!path.EndsWith(".dll", System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                scanned++;

                var importer = AssetImporter.GetAtPath(path) as PluginImporter;
                if (importer == null)
                {
                    continue;
                }

                // Already correct — leave it alone so a re-run is a no-op and does
                // not churn every .meta in the folder for nothing.
                if (importer.GetCompatibleWithAnyPlatform()
                    && importer.GetExcludeFromAnyPlatform(BuildTarget.Android)
                    && importer.GetCompatibleWithEditor())
                {
                    continue;
                }

                // Any Platform ON is what the MCP Runtime assembly needs: it compiles
                // for the player and takes these as precompiledReferences, so a
                // desktop build fails outright without them.
                importer.SetCompatibleWithAnyPlatform(true);
                importer.SetCompatibleWithEditor(true);

                // Android carved out of Any Platform. Note this is
                // SetExcludeFromAnyPlatform and NOT SetCompatibleWithPlatform: while
                // Any Platform is true, per-platform compatibility flags are ignored
                // and the exclusion list is the only thing Unity honours. The old code
                // called SetCompatibleWithPlatform(Android, false) alongside
                // Any = false, where it was merely redundant; carried over unchanged
                // onto Any = true it would silently do nothing at all.
                importer.SetExcludeFromAnyPlatform(BuildTarget.Android, true);
                EditorUtility.SetDirty(importer);
                importer.SaveAndReimport();
                changed++;
                report.Append("  ").AppendLine(path.Substring(PLUGIN_FOLDER.Length + 1));
            }

            Debug.Log($"PLUGIN PLATFORM RESULT: scanned={scanned} repaired to Any-Platform/no-Android={changed}\n{report}");
        }
    }
}
