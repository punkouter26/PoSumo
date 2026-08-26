using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PoSumo.EditorTools
{
    /// Keeps the MCP tooling's NuGet DLLs (Assets/Plugins/NuGet) out of every
    /// PLAYER build, at build time, where nothing can undo it.
    ///
    /// Why a build preprocessor and not a one-off menu item: the MCP package's own
    /// `NuGetPluginConfigurator.ConfigureAll` re-applies `includeInBuild: true`
    /// (Any Platform ON) to `McpPlugin.dll` and friends on every restore/domain
    /// reload — its NuGetConfig hardcodes them as runtime dependencies. So the
    /// *PoSumo → Fix Plugin Platforms* pass is reverted before the next build, and
    /// on 2026-08-26 the Android build died in managed stripping with
    /// `Failed to resolve base type com.IvanMurzak.ReflectorNet.MethodWrapper for
    /// type com.IvanMurzak.McpPlugin.RunPrompt in assembly McpPlugin.dll`. That
    /// exact failure is the one this project's CLAUDE.md warned would return after
    /// an MCP core upgrade, and it did, twice in one afternoon.
    ///
    /// Nothing in the player wants these assemblies: the game's runtime code never
    /// references the MCP namespaces, and the Roslyn/SignalR/AspNetCore payload
    /// they drag in is what pushed the APK to 96 MB. Running inside the build
    /// pipeline means the flags are set after the last refresh and before asset
    /// collection, so the configurator cannot get between the two.
    ///
    /// The flags are NOT restored afterwards: the Editor-only state is the one
    /// *Fix Plugin Platforms* wants anyway, and the configurator will flip them
    /// back on its next pass regardless.
    internal sealed class ExcludeMcpPluginsFromPlayer : IPreprocessBuildWithReport
    {
        private const string PLUGIN_FOLDER = "Assets/Plugins/NuGet";

        public int callbackOrder => -100;

        public void OnPreprocessBuild(BuildReport report)
        {
            string[] guids = AssetDatabase.FindAssets(string.Empty, new[] { PLUGIN_FOLDER });
            int changed = 0;
            for (int guidIndex = 0; guidIndex < guids.Length; guidIndex++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[guidIndex]);
                if (!path.EndsWith(".dll", System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var importer = AssetImporter.GetAtPath(path) as PluginImporter;
                if (importer == null)
                {
                    continue;
                }
                bool alreadyOut = !importer.GetCompatibleWithAnyPlatform()
                    && !importer.GetCompatibleWithPlatform(report.summary.platform);
                if (alreadyOut)
                {
                    continue;
                }
                importer.SetCompatibleWithAnyPlatform(false);
                importer.SetCompatibleWithEditor(true);
                importer.SetCompatibleWithPlatform(report.summary.platform, false);
                importer.SaveAndReimport();
                changed++;
            }
            Debug.Log($"PLUGIN PLATFORM RESULT (preprocess {report.summary.platform}): " +
                      $"excluded {changed} NuGet DLL(s) from the player");
        }
    }
}
