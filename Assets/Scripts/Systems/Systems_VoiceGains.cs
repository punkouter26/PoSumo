using System.Collections.Generic;
using UnityEngine;

namespace PoSumo
{
    /// Per-clip playback gain that levels the voice recordings against each other.
    ///
    /// The recordings arrived from different takes at different mic distances and
    /// measured 20.7 dB apart in RMS — Nick's set averaged ~7 dB below Matt's, so
    /// side by side one fighter mumbled and the other shouted. Rather than rewrite
    /// the source WAVs, which cannot be undone, the editor pass measures each clip
    /// once and stores the correction here; Systems_FighterVoice multiplies its
    /// volume by it at play time.
    ///
    /// Regenerate with PoSumo > Normalize Voice Levels after adding or replacing
    /// any recording. A clip with no entry plays at gain 1, so a new file is never
    /// silenced by being missing from the table — it just goes uncorrected until
    /// the pass is re-run.
    [CreateAssetMenu(fileName = "VoiceGains", menuName = "PoSumo/Voice Gains")]
    public sealed class Systems_VoiceGains : ScriptableObject, ISerializationCallbackReceiver
    {
        [Tooltip("RMS the pass levelled everything to, in dBFS. Recorded for reference; changing it here does nothing until the pass is re-run.")]
        [SerializeField] private float _targetRmsDb = -20f;
        [SerializeField] private List<string> _clipNames = new List<string>();
        [SerializeField] private List<float> _gains = new List<float>();

        // Unity cannot serialize a Dictionary, so the table lives as two parallel
        // lists and is rebuilt into a lookup on deserialize.
        private readonly Dictionary<string, float> _lookup = new Dictionary<string, float>();

        public float TargetRmsDb => _targetRmsDb;
        public int Count => _clipNames.Count;

        /// Playback multiplier for a clip, or 1 when it was never measured.
        public float GainFor(string clipName)
        {
            if (string.IsNullOrEmpty(clipName)) return 1f;
            return _lookup.TryGetValue(clipName, out float gain) ? gain : 1f;
        }

        /// Called by the editor normalization pass. Not for runtime use.
        public void SetTable(float targetRmsDb, List<string> names, List<float> gains)
        {
            _targetRmsDb = targetRmsDb;
            _clipNames = names;
            _gains = gains;
            OnAfterDeserialize();
        }

        public void OnBeforeSerialize() { }

        public void OnAfterDeserialize()
        {
            _lookup.Clear();
            int count = Mathf.Min(_clipNames.Count, _gains.Count);
            for (int countIndex = 0; countIndex < count; countIndex++)
            {
                _lookup[_clipNames[countIndex]] = _gains[countIndex];
            }
        }
    }
}
