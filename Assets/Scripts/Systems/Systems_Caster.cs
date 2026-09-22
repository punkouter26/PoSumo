using UnityEngine;
using UnityEngine.UIElements;

namespace PoSumo
{
    /// The play-by-play caster: short broadcast lines over the fight, assembled
    /// from telemetry the referee and the fighters are already producing.
    ///
    /// A TEXT caster on purpose. Per-fighter spoken banks exist
    /// (`Systems_FighterVoice`) but a caster voice would be a new recorded set
    /// keyed to nobody in particular; the lines here are written to be READ in
    /// under two seconds, which is also what keeps them off the slow-mo finish.
    /// Text is the half that costs nothing to ship and cannot be clipped wrong.
    ///
    /// Sources, in the shape the house rules want them:
    ///   - the manager it was spawned by (its four events, plus score/round);
    ///   - the `Knockout` / `Dismembered` / `Gibbed` STATICS on
    ///     `Systems_BodyDamage`, subscribed and unsubscribed in
    ///     OnEnable/OnDisable because a static outlives a scene load;
    ///   - `Systems_ArenaMutators.Telegraphed`, a static for the same reason;
    ///   - sibling reads via FindAnyObjectByType in Start, the same way
    ///     `Systems_CrowdMomentum` reads `Systems_FightHud` and
    ///     `Systems_MatchAudio` reads `Systems_CrowdMomentum`.
    ///
    /// Discipline: a line queue with a minimum gap (a caster that talks over
    /// itself is noise), one-shot flags per round so a threshold cannot repeat,
    /// and no allocation outside the moment a line is actually emitted.
    /// Read-only with respect to the fight.
    public sealed class Systems_Caster : MonoBehaviour
    {
        /// One line's lifetime and the minimum gap between lines.
        private const float SHOW_SECONDS = 3.4f;
        private const float MIN_GAP = 2.1f;

        /// Poll cadence for threshold lines (stamina, odds, mat, crowd).
        private const float POLL_INTERVAL = 0.5f;

        private const int QUEUE_CAPACITY = 3;

        private Systems_GameMatchManager _manager;
        private Systems_HudRoot _hud;
        private Systems_TensionEngine _tension;
        private Systems_CrowdMomentum _crowd;
        private Systems_FightHud _fightHud;
        private Agent_BipedBody _bodyA, _bodyB;

        private Label _line;
        private VisualElement _panel;
        private float _hideAt = -1f;
        private float _nextAllowed = 0f;

        private readonly string[] _queue = new string[QUEUE_CAPACITY];
        private int _queueHead, _queueTail;

        // One-shot flags per round. Reset in OnRoundStarted.
        private bool _saidGassingA, _saidGassingB;
        private bool _saidControlA, _saidControlB;
        private bool _saidComeback;
        private bool _saidMat;
        private bool _saidCrowd;
        private bool _saidOpening;
        private bool _subscribed;

        private const float GASSED_AT = 0.35f;
        private const float CONTROL_WP = 0.86f;
        private const float COMEBACK_WP = 0.55f;
        private const float COMEBACK_FROM = 0.75f;
        private const float MAT_LOW_SHARE = 0.42f;
        private const float CROWD_ROAR = 0.85f;

        private void Awake()
        {
            _manager = GetComponentInParent<Systems_GameMatchManager>();
        }

