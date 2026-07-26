using UnityEngine;

namespace PoSumo
{
    /// Drives Matt's facial expression from the live DOMINANCE stat: winning
    /// looks increasingly happy (happy1-3), losing increasingly sad (sad1-3),
    /// neutral in between. The displayed face always walks the expression
    /// ladder one step at a time (sad3..sad1, neutral, happy1..happy3) so
    /// changes animate smoothly instead of jumping. Big moments steer the
    /// target: scoring a round targets happy3, losing one sad3, a knockdown
    /// sad2; a decided match holds the extreme until rematch. Dominance is
    /// smoothed (~2 s) so the face doesn't flicker. Sprites load from
    /// Resources (Matt_neutral / Matt_happy1-3 / Matt_sad1-3). Spawned by
    /// Systems_GameMatchManager.
    public class Systems_FaceMood : MonoBehaviour
    {
        [Tooltip("Behavior name of the fighter whose face reacts.")]
        public string fighterBehaviorName = "Matt";
        public float flashSeconds = 1.5f;
        public float knockdownFlashSeconds = 1.2f;
        [Tooltip("Per-second rate the mood chases live dominance.")]
        public float smoothingRate = 1.2f;
        [Tooltip("Seconds between single steps along the expression ladder.")]
        public float stepSeconds = 0.18f;
        [Tooltip("Idle flourish: after this long with nothing interesting, Matt rides the ladder to 3 and back.")]
        public float idleMinSeconds = 15f;
        public float idleMaxSeconds = 30f;
        [Tooltip("Maximum idle flourishes per match.")]
        public int idleFlourishesPerMatch = 2;

        // Fallback for characters with no face names on their definition asset
        // (Matt predates the per-character face fields).
        const string FALLBACK_NEUTRAL = "Matt_neutral";
        static readonly string[] FallbackHappy =
        {
            "Matt_happy1",
            "Matt_happy2",
            "Matt_happy3",
        };
        static readonly string[] FallbackSad =
        {
            "Matt_sad1",
            "Matt_sad2",
            "Matt_sad3",
        };

        Systems_GameMatchManager _manager;
        Systems_FightHud _hud;
        Agent_Biped _fighter;
        Agent_BipedBody _body;
        bool _fighterIsA;

        Sprite _neutral;
        Sprite[] _happy, _sad;

        float _smoothedDom = 50f;
        int _displayLevel;         // -3 (sad3) .. 0 (neutral) .. +3 (happy3), moves ±1 per step
        float _nextStepTime;
        Sprite _current;

        int _flashLevel;
        float _flashUntil;
        bool _flashActive;
        bool _holdActive;          // match-over face, kept until rematch
        int _holdLevel;
        bool _wasDown;
        float _kdRearmTime;

        float _lastInterestTime;   // last flash/round event/mood shift
        float _idleDelay;          // rolled 15-30 s after each interesting moment
        int _idleFlourishes;
        int _lastMoodTarget;
        const float FLOURISH_SECONDS = 0.9f; // ladder rides up, holds a beat, walks back

        void Start()
        {
            _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            _hud = FindAnyObjectByType<Systems_FightHud>();
            if (_manager == null) return;

            if (_manager.wrestlerA != null && _manager.wrestlerA.behaviorName == fighterBehaviorName)
            {
                _fighter = _manager.wrestlerA;
                _fighterIsA = true;
            }
            else if (_manager.wrestlerB != null && _manager.wrestlerB.behaviorName == fighterBehaviorName)
            {
                _fighter = _manager.wrestlerB;
                _fighterIsA = false;
            }
            if (_fighter == null) return;
            _body = _fighter.GetComponent<Agent_BipedBody>();

            // Face names come from the character definition; Matt's legacy
            // hardcoded set is the fallback when the asset leaves them empty.
            var def = _body.character;
            string neutralName = def != null && !string.IsNullOrEmpty(def.faceNeutralName)
                ? def.faceNeutralName : FALLBACK_NEUTRAL;
            string[] happyNames = def != null && def.faceHappyNames != null && def.faceHappyNames.Length == 3
                ? def.faceHappyNames : FallbackHappy;
            string[] sadNames = def != null && def.faceSadNames != null && def.faceSadNames.Length == 3
                ? def.faceSadNames : FallbackSad;

            _neutral = Resources.Load<Sprite>(neutralName);
            _happy = new Sprite[happyNames.Length];
            _sad = new Sprite[sadNames.Length];
            int loaded = _neutral != null ? 1 : 0;
            for (int i = 0; i < happyNames.Length; i++)
            {
                _happy[i] = Resources.Load<Sprite>(happyNames[i]);
                _sad[i] = Resources.Load<Sprite>(sadNames[i]);
                if (_happy[i] != null) loaded++;
                if (_sad[i] != null) loaded++;
            }
            if (loaded < 7)
            {
                Debug.LogWarning($"Systems_FaceMood ({fighterBehaviorName}): only {loaded}/7 face sprites loaded from Resources");
            }

            _manager.RoundEnded += OnRoundEnded;
            _manager.MatchEnded += OnMatchEnded;
            _manager.MatchReset += OnMatchReset;
            _manager.RoundStarted += MarkInterest;

            MarkInterest();
            Apply(LevelSprite(0));
        }

