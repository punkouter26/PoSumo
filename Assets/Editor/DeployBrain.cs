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
        private const string RESULTS_DIR = "Training/results";

        // The menu items no longer pin a run id. They used to hold string literals
        // (*_unified01 -> *_stamina01 -> ...) that rotted every time the campaign
        // moved on: nothing checked them until a human clicked the menu, and the
        // runs they named were pruned long before anyone did. Training/results is
        // gitignored and absent from a fresh clone, so a literal can never be
        // right there either.
        //
        // Each item now resolves the NEWEST finished run for its fighter — the
        // most recently written `<name>_*/<Behavior>.onnx` under Training/results,
        // case-insensitive on the run prefix — by write time, for the same reason
        // DeployLatestCheckpoint picks by write time: a `--force` that TensorBoard
        // blocked leaves an older run's files beside the new one's, and a name
        // sort would happily ship the stale run.
        //
        // These are the UNIFIED runs: since the walk and fight brains were merged
        // there is one run and one ONNX per fighter, not two.
        [MenuItem("PoSumo/Deploy Matt Brain")]
        public static void DeployMatt()
        {
            DeployNewest("Matt", "Assets/Agents/Matt_v01");
        }

        [MenuItem("PoSumo/Deploy Standard Brain")]
        public static void DeployStandard()
        {
            DeployNewest("Standard", "Assets/Agents/Standard_v01");
        }

        [MenuItem("PoSumo/Deploy Nick Brain")]
        public static void DeployNick()
        {
            DeployNewest("Nick", "Assets/Agents/Nick_v01");
        }

        [MenuItem("PoSumo/Deploy Kim Brain")]
        public static void DeployKim()
        {
            DeployNewest("Kim", "Assets/Agents/Kim_v01");
        }

        /// Deploys the newest finished run whose id starts with `<behavior>_`
        /// (case-insensitive) and holds a final `<Behavior>.onnx` export.
        public static void DeployNewest(string behaviorName, string agentFolder)
        {
            string runId = ResolveNewestRun(behaviorName);
            if (runId == null)
            {
                return;   // ResolveNewestRun already logged the DEPLOY RESULT line
            }
            Deploy(runId, behaviorName, agentFolder);
        }

        /// Newest run directory under Training/results for `behaviorName`, judged
        /// by the write time of its final ONNX export. Null (after logging a
        /// DEPLOY RESULT failure) when the results directory is absent or holds no
        /// finished run for that fighter.
        private static string ResolveNewestRun(string behaviorName)
        {
            if (!Directory.Exists(RESULTS_DIR))
            {
                Debug.LogError($"DEPLOY RESULT: Failed — {RESULTS_DIR} absent. It is gitignored: re-run " +
                               "training locally to recreate it, or copy a run in from elsewhere.");
                return null;
            }

            string prefix = behaviorName + "_";
            string newestRun = null;
            System.DateTime newestWrite = System.DateTime.MinValue;

            foreach (string runDir in Directory.GetDirectories(RESULTS_DIR))
            {
                string runId = Path.GetFileName(runDir);
                if (!runId.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string onnx = Path.Combine(runDir, behaviorName + ".onnx");
                if (!File.Exists(onnx))
                {
                    continue;
                }
                System.DateTime write = File.GetLastWriteTimeUtc(onnx);
                if (newestRun == null || write > newestWrite)
                {
                    newestWrite = write;
                    newestRun = runId;
                }
            }

            if (newestRun == null)
            {
                Debug.LogError($"DEPLOY RESULT: Failed — no run under {RESULTS_DIR} named {prefix}* holds a " +
                               $"finished {behaviorName}.onnx. The run must have finished (or been stopped) " +
                               "so the trainer exported it.");
            }
            return newestRun;
        }

        /// Ships all four in one click. Added because deploying the roster was four
        /// menu trips, and a half-deployed roster (two fighters on a new generation,
        /// two on the old) is silently wrong rather than broken — both ONNX files
        /// load fine, they just came from different training runs.
        [MenuItem("PoSumo/Deploy ALL Brains")]
        public static void DeployAll()
        {
            DeployStandard();
            DeployMatt();
            DeployNick();
            DeployKim();
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
            string dir = $"{RESULTS_DIR}/{runId}/{behaviorName}";
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
            string source = $"{RESULTS_DIR}/{runId}/{behaviorName}.onnx";
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

            character.inferenceModel = model;
            EditorUtility.SetDirty(character);
            AssetDatabase.SaveAssets();

            var info = new FileInfo(destination);
            Debug.Log($"DEPLOY RESULT: Succeeded | {behaviorName} | {note} | {info.Length / 1024}KB | " +
                      $"{source} -> {destination} | " +
                      "character.inferenceModel set");
        }
    }
}
