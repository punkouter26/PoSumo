using UnityEngine;
using UnityEngine.UIElements;

namespace PoSumo
{
    /// The crowd gets behind whoever is losing, and it is worth something.
    ///
    /// Support builds for the fighter who is behind — on score first, on dominance
    /// as the tiebreak — and builds FASTER while they are being driven toward the
    /// rim, because that is when a real crowd gets loud. Sustained support grants a
    /// small whole-body torque boost, so a fighter can be roared back into a bout
    /// they were losing.
    ///
    /// > **This changes who wins rounds, and the training referee has no equivalent
    /// > — so no brain has ever trained against it.** That puts it in the same
    /// > class as `knockoutsToLoseMatch`: a deliberately game-only rule. It is
    /// > acceptable here for the same reason — porting it into
    /// > `Systems_SumoMatchManager` would mean the policy learns to farm the
    /// > comeback bonus by deliberately losing ground early, which is the exact
    /// > shaping-exploit failure `CLAUDE.md` warns about. Keep `MAX_BOOST` small
    /// > enough to swing a close round and too small to rescue a bad one.
    ///
    /// Spawned by `Systems_GameMatchManager` behind `enableCrowdMomentum`; never
    /// placed in a scene. Reads `Systems_FightHud.DominanceA/B` through
    /// `FindAnyObjectByType`, the same way `Systems_FaceMood` and
    /// `Systems_FighterVoice` already do — deliberately reusing the one dominance
    /// signal on screen rather than computing a second one that could disagree with
    /// the bar the player is watching.
    public sealed class Systems_CrowdMomentum : MonoBehaviour
    {
        /// Peak extra torque at full crowd support. Small on purpose — see the
        /// class note. 12% is inside the band a fighter already loses to fatigue
        /// (`FATIGUE_DEPTH` is 35%), so it can decide a close round and cannot
        /// manufacture a win.
        private const float MAX_BOOST = 0.12f;

        /// Seconds of one-sided pressure to go from no support to full support.
        private const float BUILD_SECONDS = 6f;

        /// Support bleeds away this many times faster than it builds once the
        /// backed fighter stops being the underdog — the crowd quiets quickly.
        private const float DECAY_MULTIPLIER = 2.5f;

        /// Extra build rate while the backed fighter is inside the last of the mat.
        private const float RIM_URGENCY = 1.8f;

        /// Dominance gap (0-100 scale) below which the bout counts as even and the
        /// crowd picks nobody. Prevents the backing from flickering between
        /// fighters on noise.
        private const float EVEN_BAND = 6f;

        private Systems_GameMatchManager _manager;
        private Systems_FightHud _hud;
        private Agent_BipedBody _bodyA, _bodyB;

        private VisualElement _meterTrack, _meterFill;
        private Label _meterLabel;

        /// 0 = silent, 1 = the crowd is fully behind `BackedIsA`.
        public float Support01 { get; private set; }

        /// Which fighter the crowd is behind while `Support01` is above zero.
        public bool BackedIsA { get; private set; }

        private void Awake()
        {
            _manager = GetComponentInParent<Systems_GameMatchManager>();
        }

        private void OnEnable()
        {
            if (_manager != null)
            {
                _manager.RoundStarted += ResetSupport;
                _manager.MatchReset += ResetSupport;
            }
        }

        private void OnDisable()
        {
            if (_manager != null)
            {
                _manager.RoundStarted -= ResetSupport;
                _manager.MatchReset -= ResetSupport;
            }
            // Never leave a boost applied. This component can be destroyed mid-round
            // by a scene load between bracket bouts, and adrenaline is NonSerialized
            // runtime state on a body that may outlive it by a frame.
            ClearBoost();
        }

        private void Start()
        {
            _hud = FindAnyObjectByType<Systems_FightHud>();
            if (_manager != null)
            {
                if (_manager.wrestlerA != null)
                    _bodyA = _manager.wrestlerA.GetComponent<Agent_BipedBody>();
                if (_manager.wrestlerB != null)
                    _bodyB = _manager.wrestlerB.GetComponent<Agent_BipedBody>();
            }
            BuildMeter();
        }

        private void ResetSupport()
        {
            Support01 = 0f;
            ClearBoost();
        }

        private void ClearBoost()
        {
            if (_bodyA != null) _bodyA.adrenaline = 1f;
            if (_bodyB != null) _bodyB.adrenaline = 1f;
        }

