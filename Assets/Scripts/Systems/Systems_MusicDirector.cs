using UnityEngine;

namespace PoSumo
{
    /// Adaptive score. The game shipped with no music of any kind.
    ///
    /// Four stems (bed, pulse, drums, tension) are the SAME length and start
    /// together on one Play call, so they stay sample-locked forever. Nothing is
    /// ever started or stopped after that — only faded. That is the entire trick:
    /// crossfading layers of one locked arrangement gives you music that follows
    /// the match without a single audible transition, which cutting between tracks
    /// cannot do.
    ///
    ///   bed      always on, the ceremonial drone
    ///   pulse    enters during the walk-in / countdown, a slow heartbeat
    ///   drums    enters when a round goes live
    ///   tension  enters at match point
    ///
    /// A slow-mo finish pulls everything down to the bed alone, which is why the
    /// gong lands in near-silence.
    ///
    /// Spawned at runtime by Systems_GameMatchManager.
    public sealed class Systems_MusicDirector : MonoBehaviour
    {
        [Range(0f, 1f)] public float musicVolume = 0.5f;
        [Tooltip("Seconds a layer takes to fade in or out. Long — a layer that snaps in reads as a mistake.")]
        public float fadeSeconds = 1.8f;
        [Tooltip("Extra fade time when a layer is coming down, so exits are gentler than entrances.")]
        public float releaseMultiplier = 1.6f;

        private const string AUDIO_PATH = "Audio/";

        private sealed class Layer
        {
            public AudioSource source;
            public float target;
            public float current;
            public float ceiling;
        }

        private Layer _bed, _pulse, _drums, _tension;
        private Systems_GameMatchManager _manager;
        private bool _roundLive;
        private bool _finishing;
        private bool _matchOver;

        private void Awake()
        {
            var bus = new GameObject("Bus_Music");
            bus.transform.SetParent(transform, false);

            _bed = MakeLayer(bus, "MUS_Bed", 1f);
            _pulse = MakeLayer(bus, "MUS_Pulse", 0.85f);
            _drums = MakeLayer(bus, "MUS_Drums", 0.8f);
            _tension = MakeLayer(bus, "MUS_Tension", 0.7f);

            // One synchronised start for every layer that actually loaded. Starting
            // them in separate frames would drift them apart by a frame each and
            // the arrangement would smear.
            double startAt = AudioSettings.dspTime + 0.15;
            PlayScheduled(_bed, startAt);
            PlayScheduled(_pulse, startAt);
            PlayScheduled(_drums, startAt);
            PlayScheduled(_tension, startAt);

            _bed.target = 1f;
        }

        private Layer MakeLayer(GameObject bus, string clipName, float ceiling)
        {
            AudioClip clip = Resources.Load<AudioClip>(AUDIO_PATH + clipName);
            if (clip == null)
            {
                // Music is optional dressing: a project that has not run
                // PoSumo/Generate Audio simply plays without a score.
                return new Layer { ceiling = ceiling };
            }
            var source = bus.AddComponent<AudioSource>();
            source.clip = clip;
            source.loop = true;
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            source.volume = 0f;
            return new Layer { source = source, ceiling = ceiling };
        }

        private static void PlayScheduled(Layer layer, double dspTime)
        {
            if (layer?.source != null)
            {
                layer.source.PlayScheduled(dspTime);
            }
        }

        private void Start()
        {
            _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            if (_manager == null)
            {
                return;
            }
            _manager.RoundStarted += OnRoundStarted;
            _manager.RoundEnded += OnRoundEnded;
            _manager.MatchEnded += OnMatchEnded;
            _manager.MatchReset += OnMatchReset;
        }

        private void OnDisable()
        {
            if (_manager != null)
            {
                _manager.RoundStarted -= OnRoundStarted;
                _manager.RoundEnded -= OnRoundEnded;
                _manager.MatchEnded -= OnMatchEnded;
                _manager.MatchReset -= OnMatchReset;
            }
        }

        private void OnRoundStarted()
        {
            _roundLive = true;
            _finishing = false;
        }

        private void OnRoundEnded(Agent_Biped winner, Agent_Biped loser)
        {
            _roundLive = false;
            // A draw is not a moment; only a decided fall drops the arrangement.
            _finishing = loser != null;
        }

        private void OnMatchEnded(Agent_Biped winner)
        {
            _matchOver = true;
            _finishing = false;
        }

        private void OnMatchReset()
        {
            _matchOver = false;
            _finishing = false;
            _roundLive = false;
        }

        private void Update()
        {
            // A script recompile DURING play mode reloads the domain and nulls
            // every non-serialized field without re-running Awake, so the layers
            // vanish under a live game. Cheap guard rather than a spray of
            // NullReferenceExceptions every frame for the rest of the session.
            if (_bed == null || _pulse == null || _drums == null || _tension == null)
            {
                return;
            }

            bool matchPoint = false;
            if (_manager != null && _manager.pointsToWin > 1)
            {
                matchPoint = Mathf.Max(_manager.ScoreA, _manager.ScoreB) >= _manager.pointsToWin - 1;
            }

            if (_matchOver)
            {
                // The result card gets the bed alone — the crowd and the gong own
                // everything else in that moment.
                _bed.target = 0.85f;
                _pulse.target = 0f;
                _drums.target = 0f;
                _tension.target = 0f;
            }
            else if (_finishing)
            {
                _bed.target = 1f;
                _pulse.target = 0.35f;
                _drums.target = 0f;
                _tension.target = 0f;
            }
            else if (_roundLive)
            {
                _bed.target = 1f;
                _pulse.target = 1f;
                _drums.target = 1f;
                _tension.target = matchPoint ? 1f : 0f;
            }
            else
            {
                // Between rounds: ceremony. Bed and heartbeat, no groove.
                _bed.target = 1f;
                _pulse.target = 0.7f;
                _drums.target = 0f;
                _tension.target = matchPoint ? 0.5f : 0f;
            }

            // Unscaled: a slow-mo finish must not stretch the fades out to ten
            // seconds along with everything else.
            float dt = Time.unscaledDeltaTime;
            Apply(_bed, dt);
            Apply(_pulse, dt);
            Apply(_drums, dt);
            Apply(_tension, dt);
        }

        private void Apply(Layer layer, float dt)
        {
            if (layer?.source == null)
            {
                return;
            }
            float seconds = layer.target > layer.current
                ? fadeSeconds
                : fadeSeconds * releaseMultiplier;
            layer.current = Mathf.MoveTowards(layer.current, layer.target, dt / Mathf.Max(0.01f, seconds));
            layer.source.volume = layer.current * layer.ceiling * musicVolume;
        }
    }
}
