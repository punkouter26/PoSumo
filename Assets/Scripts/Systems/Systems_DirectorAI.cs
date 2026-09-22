using UnityEngine;

namespace PoSumo
{
    /// The in-fight director: a shot-selection layer over the existing camera
    /// that cuts between framings the way a broadcast director would, driven by
    /// the tension reading and the geometry of the fight.
    ///
    /// DELIBERATELY NARROW. `Systems_MatchPresentation` owns every finish — the
    /// round-end punch, the KO slow-motion, the knockback close-ups, the salt —
    /// and the referee owns the ceremony beats. Both were tuned, both have been
    /// paid for (the realtime-vs-game-time deadline lesson lives in the
    /// countdown), and a director that fires into those moments fights them for
    /// `_focus` and wins by accident of call order. So this component is SILENT
    /// outside `RoundActive && ScoringLive`: no round end, no match end, no
    /// countdown. It only directs while the round is actually being wrestled.
    ///
    /// Four shots, each with its own cooldown so the cutting never reads as a
    /// strobe:
    ///   COMEBACK   — the side that was near-lost surges back: close-up on them.
    ///   BLOWOUT    — one side sat above ~90% for a while: a wide to show the
    ///                territory they own (and the shrinking mat they own it on).
    ///   SEPARATION — the pair drifts apart: a wide so the frame explains why
    ///                nothing is happening.
    ///   CLINCH     — locked together for seconds while the odds stay even: a
    ///                tight two-shot on the grinder the numbers say this is.
    ///
    /// All timings are REALTIME (`PunchIn`/`PullBackWide` are unscaled), all
    /// state is preallocated floats, and nothing here touches physics. Spawned
    /// behind `enableDirectorAI`; safe to disable.
    public sealed class Systems_DirectorAI : MonoBehaviour
    {
        private Systems_GameMatchManager _manager;
        private Systems_CameraFollow _camera;
        private Systems_TensionEngine _tension;
        private Systems_FightHud _hud;

        private Agent_Biped _wrestlerA, _wrestlerB;

        // ---- Comeback detector ----------------------------------------------
        // wp band that counts as "near-lost", and the band a recovery has to
        // climb back into before the shot fires.
        private const float LOST_WP = 0.22f;
        private const float RECOVERED_WP = 0.45f;
        private const float COMEBACK_MEMORY = 5f;
        private float _wasLostAtA = -99f, _wasLostAtB = -99f;

        // ---- Blowout detector ------------------------------------------------
        private const float BLOWOUT_WP = 0.90f;
        private const float BLOWOUT_HOLD = 3.5f;
        private float _blowoutFor;
        private bool _blowoutSideA;

        // ---- Separation / clinch ---------------------------------------------
        private const float SEPARATION_SHARE = 0.62f;
        private const float CLINCH_DISTANCE = 1.0f;
        private const float CLINCH_HOLD = 5f;
        private const float CLINCH_TENSION = 0.65f;
        private float _clinchFor;

        // ---- Shot discipline ---------------------------------------------------
        private const float SHOT_COOLDOWN = 5.5f;
        private const float COMEBACK_ORTHO = 2.0f;
        private const float CLINCH_ORTHO = 2.6f;
        private const float SHOT_BLEND = 14f;
        private float _coolUntil;

        private bool _subscribed;

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

        private void Start()
        {
            if (_manager == null) _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            _camera = FindAnyObjectByType<Systems_CameraFollow>();
            // The tension feed is optional — without it the director falls back
            // to raw dominance, so the camera survives its sibling being off.
            _tension = FindAnyObjectByType<Systems_TensionEngine>();
            _hud = FindAnyObjectByType<Systems_FightHud>();
            Subscribe();
            ResolveWrestlers();
        }

        private void Subscribe()
        {
            if (_manager == null || _subscribed) return;
            _manager.RoundStarted += OnRoundStarted;
            _manager.MatchReset += OnRoundStarted;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (_manager == null || !_subscribed) return;
            _manager.RoundStarted -= OnRoundStarted;
            _manager.MatchReset -= OnRoundStarted;
            _subscribed = false;
        }