        /// Physics-clock, because it writes a torque multiplier that `ApplyMotor`
        /// consumes on the same clock. Running this in `Update` would make the boost
        /// frame-rate dependent, which is exactly the trap `.claude/rules/unity-rules.md`
        /// calls out for anything that ends up as a force.
        private void FixedUpdate()
        {
            if (_manager == null || !_manager.RoundLive)
            {
                if (Support01 != 0f) ResetSupport();
                return;
            }

            int underdog = PickUnderdog();
            if (underdog == 0)
            {
                Decay();
            }
            else
            {
                bool wantA = underdog < 0;
                // Switching sides resets rather than reverses: the crowd does not
                // carry its volume across to the other fighter.
                if (wantA != BackedIsA && Support01 > 0f)
                {
                    Decay();
                    if (Support01 <= 0f) BackedIsA = wantA;
                }
                else
                {
                    BackedIsA = wantA;
                    Build();
                }
            }

            ApplyBoost();
            UpdateMeter();
        }

        /// -1 = A is the underdog, +1 = B, 0 = too even to call.
        private int PickUnderdog()
        {
            if (_manager.ScoreA != _manager.ScoreB)
            {
                return _manager.ScoreA < _manager.ScoreB ? -1 : 1;
            }
            if (_hud == null) return 0;

            float gap = _hud.DominanceA - _hud.DominanceB;
            if (Mathf.Abs(gap) < EVEN_BAND) return 0;
            return gap < 0f ? -1 : 1;
        }

        private void Build()
        {
            float rate = 1f / BUILD_SECONDS;

            // Louder when the backed fighter is near the edge — the drama term.
            Agent_Biped backed = BackedIsA ? _manager.wrestlerA : _manager.wrestlerB;
            if (backed != null && _manager.ringHalfWidth > 0.01f)
            {
                float edge01 = Mathf.Clamp01(
                    Mathf.Abs(backed.TorsoX - _manager.transform.position.x)
                    / _manager.ringHalfWidth);
                rate *= Mathf.Lerp(1f, RIM_URGENCY, edge01);
            }

            Support01 = Mathf.Clamp01(Support01 + rate * Time.fixedDeltaTime);
        }

        private void Decay()
        {
            Support01 = Mathf.Clamp01(
                Support01 - (DECAY_MULTIPLIER / BUILD_SECONDS) * Time.fixedDeltaTime);
        }

        private void ApplyBoost()
        {
            float boost = 1f + MAX_BOOST * Support01;
            if (_bodyA != null) _bodyA.adrenaline = BackedIsA ? boost : 1f;
            if (_bodyB != null) _bodyB.adrenaline = BackedIsA ? 1f : boost;
        }

        // ---- meter -----------------------------------------------------------

        /// A slim bar in the dock, under the existing live strip. Deliberately not
        /// in the centre band: that is where the round banner and the kimarite call
        /// appear, and three things competing for one slot is how taps got swallowed
        /// before `Systems_HudRoot` existed.
        private void BuildMeter()
        {
            var hud = FindAnyObjectByType<Systems_HudRoot>();
            if (hud == null || hud.Dock == null) return;

            VisualElement row = Systems_UiKit.Column(Align.Stretch);
            row.style.marginTop = Systems_UiKit.SPACE_1;
            row.NoPickTree();

            _meterLabel = Systems_UiKit.Caption("", Systems_UiKit.FONT_MICRO,
                                                Systems_UiKit.TextLow);
            _meterLabel.style.unityTextAlign = TextAnchor.MiddleCenter;

            _meterTrack = new VisualElement();
            _meterTrack.style.height = 4;
            _meterTrack.style.backgroundColor = Systems_UiKit.Track;
            _meterTrack.Round(Systems_UiKit.RADIUS_SM);

            _meterFill = new VisualElement();
            _meterFill.style.height = 4;
            _meterFill.style.width = Length.Percent(0f);
            _meterFill.style.backgroundColor = Systems_UiKit.Gold;
            _meterFill.Round(Systems_UiKit.RADIUS_SM);
            _meterTrack.Add(_meterFill);

            row.Add(_meterLabel);
            row.Add(_meterTrack);
            hud.Dock.Add(row);
        }

        /// Writes text and style on retained elements — never rebuilds the
        /// hierarchy, which is UI Toolkit's equivalent of a full Canvas rebuild.
        private void UpdateMeter()
        {
            if (_meterFill == null || _meterLabel == null) return;

            _meterFill.style.width = Length.Percent(Support01 * 100f);

            if (Support01 <= 0.02f)
            {
                _meterLabel.text = "";
                return;
            }
            string who = BackedIsA ? _manager.nameA : _manager.nameB;
            _meterLabel.text = Support01 >= 0.99f
                ? $"CROWD ROARING FOR {who}"
                : $"CROWD BEHIND {who}";
            _meterLabel.style.color = Support01 >= 0.99f
                ? Systems_UiKit.Gold : Systems_UiKit.TextLow;
        }
    }
}