        private void OnEnable()
        {
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        private void Subscribe()
        {
            if (_subscribed) return;

            if (_manager != null)
            {
                _manager.RoundStarted += OnRoundStarted;
                _manager.RoundEnded += OnRoundEnded;
                _manager.MatchEnded += OnMatchEnded;
                _manager.MatchReset += OnMatchReset;
            }
            // Statics: subscribe exactly once per instance and always unsubscribe
            // in OnDisable — a static handler holding a destroyed component is
            // the documented way a scene load poisons the next bout.
            Systems_BodyDamage.Knockout += OnKnockout;
            Systems_BodyDamage.Dismembered += OnDismembered;
            Systems_ArenaMutators.Telegraphed += OnMutator;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;

            if (_manager != null)
            {
                _manager.RoundStarted -= OnRoundStarted;
                _manager.RoundEnded -= OnRoundEnded;
                _manager.MatchEnded -= OnMatchEnded;
                _manager.MatchReset -= OnMatchReset;
            }
            Systems_BodyDamage.Knockout -= OnKnockout;
            Systems_BodyDamage.Dismembered -= OnDismembered;
            Systems_ArenaMutators.Telegraphed -= OnMutator;
            _subscribed = false;
        }

        private void Start()
        {
            if (_manager == null) _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            _hud = FindAnyObjectByType<Systems_HudRoot>();
            _tension = FindAnyObjectByType<Systems_TensionEngine>();
            _crowd = FindAnyObjectByType<Systems_CrowdMomentum>();
            _fightHud = FindAnyObjectByType<Systems_FightHud>();
            BuildUi();
            ResolveBodies();
        }

        private void ResolveBodies()
        {
            if (_manager == null) return;
            _bodyA = _manager.wrestlerA != null
                ? _manager.wrestlerA.GetComponent<Agent_BipedBody>() : null;
            _bodyB = _manager.wrestlerB != null
                ? _manager.wrestlerB.GetComponent<Agent_BipedBody>() : null;
        }

        // ---- UI -------------------------------------------------------------

        private void BuildUi()
        {
            if (_hud == null || _hud.Stage == null) return;

            // Top of the stage band, in flow terms absolute: the caster chyron.
            // NOT the centre-callout layer — that layer is exclusive by design
            // (countdown, round banner, kimarite share it) and a caster line has
            // no business hiding the gyoji's call. Anchored top-centre instead,
            // where it competes with nothing but empty crowd wall.
            _panel = Systems_UiKit.Card(new Color(0f, 0f, 0f, 0.66f), Systems_UiKit.RADIUS_MD).NoPick();
            _panel.style.position = Position.Absolute;
            _panel.style.top = Systems_UiKit.SPACE_2;
            _panel.style.left = Systems_UiKit.SPACE_4;
            _panel.style.right = Systems_UiKit.SPACE_4;
            _panel.style.alignItems = Align.Center;
            _panel.Pad(Systems_UiKit.SPACE_4, Systems_UiKit.SPACE_1);

            _line = Systems_UiKit.Text(string.Empty, Systems_UiKit.FONT_LEAD, Systems_UiKit.Gold, true);
            _line.style.unityTextAlign = TextAnchor.MiddleCenter;
            _line.style.textShadow = Systems_UiKit.Outline;
            _panel.Add(_line);

            _panel.style.display = DisplayStyle.None;
            _panel.NoPickTree();
            _hud.Stage.Add(_panel);
        }

        // ---- Queue ----------------------------------------------------------

        private void Enqueue(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            Systems_Log.Info("[CAST] " + line);

            int next = (_queueTail + 1) % QUEUE_CAPACITY;
            if (next == _queueHead) return; // full: drop rather than stack a backlog
            _queue[_queueTail] = line;
            _queueTail = next;
        }

        private void Update()
        {
            PollThresholds();

            bool showing = _hideAt > 0f;
            if (showing && Time.unscaledTime >= _hideAt)
            {
                _hideAt = -1f;
                if (_panel != null) _panel.style.display = DisplayStyle.None;
                _nextAllowed = Time.unscaledTime + MIN_GAP * 0.5f;
            }

            if (!showing && _queueHead != _queueTail && Time.unscaledTime >= _nextAllowed)
            {
                Show(_queue[_queueHead]);
                _queue[_queueHead] = null;
                _queueHead = (_queueHead + 1) % QUEUE_CAPACITY;
            }
        }

        private void Show(string line)
        {
            if (_panel == null || _line == null) return;
            _line.text = line;
            _panel.style.display = DisplayStyle.Flex;
            _panel.FadeIn();
            _hideAt = Time.unscaledTime + SHOW_SECONDS;
        }

        // ---- Threshold polling ----------------------------------------------

        private void PollThresholds()
        {
            if (_manager == null || !_manager.RoundActive || !_manager.ScoringLive) return;

            float now = Time.unscaledTime;
            if (now < _pollLeft) return;
            _pollLeft = now + POLL_INTERVAL;

            // Stamina: the gas tank, once per fighter per round.
            if (!_saidGassingA && _bodyA != null && _bodyA.Stamina < GASSED_AT)
            {
                _saidGassingA = true;
                Enqueue(NameA() + " IS GASSING");
            }
            if (!_saidGassingB && _bodyB != null && _bodyB.Stamina < GASSED_AT)
            {
                _saidGassingB = true;
                Enqueue(NameB() + " IS GASSING");
            }

            // Odds: control, then the comeback check against where the bar WAS.
            if (_tension != null)
            {
                float wpA = _tension.WinProbA;
                if (!_saidControlA && wpA >= CONTROL_WP)
                {
                    _saidControlA = true;
                    Enqueue(NameA() + " IS IN FULL CONTROL");
                }
                if (!_saidControlB && wpA <= 1f - CONTROL_WP)
                {
                    _saidControlB = true;
                    Enqueue(NameB() + " IS IN FULL CONTROL");
                }
                if (!_saidComeback && _wpHighWas && wpA >= COMEBACK_WP && wpA <= COMEBACK_FROM)
                {
                    _saidComeback = true;
                    Enqueue("THE COMEBACK IS ON - " + NameB() + " SURGES");
                }
                if (!_saidComeback && !_wpHighWas && wpA <= 1f - COMEBACK_WP && wpA >= 1f - COMEBACK_FROM)
                {
                    _saidComeback = true;
                    Enqueue("THE COMEBACK IS ON - " + NameA() + " SURGES");
                }
                if (wpA >= COMEBACK_FROM) _wpHighWas = true;
                else if (wpA <= 1f - COMEBACK_FROM) _wpHighWas = false;
            }

            // Mat: the real referee of this game. Say it once, when the squeeze
            // has taken most of the clay away.
            if (!_saidMat && _manager.CurrentRingHalfWidth / Mathf.Max(0.1f, _manager.ringHalfWidth) < MAT_LOW_SHARE)
            {
                _saidMat = true;
                Enqueue("THE MAT IS GOING");
            }

            // Crowd: only when the backing is loud enough to matter.
            if (!_saidCrowd && _crowd != null && _crowd.Support01 >= CROWD_ROAR)
            {
                _saidCrowd = true;
                Enqueue((_crowd.BackedIsA ? NameA() : NameB()) + " HAS THE CROWD");
            }
        }

        private float _pollLeft;
        private bool _wpHighWas;

        // ---- Event lines ------------------------------------------------------

        private void OnRoundStarted()
        {
            _saidGassingA = _saidGassingB = false;
            _saidControlA = _saidControlB = false;
            _saidComeback = false;
            _saidMat = false;
            _saidCrowd = false;
            _wpHighWas = false;
            ResolveBodies();

            // The opening call: the storyline (rivalry, streak, upset watch) on
            // round one, the state of the match on every later one.
            if (!_saidOpening)
            {
                _saidOpening = true;
                string story = Systems_Storylines.PreFight(
                    Behaviour(_manager != null ? _manager.wrestlerA : null),
                    Behaviour(_manager != null ? _manager.wrestlerB : null),
                    NameA(), NameB());
                if (story != null)
                {
                    Enqueue(story);
                }
                else
                {
                    Enqueue("FIRST TO " + _manager.PointsToWin);
                }
            }
            else
            {
                int scoreA = _manager.ScoreA;
                int scoreB = _manager.ScoreB;
                Enqueue("ROUND " + _manager.RoundNumber + " - " +
                        (scoreA == scoreB
                            ? "LEVEL " + scoreA + "-" + scoreB
                            : (scoreA > scoreB ? NameA() : NameB()) + " LEADS " +
                              Mathf.Max(scoreA, scoreB) + "-" + Mathf.Min(scoreA, scoreB)));
            }
        }

        private void OnRoundEnded(Agent_Biped winner, Agent_Biped loser)
        {
            if (winner == null || loser == null)
            {
                Enqueue("BOTH OVER THE EDGE - NO DECISION");
                return;
            }
            Enqueue(DisplayName(winner) + " TAKES THE ROUND");
        }

        private void OnMatchEnded(Agent_Biped winner)
        {
            if (winner == null) return;
            string stage = Systems_TournamentState.Active
                ? Systems_TournamentState.RoundName(Systems_TournamentState.CurrentMatch)
                : null;
            Enqueue(DisplayName(winner) + " TAKES THE MATCH"
                    + (stage != null ? " - " + stage : string.Empty));
        }

        private void OnMatchReset()
        {
            _saidOpening = false;
            _queueHead = _queueTail = 0;
            _hideAt = -1f;
            if (_panel != null) _panel.style.display = DisplayStyle.None;
        }

        private void OnKnockout(Agent_BipedBody body, Vector3 at)
        {
            Enqueue("THE HEAD SHOT LANDS" + (IsSideA(body) ? " - " + NameA() : " - " + NameB()));
        }

        private void OnDismembered(Agent_BipedBody body, Systems_BodyDamage.Region region, Vector3 at)
        {
            Enqueue(DisplayName(BodyFighter(body)) + " LOSES THE " + RegionWord(region));
        }

        private void OnMutator(string announcement)
        {
            Enqueue(announcement);
        }

        // ---- Naming -----------------------------------------------------------

        private string NameA()
        {
            return _manager != null ? DisplayName(_manager.wrestlerA) : "A";
        }

        private string NameB()
        {
            return _manager != null ? DisplayName(_manager.wrestlerB) : "B";
        }

        private static string DisplayName(Agent_Biped fighter)
        {
            if (fighter == null) return "—";
            if (!string.IsNullOrEmpty(fighter.displayNameOverride))
            {
                return fighter.displayNameOverride;
            }
            return fighter.character != null ? fighter.character.behaviorName : fighter.name;
        }

        private static string Behaviour(Agent_Biped fighter)
        {
            if (fighter == null || fighter.character == null || fighter.character.useBot)
            {
                return null;
            }
            return fighter.character.behaviorName;
        }

        private bool IsSideA(Agent_BipedBody body)
        {
            return body != null && body == _bodyA;
        }

        private Agent_Biped BodyFighter(Agent_BipedBody body)
        {
            if (body == null) return null;
            if (body == _bodyA) return _manager != null ? _manager.wrestlerA : null;
            if (body == _bodyB) return _manager != null ? _manager.wrestlerB : null;
            return body.GetComponent<Agent_Biped>();
        }

        private static string RegionWord(Systems_BodyDamage.Region region)
        {
            switch (region)
            {
                case Systems_BodyDamage.Region.Head: return "HEAD";
                case Systems_BodyDamage.Region.ArmNear:
                case Systems_BodyDamage.Region.ArmFar: return "ARM";
                case Systems_BodyDamage.Region.LegNear:
                case Systems_BodyDamage.Region.LegFar: return "LEG";
                default: return "LIMB";
            }
        }
    }
}
