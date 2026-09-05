using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoSumo.EditorTools
{
    /// Plays a whole tournament unattended and reports whether the LOOP held —
    /// bracket -> arena -> win/loss -> bracket -> ... -> champion -> reset.
    ///
    /// **Why this exists, when MatchTestHarness already exists.** That one chains
    /// matches inside ONE arena scene by calling `ResetMatch`. It therefore never
    /// crosses a scene boundary, and every part of this game that is hard — the
    /// static bracket surviving `LoadScene`, `Systems_TournamentReporter` handing
    /// the winner back, `Systems_MatchRoster` re-seeding the fighters on the far
    /// side, the career/banzuke write, `Time.timeScale` not leaking out of a
    /// slow-motion finish into the bracket screen — lives exactly at that
    /// boundary and had no automated coverage at all.
    /// `Systems_TournamentBracket.PressAction` was added as the pointer-free hook
    /// for precisely this and, until now, nothing called it.
    ///
    /// Enter Play mode on SCN_TOURNAMENT, then call Run(). It presses START once
    /// (auto-play carries the rest) and watches the static state across every
    /// scene load from `EditorApplication.update`, which is Editor-side and so is
    /// not disturbed by the loads it is watching.
    ///
    ///     PoSumo -> Test -> Run Bracket Harness
    ///     BracketTestHarness.Run();
    public static class BracketTestHarness
    {
        /// Wall-clock ceiling for a whole 7-match bracket. Measured bouts run
        /// 60-110 s (rounds average ~18 s, best-of-three, plus ceremony and the
        /// return), so seven of them plus slack is comfortably inside this. It
        /// exists so a hung bout reports a FAILURE rather than leaving the Editor
        /// spinning with no verdict.
        private const float TIMEOUT_SECONDS = 1500f;
        /// A single bout that outruns this is stalled. The longest genuine bout
        /// measured was ~110 s; the historical stall this guards against sat at
        /// 170 s and climbing on a fully shrunk mat.
        private const float MATCH_TIMEOUT_SECONDS = 240f;
        /// How long the final's result card plus the return to the bracket is
        /// allowed to take. `Systems_TournamentReporter` waits `resultPause` (2.5)
        /// plus the match's announce delay, and a knockout finish takes the longer
        /// of the two, so this has room for both.
        private const float RETURN_TIMEOUT_SECONDS = 20f;
        /// Must match Systems_TournamentBracket.ARENA_SCENE's counterpart — the
        /// scene the reporter returns to.
        private const string BRACKET_SCENE = "SCN_TOURNAMENT";

        private static bool _running;
        private static float _startedAt;
        private static float _matchStartedAt;
        private static float _completeAt = -1f;
        private static int _lastMatch;
        private static int _matchesObserved;
        private static readonly StringBuilder Log = new StringBuilder();

        [MenuItem("PoSumo/Test/Run Bracket Harness")]
        public static void Run()
        {
            if (!EditorApplication.isPlaying)
            {
                Debug.LogError("BRACKET HARNESS: enter Play mode on SCN_TOURNAMENT first.");
                return;
            }

            var bracket = Object.FindAnyObjectByType<Systems_TournamentBracket>();
            if (bracket == null)
            {
                Debug.LogError("BRACKET HARNESS: no Systems_TournamentBracket in the open scene. " +
                               "Start from SCN_TOURNAMENT — SCN_SUMO is a standalone exhibition " +
                               "and has no bracket at all.");
                return;
            }

            // Always detach first: a Run() that was stopped by exiting Play mode
            // left its update hook attached, and a second Run would then tick twice.
            EditorApplication.update -= Tick;

            Log.Clear();
            _running = true;
            _matchesObserved = 0;
            _startedAt = Time.realtimeSinceStartup;
            _matchStartedAt = _startedAt;
            _completeAt = -1f;
            _lastMatch = Systems_TournamentState.CurrentMatch;

            if (Systems_TournamentState.IsComplete)
            {
                // A finished bracket's action button is NEW TOURNAMENT, so one
                // press would only reseed and the harness would sit watching a
                // bracket that never started. Clear it, then start.
                bracket.PressAction();
            }
            if (!Systems_TournamentState.SeedsReady())
            {
                Debug.LogError("BRACKET HARNESS: seeds are not ready — every slot needs a fighter.");
                _running = false;
                return;
            }

            Debug.Log("BRACKET HARNESS: starting a full bracket from " +
                      $"match {Systems_TournamentState.CurrentMatch}.");
            bracket.PressAction();
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            if (!_running || !EditorApplication.isPlaying)
            {
                if (_running)
                {
                    Fail("Play mode exited before the bracket finished.");
                }
                return;
            }

            float now = Time.realtimeSinceStartup;

            int match = Systems_TournamentState.CurrentMatch;
            if (match != _lastMatch)
            {
                Agent_CharacterDefinition winner = Systems_TournamentState.GetWinner(_lastMatch);
                _matchesObserved++;
                string line = $"  match {_lastMatch} ({Systems_TournamentState.RoundName(_lastMatch)}) " +
                              $"-> {(winner != null ? winner.behaviorName : "NULL")} " +
                              $"in {now - _matchStartedAt:F0}s";
                Log.AppendLine(line);
                Debug.Log("BRACKET HARNESS:" + line);
                _lastMatch = match;
                _matchStartedAt = now;
            }

            // COMPLETE IS NOT THE SAME MOMENT AS BACK-ON-THE-BRACKET, and the first
            // version of this harness conflated them and reported a false FAIL.
            //
            // `IsComplete` flips inside `ReportWinner`, which the reporter calls from
            // its `MatchEnded` handler — while the game is still in SCN_SUMO, with
            // the loser mid-flop and Systems_MatchPresentation's finish slow motion
            // running. Sampling `Time.timeScale` there measured 0.35 (slowMoScale)
            // and blamed the reporter for a leak that does not exist: measured
            // immediately afterwards on the bracket screen it is 1.000.
            //
            // The assertion is right and worth keeping — a knockout finish returning
            // mid-slow-motion IS how timeScale leaks across a scene load, and that is
            // a real bug this should catch. It just has to be asked after the load,
            // not before it. So: wait for the bracket scene to actually be active.
            if (Systems_TournamentState.IsComplete)
            {
                if (SceneManager.GetActiveScene().name != BRACKET_SCENE)
                {
                    if (_completeAt < 0f) _completeAt = now;
                    if (now - _completeAt > RETURN_TIMEOUT_SECONDS)
                    {
                        Fail($"the final was decided {RETURN_TIMEOUT_SECONDS:F0}s ago and the " +
                             $"game is still in " +
                             $"'{SceneManager.GetActiveScene().name}' — the return to the " +
                             "bracket never happened.");
                    }
                    return;
                }
                Finish();
                return;
            }

            if (now - _matchStartedAt > MATCH_TIMEOUT_SECONDS)
            {
                Fail($"match {match} ran past {MATCH_TIMEOUT_SECONDS:F0}s with no result — " +
                     $"scene={SceneManager.GetActiveScene().name}, " +
                     $"timeScale={Time.timeScale:F2}.");
                return;
            }
            if (now - _startedAt > TIMEOUT_SECONDS)
            {
                Fail($"the bracket ran past {TIMEOUT_SECONDS:F0}s with " +
                     $"{_matchesObserved} matches decided.");
            }
        }

        private static void Finish()
        {
            EditorApplication.update -= Tick;
            _running = false;

            // The last transition is into the FINAL's own slot, so the champion's
            // match is decided without CurrentMatch moving past it again — count it
            // here rather than in the transition above.
            if (_matchesObserved < 7) _matchesObserved = 7;

            Agent_CharacterDefinition champion = Systems_TournamentState.Champion;
            var problems = new StringBuilder();

            if (champion == null)
            {
                problems.AppendLine("  - IsComplete is true but Champion is null.");
            }
            if (!Mathf.Approximately(Time.timeScale, 1f))
            {
                // The exact leak Systems_TournamentReporter used to allow: a
                // knockout finish returns to the bracket mid slow-motion and
                // timeScale is global, so the bracket screen inherits it.
                problems.AppendLine($"  - Time.timeScale returned to the bracket at " +
                                    $"{Time.timeScale:F2}, not 1.");
            }
            if (Systems_TournamentState.Active)
            {
                problems.AppendLine("  - Systems_TournamentState.Active is still true on a " +
                                    "complete bracket.");
            }

            string careerLine = "  career: (no champion to look up)";
            if (champion != null)
            {
                Systems_CareerStats.Record record = Systems_CareerStats.Get(champion.behaviorName);
                careerLine = $"  career: {champion.behaviorName} " +
                             $"{record.matchWins}W-{record.matchLosses}L elo {record.elo:F0} " +
                             $"titles {record.titles} rank {Systems_CareerLadder.NameFor(record)}";
                if (record.titles < 1)
                {
                    problems.AppendLine("  - the champion was awarded no title, so " +
                                        "Systems_CareerRecorder never saw the final.");
                }
                if (record.matchWins < 1)
                {
                    problems.AppendLine("  - the champion banked no match wins; a mirror final " +
                                        "(winner == loser) is guarded in RecordMatch and " +
                                        "silently records nothing.");
                }
            }

            bool ok = problems.Length == 0;
            Debug.Log($"BRACKET HARNESS RESULT: {(ok ? "PASS" : "FAIL")} — " +
                      $"champion {(champion != null ? champion.behaviorName : "NONE")} " +
                      $"over {_matchesObserved} matches in {Time.realtimeSinceStartup - _startedAt:F0}s\n" +
                      Log + careerLine +
                      (ok ? string.Empty : "\nPROBLEMS:\n" + problems));
        }

        private static void Fail(string why)
        {
            EditorApplication.update -= Tick;
            _running = false;
            Debug.LogError($"BRACKET HARNESS RESULT: FAIL — {why}\n{Log}");
        }
    }
}
