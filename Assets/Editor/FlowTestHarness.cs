using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoSumo.EditorTools
{
    /// Drives the ARENA state machine across its transition edges — the checks
    /// that fall between the two harnesses that already exist. MatchTestHarness
    /// chains matches inside one scene but asserts nothing about state;
    /// BracketTestHarness crosses the scene boundary but only ever walks the
    /// happy path. This one pauses and resumes in every phase, spams the pause
    /// toggle, races the reset, exercises the fake device cutout, audits the HUD
    /// tree in the states the python tool cannot reach, and counts event
    /// subscribers across a full reset cycle.
    ///
    /// Enter Play mode on SCN_SUMO (a standalone exhibition), then
    ///     PoSumo -> Test -> Run Flow Harness
    /// Exactly like MatchTestHarness — from a BRACKET session the reporter steals
    /// the scene after the match and every stage after the result card is
    /// unobservable. The match is shortened to a single decided round
    /// (`pointsToWin = 1` is public and read live by EndRound) so a whole run
    /// lands in a few minutes.
    ///
    /// Every stage drives REAL entry points (TogglePause, ResetMatch) rather than
    /// reaching into private state, so what is tested is what a player's taps
    /// hit.
    public static class FlowTestHarness
    {
        private const string ARENA_SCENE = "SCN_SUMO";
        /// A decided round plus walk-in plus ceremony measured 15-70 s; three of
        /// these (two matches) plus slack.
        private const float MATCH_WAIT_SECONDS = 300f;
        private const float SCORING_WAIT_SECONDS = 150f;
        /// The result card reveal is scheduled at most a couple of seconds out.
        private const float CARD_WAIT_SECONDS = 12f;

        private enum Stage
        {
            Setup,
            Notch,
            CeremonyPause,
            FightPause,
            Spam,
            AwaitMatchEnd,
            ResultCard,
            ResetRace,
            SecondMatch,
            Done
        }

        private static Systems_GameMatchManager _manager;
        private static Systems_HudRoot _hud;
        private static Stage _stage;
        private static float _stageStartedAt;
        private static float _startedAt;
        private static readonly StringBuilder Problems = new StringBuilder();
        private static readonly StringBuilder Notes = new StringBuilder();
        private static int _checks;
        private static bool _matchEndedSeen;
        private static int _matchEndedCount;
        private static int _matchResetCount;
        private static bool _skipFightStages;

        // ---- Leak-probe baselines -------------------------------------------
        private static readonly string[] MANAGER_EVENTS =
            { "RoundStarted", "RoundEnded", "MatchEnded", "MatchReset" };
        private static readonly string[] DAMAGE_EVENTS = { "Knockout", "Dismembered", "Gibbed" };
        private static readonly int[] ManagerBaseline = new int[MANAGER_EVENTS.Length];
        private static readonly int[] DamageBaseline = new int[DAMAGE_EVENTS.Length];

        [MenuItem("PoSumo/Test/Run Flow Harness")]
        public static void Run()
        {
            if (!EditorApplication.isPlaying)
            {
                Debug.LogError("FLOW HARNESS: enter Play mode first.");
                return;
            }
            if (SceneManager.GetActiveScene().name != ARENA_SCENE)
            {
                Debug.LogError("FLOW HARNESS: enter Play mode on " + ARENA_SCENE +
                               " (standalone exhibition). From SCN_TOURNAMENT the tournament " +
                               "reporter steals the scene after the match and every stage after " +
                               "the result card is unobservable — that loop is what " +
                               "BracketTestHarness covers.");
                return;
            }
            if (Systems_TournamentState.Active)
            {
                Debug.LogError("FLOW HARNESS: a bracket is active — this must be a standalone " +
                               "exhibition. Start Play mode from SCN_SUMO directly.");
                return;
            }
            _manager = Object.FindAnyObjectByType<Systems_GameMatchManager>();
            if (_manager == null)
            {
                Debug.LogError("FLOW HARNESS: no match manager in the open scene.");
                return;
            }

            // Always detach first: a previous Run stopped by exiting Play mode left
            // its update hook and its event handlers attached, and a second Run
            // would then double-count every reset and match end.
            EditorApplication.update -= Tick;
            _manager.MatchEnded -= OnMatchEnded;
            _manager.MatchReset -= OnMatchReset;

            _hud = Object.FindAnyObjectByType<Systems_HudRoot>();
            _matchEndedSeen = false;
            _matchEndedCount = 0;
            _matchResetCount = 0;
            _skipFightStages = false;
            _checks = 0;
            Problems.Clear();
            Notes.Clear();

            _manager.MatchEnded += OnMatchEnded;
            _manager.MatchReset += OnMatchReset;

            ConsoleSentinel.Start();
            _stage = Stage.Setup;
            _stageStartedAt = Time.realtimeSinceStartup;
            _startedAt = _stageStartedAt;
            EditorApplication.update += Tick;
            Debug.Log("FLOW HARNESS: running — pause matrix, back-key routing, reset race, " +
                      "notch sim, HUD audits, leak probe. Match shortened to one round.");
        }

        private static void OnMatchEnded(Agent_Biped winner)
        {
            _matchEndedSeen = true;
            _matchEndedCount++;
        }

        private static void OnMatchReset()
        {
            _matchResetCount++;
        }

        private static void Tick()
        {
            if (!EditorApplication.isPlaying)
            {
                // Play mode exited mid-run: report what we have rather than hang.
                EditorApplication.update -= Tick;
                Debug.LogError("FLOW HARNESS RESULT: FAIL — Play mode exited during " + _stage + ".\n" +
                               Problems + ConsoleSentinel.Tally());
                ConsoleSentinel.Stop();
                return;
            }

            if (Time.realtimeSinceStartup - _startedAt > 700f)
            {
                Fail("overall timeout in stage " + _stage);
                return;
            }

            // A super-fast round can decide the match under any wait stage — jump
            // straight to the result-card checks instead of timing out.
            if (_matchEndedSeen &&
                _stage != Stage.AwaitMatchEnd && _stage != Stage.ResultCard &&
                _stage != Stage.ResetRace && _stage != Stage.SecondMatch &&
                _stage != Stage.Done && _stage != Stage.Setup && _stage != Stage.Notch)
            {
                Notes.Append("  - match decided before stage ").Append(_stage)
                     .Append(" ran; fight-pause checks skipped for this round.\n");
                Enter(Stage.ResultCard);
            }

            switch (_stage)
            {
                case Stage.Setup:
                    // The manager's companions subscribe in Start/OnEnable; wait out
                    // a beat so the leak baselines see the steady state, not the
                    // spawn frame.
                    if (Elapsed(_stageStartedAt) < 2f)
                    {
                        return;
                    }
                    CaptureBaselines();
                    // The first decided round ends the match — pointsToWin is read
                    // live by EndRound. Public field, set through the front door.
                    _manager.pointsToWin = 1;
                    Check(Mathf.Approximately(Time.timeScale, 1f),
                          "match opens at timeScale 1");
                    Enter(Stage.Notch);
                    return;

                case Stage.Notch:
                    if (_hud == null)
                    {
                        _hud = Object.FindAnyObjectByType<Systems_HudRoot>();
                    }
                    if (_hud == null)
                    {
                        Problem("no Systems_HudRoot in the scene — the HUD never built.");
                        Enter(Stage.CeremonyPause);
                        return;
                    }
                    if (Elapsed(_stageStartedAt) < 0.1f)
                    {
                        // First tick: arm the fake cutout and wait for a layout pass.
                        Systems_SafeArea.SafeAreaOverride =
                            new Rect(0f, 48f, Screen.width, Screen.height - 96f);
                        return;
                    }
                    if (Elapsed(_stageStartedAt) < 0.8f)
                    {
                        return;
                    }
                    Check(_hud.ContentLayer.resolvedStyle.paddingTop > 0.5f,
                          "fake 48px cutout insets the HUD content layer (top=" +
                          _hud.ContentLayer.resolvedStyle.paddingTop.ToString("F1") + ")");
                    Check(_hud.ContentLayer.resolvedStyle.paddingBottom > 0.5f,
                          "fake gesture bar insets the content layer (bottom=" +
                          _hud.ContentLayer.resolvedStyle.paddingBottom.ToString("F1") + ")");
                    Systems_SafeArea.SafeAreaOverride = default;
                    Enter(Stage.CeremonyPause);
                    return;

                case Stage.CeremonyPause:
                    // ~3 s in: the opening ceremony (Intro/Grace/WalkIn). Pause must
                    // hold the screen, resume must hand it back, and timeScale must
                    // land exactly where it started both times.
                    if (Elapsed(_stageStartedAt) < 1f)
                    {
                        return;
                    }
                    PauseCycle("ceremony");
                    Enter(Stage.FightPause);
                    return;

                case Stage.FightPause:
                    if (!_skipFightStages && !_manager.ScoringLive)
                    {
                        if (Elapsed(_stageStartedAt) > SCORING_WAIT_SECONDS)
                        {
                            Problem("no live scoring within " + SCORING_WAIT_SECONDS +
                                    "s — the round never opened (walk-in stalled past every backstop?).");
                            Enter(Stage.AwaitMatchEnd);
                            return;
                        }
                        return;
                    }
                    if (!_skipFightStages)
                    {
                        PauseCycle("fight");
                    }
                    Enter(Stage.Spam);
                    return;

                case Stage.Spam:
                    // Triple-tap the pause toggle in one frame: an odd number of
                    // flips must leave the game PAUSED, not in an undefined state,
                    // and one more tap must resume cleanly. This is the "jittery
                    // double-tap" class of fault at the state level — the buttons
                    // themselves are debounced in the kit.
                    _manager.TogglePause();
                    _manager.TogglePause();
                    _manager.TogglePause();
                    Check(_manager.IsPaused, "3x pause toggle leaves the game paused");
                    _manager.TogglePause();
                    Check(!_manager.IsPaused, "4x pause toggle resumes");
                    Check(Mathf.Approximately(Time.timeScale, 1f),
                          "timeScale is 1 after the pause spam");
                    Enter(Stage.AwaitMatchEnd);
                    return;

                case Stage.AwaitMatchEnd:
                    if (Elapsed(_stageStartedAt) > MATCH_WAIT_SECONDS)
                    {
                        Fail("no match decision within " + MATCH_WAIT_SECONDS + "s " +
                             "(timeScale=" + Time.timeScale.ToString("F2") + ")");
                        return;
                    }
                    if (_matchEndedSeen)
                    {
                        Enter(Stage.ResultCard);
                    }
                    return;

                case Stage.ResultCard:
                    if (_hud == null || !_hud.ModalShown)
                    {
                        if (Elapsed(_stageStartedAt) > CARD_WAIT_SECONDS)
                        {
                            Problem("result card never appeared within " + CARD_WAIT_SECONDS +
                                    "s of MatchEnded.");
                            Enter(Stage.ResetRace);
                            return;
                        }
                        return;
                    }
                    Check(true, "result card is on screen after MatchEnded");
                    Audit("result card");

                    // The phase guard: pause must refuse at MatchOver — same
                    // timeScale, no second modal fighting the card.
                    float timeScaleBefore = Time.timeScale;
                    _manager.TogglePause();
                    Check(!_manager.IsPaused, "pause refuses during MatchOver");
                    Check(Mathf.Approximately(Time.timeScale, timeScaleBefore),
                          "timeScale untouched by a refused pause");

                    // Back-key routing at MatchOver: dismiss is NOT the answer —
                    // HandleBackKey routes to TogglePause, which the phase guard
                    // refuses. Same assertion through the real entry point.
                    Enter(Stage.ResetRace);
                    return;

                case Stage.ResetRace:
                    // The exact double-fire that shipped: two calls back-to-back
                    // (press-down tap + button release). Exactly one MatchReset may
                    // reach the companions.
                    int resetsBefore = _matchResetCount;
                    _manager.ResetMatch();
                    _manager.ResetMatch();
                    Check(_matchResetCount - resetsBefore == 1,
                          "double ResetMatch fires MatchReset exactly once (saw " +
                          (_matchResetCount - resetsBefore) + ")");
                    Check(_hud == null || !_hud.ModalShown,
                          "result card is cleared by the reset");
                    Audit("after reset");
                    Enter(Stage.SecondMatch);
                    return;

                case Stage.SecondMatch:
                    // Let the reset match play out so the leak probe sees a full
                    // cycle (opening ceremony, round, decision) before comparing.
                    if (Elapsed(_stageStartedAt) > MATCH_WAIT_SECONDS && !_matchEndedSeen)
                    {
                        Problem("the reset match never decided within " + MATCH_WAIT_SECONDS + "s.");
                        FinishLeakCheck();
                        return;
                    }
                    if (_matchEndedCount >= 2)
                    {
                        FinishLeakCheck();
                    }
                    return;

                case Stage.Done:
                    return;
            }
        }

        /// One pause/resume cycle with the assertions that make it a test.
        private static void PauseCycle(string label)
        {
            bool wasPaused = _manager.IsPaused;
            float timeScaleBefore = Time.timeScale;

            _manager.TogglePause();
            Check(_manager.IsPaused, label + ": pause opens");
            Check(Mathf.Approximately(Time.timeScale, 0f), label + ": pause sets timeScale 0");
            Check(_hud == null || _hud.ModalShown, label + ": pause card is up as the modal");
            Audit(label + " pause");

            _manager.TogglePause();
            Check(!_manager.IsPaused, label + ": resume closes");
            Check(Mathf.Approximately(Time.timeScale, 1f), label + ": resume restores timeScale");
            Check(_hud == null || !_hud.ModalShown, label + ": pause card dismissed");
            Check(Mathf.Abs(timeScaleBefore - 1f) < 0.01f || Mathf.Approximately(timeScaleBefore, 1f),
                  label + ": entered the cycle at timeScale 1");
            if (wasPaused)
            {
                Problem(label + ": entered the pause cycle already paused — previous stage left state behind.");
            }
        }

        private static void CaptureBaselines()
        {
            for (int index = 0; index < MANAGER_EVENTS.Length; index++)
            {
                ManagerBaseline[index] = EventLeakProbe.InstanceSubscribers(_manager, MANAGER_EVENTS[index]);
            }
            for (int index = 0; index < DAMAGE_EVENTS.Length; index++)
            {
                DamageBaseline[index] = EventLeakProbe.StaticSubscribers(
                    typeof(Systems_BodyDamage), DAMAGE_EVENTS[index]);
            }
            var sb = new StringBuilder("FLOW HARNESS: baselines — manager ");
            for (int index = 0; index < MANAGER_EVENTS.Length; index++)
            {
                sb.Append(MANAGER_EVENTS[index]).Append('=').Append(ManagerBaseline[index]).Append(' ');
            }
            sb.Append("| damage statics ");
            for (int index = 0; index < DAMAGE_EVENTS.Length; index++)
            {
                sb.Append(DAMAGE_EVENTS[index]).Append('=').Append(DamageBaseline[index]).Append(' ');
            }
            Debug.Log(sb.ToString());
        }

        private static void FinishLeakCheck()
        {
            for (int index = 0; index < MANAGER_EVENTS.Length; index++)
            {
                int now = EventLeakProbe.InstanceSubscribers(_manager, MANAGER_EVENTS[index]);
                Check(now == ManagerBaseline[index],
                      MANAGER_EVENTS[index] + " subscriber count survived a full reset cycle (" +
                      ManagerBaseline[index] + " -> " + now + ")");
            }
            for (int index = 0; index < DAMAGE_EVENTS.Length; index++)
            {
                int now = EventLeakProbe.StaticSubscribers(typeof(Systems_BodyDamage), DAMAGE_EVENTS[index]);
                Check(now == DamageBaseline[index],
                      "Systems_BodyDamage." + DAMAGE_EVENTS[index] +
                      " static subscriber count survived a full reset cycle (" +
                      DamageBaseline[index] + " -> " + now + ")");
            }
            Finish();
        }

        private static void Audit(string when)
        {
            string report = HudOverflowAudit.Run();
            int flagged = CountOverflowLines(report);
            Check(flagged == 0, "HUD layout at " + when + " has no overflowing elements");
            if (flagged > 0)
            {
                Problems.Append("  HUD audit at ").Append(when).Append(":\n").Append(report);
            }
        }

        private static int CountOverflowLines(string report)
        {
            int count = 0;
            int index = 0;
            while ((index = report.IndexOf("OVERFLOW-", index)) >= 0)
            {
                count++;
                index += 9;
            }
            return count;
        }

        private static void Check(bool condition, string what)
        {
            _checks++;
            if (!condition)
            {
                Problems.Append("  - ").Append(what).Append('\n');
            }
        }

        private static void Problem(string what)
        {
            _checks++;
            Problems.Append("  - ").Append(what).Append('\n');
        }

        private static float Elapsed(float from) => Time.realtimeSinceStartup - from;

        private static void Enter(Stage stage)
        {
            _stage = stage;
            _stageStartedAt = Time.realtimeSinceStartup;
            if (stage == Stage.SecondMatch)
            {
                // The reset match just started; its decision is the NEXT MatchEnded.
                _matchEndedSeen = false;
            }
        }

        private static void Fail(string why)
        {
            EditorApplication.update -= Tick;
            _manager.MatchEnded -= OnMatchEnded;
            _manager.MatchReset -= OnMatchReset;
            ConsoleSentinel.Stop();
            Debug.LogError($"FLOW HARNESS RESULT: FAIL — {why}\n{Problems}{Notes}{ConsoleSentinel.Tally()}");
        }

        private static void Finish()
        {
            EditorApplication.update -= Tick;
            _manager.MatchEnded -= OnMatchEnded;
            _manager.MatchReset -= OnMatchReset;
            ConsoleSentinel.Stop();

            bool ok = Problems.Length == 0 && ConsoleSentinel.Errors == 0;
            string verdict = ok ? "PASS" : "FAIL";
            if (Problems.Length == 0 && ConsoleSentinel.Errors > 0)
            {
                Problems.Append("  - the console carried errors during the run (see samples below).\n");
            }
            Debug.Log($"FLOW HARNESS RESULT: {verdict} — {_checks} checks over a full " +
                      $"pause/resume/reset cycle in {Time.realtimeSinceStartup - _startedAt:F0}s\n" +
                      Problems + Notes + ConsoleSentinel.Tally());
        }
    }
}
