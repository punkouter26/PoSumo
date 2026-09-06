using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using PoSumo;

namespace PoSumo.EditorTools
{
    /// Re-bakes the arena's foreground crowd tiers in every arena scene.
    ///
    /// WHY A TOOL AND NOT JUST A CONSTANT. The arena is BAKED: an editor pass ran
    /// `Systems_SumoArena.Build()` once and the children were saved into the scene,
    /// so `Awake` only rebinds references. Editing a tier colour in code therefore
    /// changes nothing on screen — the serialized objects are what render. And
    /// re-running `Build()` is not the fix either: it does not clear what is
    /// already there, so it DUPLICATES the whole arena.
    ///
    /// `RebuildForegroundTiers` removes the one subtree it owns and rebuilds it,
    /// which is why this is safe to run repeatedly.
    internal static class RebuildArenaTiers
    {
        private static readonly string[] ArenaScenes =
        {
            "Assets/Scenes/SCN_SUMO.unity",
            "Assets/Scenes/SCN_BOT.unity",
        };

        [MenuItem("PoSumo/Rebuild Arena Foreground Tiers")]
        private static void Rebuild()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError("TIER REBUILD RESULT: Failed — exit Play mode first; " +
                               "scene edits do not survive it.");
                return;
            }

            int scenesTouched = 0;
            int arenasRebuilt = 0;

            foreach (string scenePath in ArenaScenes)
            {
                if (System.IO.File.Exists(scenePath) == false)
                {
                    continue;
                }

                Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                Systems_SumoArena[] arenas =
                    Object.FindObjectsByType<Systems_SumoArena>(FindObjectsSortMode.None);

                int rebuiltHere = 0;
                foreach (Systems_SumoArena arena in arenas)
                {
                    arena.RebuildForegroundTiers();
                    rebuiltHere++;
                }

                if (rebuiltHere > 0)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                    scenesTouched++;
                    arenasRebuilt += rebuiltHere;
                }

                Debug.Log($"[TIERS] {scenePath}: {rebuiltHere} arena(s) rebuilt.");
            }

            Debug.Log($"TIER REBUILD RESULT: Succeeded | {arenasRebuilt} arena(s) " +
                      $"across {scenesTouched} scene(s).");
        }
    }
}
