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

        /// Cached, not looked up per frame. Same cross-companion read that
        /// `Systems_FaceMood` and `Systems_FighterVoice` already do for dominance —
        /// reusing the one crowd signal on screen rather than computing a second
        /// that could disagree with the meter the player is watching.
        private Systems_CrowdMomentum _crowd;
        private bool _crowdSearched;

        private float CrowdSupport01()
        {
            if (!_crowdSearched)
            {
                _crowdSearched = true;
                _crowd = FindAnyObjectByType<Systems_CrowdMomentum>();
            }
            // Null whenever enableCrowdMomentum is off — the layer simply loses one
            // of its three reasons rather than erroring.
            return _crowd != null ? _crowd.Support01 : 0f;
        }

        /// How close the NEARER-to-out fighter is to the rim, 0 at the centre and 1
        /// at the edge. Reads the manager's live `ringHalfWidth`, which shrinks
        /// through the round, so the music tightens as the mat closes.
        private float EdgeDanger01()
        {
            if (_manager == null || _manager.ringHalfWidth <= 0.01f) return 0f;

            float centre = _manager.transform.position.x;
            float half = _manager.ringHalfWidth;
            float worst = 0f;
            if (_manager.wrestlerA != null)
                worst = Mathf.Max(worst, Mathf.Abs(_manager.wrestlerA.TorsoX - centre) / half);
            if (_manager.wrestlerB != null)
                worst = Mathf.Max(worst, Mathf.Abs(_manager.wrestlerB.TorsoX - centre) / half);

            // Dead zone: the opening stand-off is already ~0.6 of the half-width, so
            // an ungated reading would start the round half-tense every time.
            return Mathf.Clamp01((worst - 0.65f) / 0.35f);
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
                // Tension used to be BINARY on match point, so most of most matches
                // played the same three layers flat. It is now continuous and takes
                // the loudest of three independent reasons to be tense:
                //
                //   match point   - the round can end the match
                //   edge danger   - someone is close to being pushed out
                //   crowd support - the underdog is being roared back in
                //
                // Max rather than sum: these overlap constantly (a cornered fighter
                // is usually also the one the crowd is behind) and adding them would
                // peg the layer for most of a bout, which is the same flatness in a
                // new costume.
                _tension.target = Mathf.Max(matchPoint ? 1f : 0f,
                                            Mathf.Max(EdgeDanger01(), CrowdSupport01()));
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
