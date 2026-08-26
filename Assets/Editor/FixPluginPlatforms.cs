using System.Text;
using UnityEditor;
using UnityEngine;

namespace PoSumo.EditorTools
{
    /// Keeps the MCP / NuGet tooling DLLs out of player builds.
    ///
    /// WHY THIS EXISTS. `Assets/Plugins/NuGet/` holds ~42 DLLs that belong to the
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
    /// Re-run this after any NuGet restore or MCP package upgrade: the importer
    /// settings live in each DLL's `.meta`, and a re-resolve rewrites them back to
    /// Any Platform.
    internal static class FixPluginPlatforms
    {
        private const string PLUGIN_FOLDER = "Assets/Plugins/NuGet";

        [MenuItem("PoSumo/Fix Plugin Platforms (editor-only tooling)")]
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
                if (!importer.GetCompatibleWithAnyPlatform() && importer.GetCompatibleWithEditor())
                {
                    continue;
                }

                importer.SetCompatibleWithAnyPlatform(false);
                importer.SetCompatibleWithEditor(true);
                // Explicit, not implied by Any=false: the Android player build is
                // the one consumer that must never see these (2026-08-26, the
                // McpPlugin.dll linker failure came back after the 0.90.0 upgrade).
                importer.SetCompatibleWithPlatform(BuildTarget.Android, false);
                EditorUtility.SetDirty(importer);
                importer.SaveAndReimport();
                changed++;
                report.Append("  ").AppendLine(path.Substring(PLUGIN_FOLDER.Length + 1));
            }

            Debug.Log($"PLUGIN PLATFORM RESULT: scanned={scanned} switched to editor-only={changed}\n{report}");
        }
    }
}
