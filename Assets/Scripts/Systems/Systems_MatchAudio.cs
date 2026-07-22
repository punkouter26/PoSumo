using System.Collections.Generic;
using UnityEngine;

namespace PoSumo
{
    /// Physics-driven match audio: body thuds scaled by impact speed, foot
    /// scuffs, taiko at round start and a gong when the match is decided, over
    /// a low crowd-murmur bed. Clips are procedurally generated placeholder
    /// WAVs loaded from Resources/Audio (overwrite the files to reskin).
    /// Spawned at runtime by Systems_GameMatchManager.
    public class Systems_MatchAudio : MonoBehaviour
    {
        public float masterVolume = 1f;

        const string AUDIO_PATH = "Audio/";
        const int VOICE_COUNT = 8;
        const float THUD_MIN_SPEED = 1.2f;      // rel. velocity below this is inaudible
        const float THUD_FULL_SPEED = 6.5f;     // rel. velocity for full-volume thud
        const float SCUFF_MIN_SPEED = 0.5f;
        const float SCUFF_MAX_SPEED = 3.5f;

        AudioClip[] _thuds;
        AudioClip _scuff, _murmur, _taiko, _gong;

        AudioSource[] _voices;
        int _nextVoice;
        AudioSource _murmurSource;

        Systems_GameMatchManager _manager;
        float _nextThudTime;
        readonly Dictionary<Agent_BipedBody, float> _nextScuffTime = new Dictionary<Agent_BipedBody, float>();

        void Awake()
        {
            _thuds = new[]
            {
                Resources.Load<AudioClip>(AUDIO_PATH + "SFX_ThudA"),
                Resources.Load<AudioClip>(AUDIO_PATH + "SFX_ThudB"),
                Resources.Load<AudioClip>(AUDIO_PATH + "SFX_ThudC"),
            };
            _scuff = Resources.Load<AudioClip>(AUDIO_PATH + "SFX_Scuff");
            _murmur = Resources.Load<AudioClip>(AUDIO_PATH + "SFX_CrowdMurmur");
            _taiko = Resources.Load<AudioClip>(AUDIO_PATH + "SFX_Taiko");
            _gong = Resources.Load<AudioClip>(AUDIO_PATH + "SFX_Gong");

            _voices = new AudioSource[VOICE_COUNT];
            for (int i = 0; i < VOICE_COUNT; i++)
            {
                var src = gameObject.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.spatialBlend = 0f;
                _voices[i] = src;
            }

            _murmurSource = gameObject.AddComponent<AudioSource>();
            _murmurSource.playOnAwake = false;
            _murmurSource.spatialBlend = 0f;
            _murmurSource.loop = true;
        }

        void Start()
        {
            _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            if (_manager != null)
            {
                _manager.RoundStarted += OnRoundStarted;
                _manager.MatchEnded += OnMatchEnded;
            }

            if (_murmur != null)
            {
                _murmurSource.clip = _murmur;
                _murmurSource.volume = 0.14f * masterVolume;
                _murmurSource.Play();
            }

            // The first round starts without a RoundStarted event.
            Play(_taiko, 0.8f, 1f);
        }

        void OnEnable() { Sensor_Impact.AnyImpact += OnImpact; }

        void OnDisable()
        {
            Sensor_Impact.AnyImpact -= OnImpact;
            if (_manager != null)
            {
                _manager.RoundStarted -= OnRoundStarted;
                _manager.MatchEnded -= OnMatchEnded;
            }
        }

        void OnRoundStarted() { Play(_taiko, 0.8f, 1f); }

        void OnMatchEnded(Agent_Biped winner)
        {
            Play(_gong, 1f, 1f);
        }

        static bool IsStatic(Collision2D c) =>
            c.rigidbody == null || c.rigidbody.bodyType == RigidbodyType2D.Static;

        void OnImpact(Sensor_Impact sensor, Collision2D c)
        {
            float speed = c.relativeVelocity.magnitude;
            float now = Time.unscaledTime;

            if (IsStatic(c))
            {
                if (sensor.isFoot)
                {
                    if (speed < SCUFF_MIN_SPEED || speed > SCUFF_MAX_SPEED) return;
                    float next;
                    if (_nextScuffTime.TryGetValue(sensor.owner, out next) && now < next) return;
                    _nextScuffTime[sensor.owner] = now + 0.14f;
                    Play(_scuff, Mathf.Lerp(0.08f, 0.3f, speed / SCUFF_MAX_SPEED),
                         Random.Range(0.85f, 1.15f));
                    return;
                }
                // Body part slamming the ground.
                if (speed > 2f && now >= _nextThudTime)
                {
                    _nextThudTime = now + 0.06f;
                    PlayThud(speed, 0.75f);
                }
                return;
            }

            // Dynamic contact: only wrestler-vs-wrestler hits are interesting,
            // and both sides report, so a short global cooldown dedupes them.
            var otherBody = c.collider.GetComponentInParent<Agent_BipedBody>();
            if (otherBody == null || otherBody == sensor.owner) return;
            if (speed < THUD_MIN_SPEED || now < _nextThudTime) return;
            _nextThudTime = now + 0.06f;
            PlayThud(speed, 1f);
        }

        void PlayThud(float speed, float pitchScale)
        {
            float t = Mathf.Clamp01((speed - THUD_MIN_SPEED) / (THUD_FULL_SPEED - THUD_MIN_SPEED));
            var clip = _thuds[Random.Range(0, _thuds.Length)];
            Play(clip, Mathf.Lerp(0.15f, 1f, t), pitchScale * Random.Range(0.9f, 1.1f));
        }

        void Play(AudioClip clip, float volume, float pitch)
        {
            if (clip == null) return;
            var src = _voices[_nextVoice];
            _nextVoice = (_nextVoice + 1) % VOICE_COUNT;
            src.pitch = pitch;
            src.PlayOneShot(clip, volume * masterVolume);
        }
    }
}
