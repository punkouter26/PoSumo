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
            foreach (string file in Directory.GetFiles(dir, $"{behaviorName}-*.onnx"))
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                int dash = stem.LastIndexOf('-');
                if (dash < 0) continue;
                if (!long.TryParse(stem.Substring(dash + 1), out long step)) continue;
                if (step > newestStep)
                {
                    newestStep = step;
                    newest = file;
                }
            }

            if (newest == null)
            {
                Debug.LogError($"DEPLOY RESULT: Failed — no {behaviorName}-<step>.onnx checkpoints in {dir}.");
                return;
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

        static void CopyInto(string source, string behaviorName, string agentFolder, string note)
        {
            Directory.CreateDirectory(agentFolder);
            string destination = $"{agentFolder}/{behaviorName}.onnx";
            File.Copy(source, destination, overwrite: true);
            AssetDatabase.ImportAsset(destination, ImportAssetOptions.ForceUpdate);

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
                      $"{source} -> {destination} | character.inferenceModel set");
        }
    }
}
