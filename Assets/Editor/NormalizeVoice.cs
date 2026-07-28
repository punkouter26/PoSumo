using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace PoSumo.EditorTools
{
    /// Measures every voice recording and writes the per-clip gains that level
    /// them against each other into Assets/Resources/VoiceGains.asset.
    ///
    /// Non-destructive on purpose: the WAVs are original recordings and a gain
    /// baked into the PCM cannot be taken back out, while a table can be
    /// re-measured, inspected and overridden. Systems_FighterVoice applies it.
    ///
    /// Scoped to Assets/Resources/Audio/Voice and nothing else. The generated
    /// SFX and music one level up (Assets/Resources/Audio) are produced by
    /// GenerateAudio at intentional relative levels, and levelling a music bed
    /// against a gong would flatten the mix rather than fix it.
    ///
    /// This used to scan all of Assets/Resources and skip anything containing
    /// "/Audio/", which worked only while the voice clips sat loose at the
    /// Resources root. They now live under Audio/Voice, so the folder itself is
    /// the filter.
    public static class NormalizeVoice
    {
        private const string ASSET_PATH = "Assets/Resources/VoiceGains.asset";
        private const string VOICE_FOLDER = "Assets/Resources/Audio/Voice";

        /// Everything is levelled to this RMS. -20 dBFS was chosen against the
        /// measured set: the loudest clip needs only -3 dB to reach it and the
        /// quietest needs +15.9 dB, which still leaves its peak at -3.9 dBFS, so
        /// no clip has to be pushed into the ceiling.
        private const float TARGET_RMS_DB = -20f;

        /// No corrected clip may peak above this. A quiet clip that is also peaky
        /// would otherwise clip when raised to the RMS target; it gets less gain
        /// instead and stays slightly under-level, which is the right trade.
        private const float PEAK_CEILING_DB = -1f;

        [MenuItem("PoSumo/Normalize Voice Levels")]
        public static void Run()
        {
            float targetLinear = Mathf.Pow(10f, TARGET_RMS_DB / 20f);
            float ceilingLinear = Mathf.Pow(10f, PEAK_CEILING_DB / 20f);

            var names = new List<string>();
            var gains = new List<float>();
            var report = new StringBuilder();
            int ceilingLimited = 0, unreadable = 0;

            string[] guids = AssetDatabase.FindAssets("t:AudioClip", new[] { VOICE_FOLDER });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (clip == null)
                {
                    continue;
                }

                var data = new float[clip.samples * clip.channels];
                if (!clip.GetData(data, 0))
                {
                    // Almost always a compressed/streaming import setting. Better to
                    // name it than to silently leave the clip uncorrected.
                    Debug.LogWarning($"NormalizeVoice: could not read samples from {clip.name} — " +
                                     "set its import Load Type to Decompress On Load and re-run.");
                    unreadable++;
                    continue;
                }

                double sumSquares = 0.0;
                float peak = 0f;
                for (int dataIndex = 0; dataIndex < data.Length; dataIndex++)
                {
                    float magnitude = Mathf.Abs(data[dataIndex]);
                    if (magnitude > peak) peak = magnitude;
                    sumSquares += (double)data[dataIndex] * data[dataIndex];
                }
                if (data.Length == 0 || peak <= 1e-6f)
                {
                    continue;   // silence: nothing to level
                }

                float rms = Mathf.Sqrt((float)(sumSquares / data.Length));
                float gain = targetLinear / Mathf.Max(1e-6f, rms);

                float peakLimited = ceilingLinear / peak;
                if (peakLimited < gain)
                {
                    gain = peakLimited;
                    ceilingLimited++;
                }

                names.Add(clip.name);
                gains.Add(gain);
                report.AppendLine($"  {clip.name}: {20f * Mathf.Log10(rms):F1} dB -> " +
                                  $"gain {20f * Mathf.Log10(gain):+0.0;-0.0} dB");
            }

            var asset = AssetDatabase.LoadAssetAtPath<Systems_VoiceGains>(ASSET_PATH);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<Systems_VoiceGains>();
                AssetDatabase.CreateAsset(asset, ASSET_PATH);
            }
            asset.SetTable(TARGET_RMS_DB, names, gains);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"NORMALIZE RESULT: {names.Count} clips levelled to {TARGET_RMS_DB} dBFS RMS | " +
                      $"peak-limited={ceilingLimited} unreadable={unreadable} | {ASSET_PATH}\n{report}");
        }
    }
}
