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
        [MenuItem("PoSumo/Deploy Nick Brain")]
        public static void DeployNick()
        {
            Deploy("nick_sumo01", "Nick", "Assets/Agents/Nick_v01");
        }

        [MenuItem("PoSumo/Deploy Kim Brain")]
        public static void DeployKim()
        {
            Deploy("kim_sumo01", "Kim", "Assets/Agents/Kim_v01");
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
            Debug.Log($"DEPLOY RESULT: Succeeded | {behaviorName} | {info.Length / 1024}KB | " +
                      $"{source} -> {destination} | character.inferenceModel set");
        }
    }
}
