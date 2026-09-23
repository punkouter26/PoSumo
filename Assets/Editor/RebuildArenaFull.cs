using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using PoSumo;

namespace PoSumo.EditorTools
{
    /// Full arena re-bake: clears the arena's children and re-runs
    /// `Systems_SumoArena.Build()` from scratch, then saves the scene.
    ///
    /// WHY THIS EXISTS. The arena scenes are BAKED — `Awake` finds children and
    /// only rebinds, so the serialized objects are what render. `Build()` cannot
    /// simply be re-run to pick up code changes: it does NOT clear what is
    /// already there and would DUPLICATE the whole arena. Until this tool the
    /// only safe path was hand-deleting children in the Editor.
    ///
    /// Scope note: this rebuilds the STRUCTURE only (platform, tawara, tiers,
    /// dressing) from the arena's serialized tuning fields. Tune those fields on
    /// the `Systems_SumoArena` component in the Inspector FIRST — they are copied
    /// onto the component when the scene loads — then re-bake. For a colour-only
    /// change to the crowd tiers, prefer PoSumo -> Rebuild Arena Foreground
    /// Tiers, which replaces only that subtree and is much cheaper.
    internal static class RebuildArenaFull
    {
        private static readonly string[] ArenaScenes =
        {
            "Assets/Scenes/SCN_SUMO.unity",
            "Assets/Scenes/SCN_BOT.unity",
        };

        [MenuItem("PoSumo/Rebuild Arena (FULL)")]
        private static void Rebuild()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError("ARENA REBUILD RESULT: Failed — exit Play mode first; " +
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
                    // Clear ONLY the arena's own children — the baked subtree it
                    // built last time — then Build() re-creates it from the
                    // component's current serialized tuning. Destroying top-level
                    // scene objects here would take the fighters and managers
                    // with it, so the loop is deliberately over transform children.
                    Transform root = arena.transform;
                    for (int index = root.childCount - 1; index >= 0; index--)
                    {
                        Object.DestroyImmediate(root.GetChild(index).gameObject);
                    }
                    arena.Build();
                    rebuiltHere++;
                }

                if (rebuiltHere > 0)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                    scenesTouched++;
                    arenasRebuilt += rebuiltHere;
                }

                Debug.Log($"[ARENA] {scenePath}: {rebuiltHere} arena(s) rebuilt from scratch.");
            }

            Debug.Log("ARENA REBUILD RESULT: Succeeded | " +
                      $"{arenasRebuilt} arena(s) across {scenesTouched} scene(s).");
        }
    }
}