        private void OnRoundStarted()
        {
            ResolveWrestlers();
            _wasLostAtA = _wasLostAtB = -99f;
            _blowoutFor = 0f;
            _clinchFor = 0f;
            _coolUntil = 0f;
        }

        private void ResolveWrestlers()
        {
            _wrestlerA = _manager != null ? _manager.wrestlerA : null;
            _wrestlerB = _manager != null ? _manager.wrestlerB : null;
        }

        private void Update()
        {
            if (_manager == null || _camera == null || _wrestlerA == null || _wrestlerB == null)
            {
                return;
            }

            // The narrow remit, enforced here once: only live, only scoring.
            if (!(_manager.RoundActive && _manager.ScoringLive)) return;
            if (Time.realtimeSinceStartup < _coolUntil) return;

            float wpA = _tension != null ? _tension.WinProbA : FallbackWpA();
            float dt = Time.unscaledDeltaTime;
            float ring = Mathf.Max(0.5f, _manager.CurrentRingHalfWidth);
            float separation = Mathf.Abs(_wrestlerA.TorsoX - _wrestlerB.TorsoX);

            // -- Comeback: a near-lost side back into contention.
            if (wpA <= LOST_WP) _wasLostAtA = Time.unscaledTime;
            if (wpA >= 1f - LOST_WP) _wasLostAtB = Time.unscaledTime;
            if (Time.unscaledTime - _wasLostAtB <= COMEBACK_MEMORY && wpA >= RECOVERED_WP && wpA <= RECOVERED_WP + 0.15f)
            {
                Punch(_wrestlerB);
                return;
            }
            if (Time.unscaledTime - _wasLostAtA <= COMEBACK_MEMORY && wpA <= 1f - RECOVERED_WP && wpA >= 1f - RECOVERED_WP - 0.15f)
            {
                Punch(_wrestlerA);
                return;
            }

            // -- Blowout: sustained near-certainty earns a territory wide.
            if (wpA >= BLOWOUT_WP || wpA <= 1f - BLOWOUT_WP)
            {
                _blowoutFor += dt;
                if (_blowoutFor >= BLOWOUT_HOLD)
                {
                    _blowoutFor = 0f;
                    _camera.PullBackWide(1.8f, SHOT_BLEND);
                    _coolUntil = Time.realtimeSinceStartup + SHOT_COOLDOWN * 2f;
                    return;
                }
            }
            else
            {
                _blowoutFor = 0f;
            }

            // -- Separation: explain the quiet.
            if (separation > SEPARATION_SHARE * ring)
            {
                _camera.PullBackWide(1.5f, SHOT_BLEND);
                _coolUntil = Time.realtimeSinceStartup + SHOT_COOLDOWN * 1.5f;
                return;
            }

            // -- Clinch: two fighters, no decision, real stakes.
            if (separation < CLINCH_DISTANCE && (_tension == null || _tension.Tension01 >= CLINCH_TENSION))
            {
                _clinchFor += dt;
                if (_clinchFor >= CLINCH_HOLD)
                {
                    _clinchFor = 0f;
                    Punch(wpA >= 0.5f ? _wrestlerA : _wrestlerB);
                    return;
                }
            }
            else
            {
                _clinchFor = 0f;
            }
        }

        /// Close-up on a fighter's head, one heartbeat long. Ortho 2.0 frames
        /// head and shoulders the way `knockbackOrtho` does — tighter than that
        /// and a moving head will not stay in a blend this fast.
        private void Punch(Agent_Biped fighter)
        {
            Transform focus = Systems_CameraFollow.FocusPoint(fighter);
            if (focus == null) return;
            _camera.PunchIn(focus, COMEBACK_ORTHO, 1.1f, SHOT_BLEND);
            _coolUntil = Time.realtimeSinceStartup + SHOT_COOLDOWN;
        }

        /// Dominance as a 0..1 probability stand-in when the tension engine is
        /// not running. Coarse, but it keeps the clinch and blowout detectors
        /// honest enough to fire on.
        private float FallbackWpA()
        {
            float dominance = _hud != null ? _hud.DominanceA : 50f;
            return Mathf.Clamp01(dominance / 100f);
        }
    }
}
