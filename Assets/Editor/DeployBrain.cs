using System.IO;
using UnityEditor;
using UnityEngine;

namespace PoSumo.EditorTools
{
    /// Copies a finished training run's ONNX into the character's agent folder
    /// and points that character's `inferenceModel` at it.
    ///
    /// The ONNX is always overwritten IN PLACE so the .meta (and therefore the
    /// asset GUID) survives — any scene or asset already referencing the model
    /// keeps working. Creating a fresh file instead would mint a new GUID and
    /// silently break those references.
    public static class DeployBrain
    {
        // Run ids below are the runs that currently back each deployed brain —
        // re-deploying from them reproduces exactly what ships in Assets/Agents.
        //
        // These are the UNIFIED runs: since the walk and fight brains were merged
        // there is one run and one ONNX per fighter, not two. The pre-merge
        // *_sumo0N and *_walk0N runs are retired and their checkpoints no longer
        // fit the 45-slot observation vector.
        //
        // Matt is on 02: matt_unified01's walk lane was broken (every walker faced
        // away from its target and banked the +3 graduation on its first decision),
        // and because walk and fight share one network that free reward
        // contaminated the whole policy, so it was restarted rather than resumed.
        [MenuItem("PoSumo/Deploy Matt Brain")]
        public static void DeployMatt()
        {
            Deploy("matt_unified02", "Matt", "Assets/Agents/Matt_v01");
        }

        [MenuItem("PoSumo/Deploy Standard Brain")]
        public static void DeployStandard()
        {
            Deploy("standard_unified01", "Standard", "Assets/Agents/Standard_v01");
        }

        [MenuItem("PoSumo/Deploy Nick Brain")]
        public static void DeployNick()
        {
            Deploy("nick_unified01", "Nick", "Assets/Agents/Nick_v01");
        }

        [MenuItem("PoSumo/Deploy Kim Brain")]
        public static void DeployKim()
        {
            Deploy("kim_unified01", "Kim", "Assets/Agents/Kim_v01");
        }

        /// Deploy the newest numbered checkpoint of a RUNNING training run, so a
        /// brain can be tried before the run finishes. The trainer only writes
        /// the unnumbered `<Behavior>.onnx` on shutdown, hence the separate path.
        /// Safe while training continues — this only reads the exported files.
        ///
        /// No menu item: the run id changes every run, so this is invoked by
        /// name through MCP `script-execute` while a run is in flight. Finished
        /// runs keep only their final export, so this finds nothing in them.
        ///
        /// Picks by WRITE TIME, not by step number, and the difference is not
        /// academic. `--force` silently fails to clear a run directory when
        /// TensorBoard is holding it open (see Training/README.md), which leaves
        /// the discarded run's checkpoints sitting next to the new one's. Those
        /// leftovers carry HIGHER step numbers than a restarted run reaches for
        /// hours, so a highest-step rule ships a brain from the run you deleted —
        /// trained on whatever physics you just changed. Write time is monotonic
        /// within a run and always identifies the live one. Ties (the trainer
        /// exports two checkpoints in the same instant on shutdown) break by step.
        public static void DeployLatestCheckpoint(string runId, string behaviorName, string agentFolder)
        {
            string dir = $"Training/results/{runId}/{behaviorName}";
            if (!Directory.Exists(dir))
            {
                Debug.LogError($"DEPLOY RESULT: Failed — no checkpoint directory at {dir}.");
                return;
            }

            string newest = null;
            long newestStep = -1;
            System.DateTime newestWrite = System.DateTime.MinValue;
            long highestStepSeen = -1;

            foreach (string file in Directory.GetFiles(dir, $"{behaviorName}-*.onnx"))
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                int dash = stem.LastIndexOf('-');
                if (dash < 0) continue;
                if (!long.TryParse(stem.Substring(dash + 1), out long step)) continue;

                if (step > highestStepSeen) highestStepSeen = step;

                System.DateTime write = File.GetLastWriteTimeUtc(file);
                if (write > newestWrite || (write == newestWrite && step > newestStep))
                {
                    newestWrite = write;
                    newestStep = step;
                    newest = file;
                }
            }

            if (newest == null)
            {
                Debug.LogError($"DEPLOY RESULT: Failed — no {behaviorName}-<step>.onnx checkpoints in {dir}.");
                return;
            }

            // Loud, because silence here is how a stale brain ships unnoticed.
            if (highestStepSeen > newestStep)
            {
                Debug.LogWarning(
                    $"DEPLOY: {dir} still holds checkpoints from a DISCARDED run — highest step on disk is " +
                    $"{highestStepSeen:N0} but the live run has only reached {newestStep:N0}. Deploying the live " +
                    "one by write time. Delete the leftovers, and kill TensorBoard before the next --force.");
            }

            CopyInto(newest, behaviorName, agentFolder, $"checkpoint @ {newestStep:N0} steps");
        }

        /// runId: folder under Training/results. behaviorName: the ONNX stem the
        /// trainer writes. agentFolder: destination under Assets/Agents.
        public static void Deploy(string runId, string behaviorName, string agentFolder)
        {
            string source = $"Training/results/{runId}/{behaviorName}.onnx";
            if (!File.Exists(source))
            {
                Debug.LogError($"DEPLOY RESULT: Failed — no ONNX at {source}. " +
                               "The run must have finished (or been stopped) so the trainer exported it.");
                return;
            }

            CopyInto(source, behaviorName, agentFolder, "final export");
        }

        // The separate "Deploy <X> Walk Brain" entries are gone. Walk and fight
        // are one policy per fighter now, told apart by the task flag in the
        // observation vector, so there is a single brain to deploy per fighter and
        // a single slot on the character sheet to point at it.

        private static void CopyInto(string source, string behaviorName, string agentFolder, string note)
        {
            Directory.CreateDirectory(agentFolder);
            string destination = $"{agentFolder}/{behaviorName}.onnx";
            File.Copy(source, destination, overwrite: true);
            // ForceSynchronousImport, not just ForceUpdate. The load below runs on
            // the very next line, and a plain ForceUpdate import can still be
            // queued at that point — so the ModelAsset comes back null and this
            // reports a deploy failure for a file that is perfectly fine. It shows
            // up whenever the editor is starved, which is exactly when you are
            // deploying: mid-session with training envs saturating the machine.
            AssetDatabase.ImportAsset(destination,
                ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

            var model = AssetDatabase.LoadAssetAtPath<Unity.InferenceEngine.ModelAsset>(destination);
            if (model == null)
            {
                Debug.LogError($"DEPLOY RESULT: Failed — {destination} did not import as a ModelAsset.");
                return;
            }

            string characterPath = $"{agentFolder}/{behaviorName}_Character.asset";
            var character = AssetDatabase.LoadAssetAtPath<Agent_CharacterDefinition>(characterPath);
            if (character == null)
            {
                Debug.LogError($"DEPLOY RESULT: Failed — no character asset at {characterPath}.");
                return;
            }

            {
                character.inferenceModel = model;
            }
            EditorUtility.SetDirty(character);
            AssetDatabase.SaveAssets();

            var info = new FileInfo(destination);
            Debug.Log($"DEPLOY RESULT: Succeeded | {behaviorName} | {note} | {info.Length / 1024}KB | " +
                      $"{source} -> {destination} | " +
                      "character.inferenceModel set");
        }
    }
}
