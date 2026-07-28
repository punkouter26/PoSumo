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
        [MenuItem("PoSumo/Deploy Matt Brain")]
        public static void DeployMatt()
        {
            Deploy("matt_sumo05", "Matt", "Assets/Agents/Matt_v01");
        }

        [MenuItem("PoSumo/Deploy Standard Brain")]
        public static void DeployStandard()
        {
            Deploy("standard_sumo01", "Standard", "Assets/Agents/Standard_v01");
        }

        [MenuItem("PoSumo/Deploy Nick Brain")]
        public static void DeployNick()
        {
            Deploy("nick_sumo02", "Nick", "Assets/Agents/Nick_v01");
        }

        [MenuItem("PoSumo/Deploy Kim Brain")]
        public static void DeployKim()
        {
            Deploy("kim_sumo01", "Kim", "Assets/Agents/Kim_v01");
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

        // Every fighter now owns a walk brain trained on the corrected skeleton
        // (2026-07-28 joint-direction fix). Standard used to borrow matt_walk01's
        // export; standard_walk02 replaces that, so all four are first-class runs
        // and each entry below is pinned to the run backing its shipped brain.
        [MenuItem("PoSumo/Deploy Matt Walk Brain")]
        public static void DeployMattWalk()
        {
            DeployWalk("matt_walk03", "Matt", "Assets/Agents/Matt_v01");
        }

        [MenuItem("PoSumo/Deploy Standard Walk Brain")]
        public static void DeployStandardWalk()
        {
            DeployWalk("standard_walk02", "Standard", "Assets/Agents/Standard_v01");
        }

        [MenuItem("PoSumo/Deploy Kim Walk Brain")]
        public static void DeployKimWalk()
        {
            DeployWalk("kim_walk03", "Kim", "Assets/Agents/Kim_v01");
        }

        [MenuItem("PoSumo/Deploy Nick Walk Brain")]
        public static void DeployNickWalk()
        {
            DeployWalk("nick_walk03", "Nick", "Assets/Agents/Nick_v01");
        }

        /// Deploy a locomotion brain: the trainer exports it as `<Behavior>.onnx`
        /// like any other run, but it lands as `<Name>Walk.onnx` and wires to the
        /// character's `walkModel` — the brain Agent_Biped.BeginWalkIn borrows for
        /// the round-opening walk-in. Deploying it over `inferenceModel` would
        /// replace the fighter's fight brain with a walker.
        public static void DeployWalk(string runId, string behaviorName, string agentFolder)
        {
            string source = $"Training/results/{runId}/{behaviorName}.onnx";
            if (!File.Exists(source))
            {
                Debug.LogError($"DEPLOY RESULT: Failed — no ONNX at {source}. " +
                               "The run must have finished (or been stopped) so the trainer exported it.");
                return;
            }

            CopyInto(source, behaviorName, agentFolder, "walk brain", walkSlot: true);
        }

        static void CopyInto(string source, string behaviorName, string agentFolder, string note,
                             bool walkSlot = false)
        {
            Directory.CreateDirectory(agentFolder);
            string destination = walkSlot
                ? $"{agentFolder}/{behaviorName}Walk.onnx"
                : $"{agentFolder}/{behaviorName}.onnx";
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

            if (walkSlot)
            {
                character.walkModel = model;
            }
            else
            {
                character.inferenceModel = model;
            }
            EditorUtility.SetDirty(character);
            AssetDatabase.SaveAssets();

            var info = new FileInfo(destination);
            Debug.Log($"DEPLOY RESULT: Succeeded | {behaviorName} | {note} | {info.Length / 1024}KB | " +
                      $"{source} -> {destination} | " +
                      $"character.{(walkSlot ? "walkModel" : "inferenceModel")} set");
        }
    }
}