        void OnDisable()
        {
            if (_manager != null)
            {
                _manager.RoundEnded -= OnRoundEnded;
                _manager.MatchEnded -= OnMatchEnded;
                _manager.MatchReset -= OnMatchReset;
                _manager.RoundStarted -= MarkInterest;
            }
        }

        void MarkInterest()
        {
            _lastInterestTime = Time.unscaledTime;
            _idleDelay = Random.Range(idleMinSeconds, idleMaxSeconds);
        }

        /// Round results flash to a level scaled by the score margin rather than
        /// always slamming to ±3. Winning the opener shows happy1, pulling ahead
        /// happy2, clinching happy3 — which is what actually gets the middle
        /// expressions on screen. Rounds are far too short for in-round dominance
        /// drift to reach them on its own.
        void OnRoundEnded(Agent_Biped winner, Agent_Biped loser)
        {
            int margin = MarginForFighter();
            int level = Mathf.Clamp(Mathf.Abs(margin), 1, 3);
            if (winner == _fighter) Flash(level, flashSeconds);
            else if (loser == _fighter) Flash(-level, flashSeconds);
        }

        /// Score margin from this fighter's point of view, after the round.
        int MarginForFighter()
        {
            if (_manager == null) return 1;
            int mine = _fighterIsA ? _manager.ScoreA : _manager.ScoreB;
            int theirs = _fighterIsA ? _manager.ScoreB : _manager.ScoreA;
            int margin = mine - theirs;
            return margin == 0 ? 1 : margin;
        }

        void OnMatchEnded(Agent_Biped winner)
        {
            _holdActive = true;
            _holdLevel = winner == _fighter ? 3 : -3;
        }

        void OnMatchReset()
        {
            _holdActive = false;
            _flashActive = false;
            _smoothedDom = 50f;
            _idleFlourishes = 0;
            MarkInterest();
        }

        void Flash(int level, float seconds)
        {
            _flashLevel = level;
            _flashActive = true;
            _flashUntil = Time.unscaledTime + seconds;
            MarkInterest();
        }

        void Update()
        {
            if (_fighter == null || _body == null) return;

            // Knockdown reaction (mid-round, debounced).
            bool down = _fighter.IsDown;
            if (down && !_wasDown && Time.unscaledTime >= _kdRearmTime && _manager.RoundActive)
            {
                Flash(-2, knockdownFlashSeconds);
                _kdRearmTime = Time.unscaledTime + 2f;
            }
            _wasDown = down;

            float dom = _hud != null ? (_fighterIsA ? _hud.DominanceA : _hud.DominanceB) : 50f;
            float t = 1f - Mathf.Exp(-smoothingRate * Time.deltaTime);
            _smoothedDom = Mathf.Lerp(_smoothedDom, dom, t);

            if (_flashActive && Time.unscaledTime >= _flashUntil) _flashActive = false;

            int mood = MoodLevel(_smoothedDom);
            if (mood != _lastMoodTarget)
            {
                _lastMoodTarget = mood;
                MarkInterest(); // a naturally shifting face is interesting enough
            }

            // Idle flourish: a dull stretch earns a quick ladder ride to 3 and
            // back — happy if currently ahead, sad if behind.
            if (!_holdActive && !_flashActive && _manager.RoundActive
                && _idleFlourishes < idleFlourishesPerMatch
                && Time.unscaledTime - _lastInterestTime >= _idleDelay)
            {
                _idleFlourishes++;
                Flash(_smoothedDom >= 50f ? 3 : -3, FLOURISH_SECONDS);
            }

            int target;
            if (_holdActive) target = _holdLevel;
            else if (_flashActive) target = _flashLevel;
            else target = mood;

            // Walk the ladder one expression per step — never jump.
            if (_displayLevel != target && Time.unscaledTime >= _nextStepTime)
            {
                _displayLevel += _displayLevel < target ? 1 : -1;
                _nextStepTime = Time.unscaledTime + stepSeconds;
                Apply(LevelSprite(_displayLevel));
            }
        }

        static int MoodLevel(float dom)
        {
            if (dom >= 76f) return 3;
            if (dom >= 66f) return 2;
            if (dom >= 57f) return 1;
            if (dom <= 24f) return -3;
            if (dom <= 34f) return -2;
            if (dom <= 43f) return -1;
            return 0;
        }

        Sprite LevelSprite(int level)
        {
            if (level > 0) return _happy[level - 1];
            if (level < 0) return _sad[-level - 1];
            return _neutral;
        }

        void Apply(Sprite s)
        {
            if (s == null || s == _current) return;
            _current = s;
            _body.SetHeadSprite(s);
        }
    }
}
