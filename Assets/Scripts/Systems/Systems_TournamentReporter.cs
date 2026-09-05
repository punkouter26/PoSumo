using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoSumo
{
    /// Present in the arena only while a tournament match is being played.
    /// Forces best-of-three (first to 2 round wins takes the bracket), then
    /// records the winner into the bracket and returns to the bracket scene.
    ///
    /// Spawned at runtime by Systems_MatchRoster when Systems_TournamentState.Active, so
    /// SCN_SUMO stays usable as a standalone exhibition scene with nothing to
    /// disable.
    public sealed class Systems_TournamentReporter : MonoBehaviour
    {
        [Tooltip("Bracket scene to return to when the match is decided.")]
        public string bracketScene = "SCN_TOURNAMENT";
        [Tooltip("Seconds to hold on the result before returning to the bracket.")]
        public float resultPause = 2.5f;
        [Tooltip("Fallback when the match manager has no GameTuning asset assigned. 2 = best of three.")]
        public int roundsToWinMatchFallback = 2;

        private Systems_GameMatchManager _manager;
        private bool _returning;
        /// Realtime stamp the bracket is loaded at, or negative when idle.
        ///
        /// This was `Invoke(nameof(ReturnToBracket), ...)`, which counts in SCALED
        /// time. Everything it is waiting on counts in REALTIME: the result card is
        /// raised by the UI Toolkit scheduler, and the slow motion in front of it
        /// ends on `Time.realtimeSinceStartup`. So a knockout finish — the one case
        /// with a deep, long `timeScale` drop — stretched this wait well past the
        /// `resultPause` it was asked for, and the card sat there. An accumulator in
        /// Update is also the project's rule for deferred work; there are no
        /// coroutines here and Invoke was the last thing behaving like one.
        private float _returnAtReal = -1f;

        private void Start()
        {
            _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            if (_manager == null) return;

            // The manager reads tournamentPointsToWin from GameTuning itself, so
            // nothing is set here — assigning pointsToWin from this Start raced
            // the manager's own Start and lost. Only cover the case where no
            // tuning asset is assigned at all.
            if (_manager.tuning == null)
            {
                _manager.pointsToWin = roundsToWinMatchFallback;
            }
            // MarkBracketBout is NOT called here any more. This bout's result goes
            // to the bracket and the scene change is scheduled the moment it is
            // decided, so the result card must not offer a rematch it cannot
            // honour — but this Start is not ordered against the manager's, and
            // the flag was only reliably set by the time a match had been decided.
            // Systems_MatchRoster, which spawns this component at execution order
            // -500, sets it before any Awake runs.
            _manager.MatchEnded += OnMatchEnded;
        }

        private void Update()
        {
            if (_returnAtReal < 0f || Time.realtimeSinceStartup < _returnAtReal) return;
            _returnAtReal = -1f;
            ReturnToBracket();
        }

        private void OnDisable()
        {
            if (_manager != null) _manager.MatchEnded -= OnMatchEnded;
        }

        private void OnMatchEnded(Agent_Biped winner)
        {
            if (_returning || winner == null) return;
            _returning = true;

            Agent_CharacterDefinition character = winner.character;
            // Read BEFORE reporting: ReportWinner advances CurrentMatch, so
            // logging it afterwards named the match that has not been played yet
            // and every line was off by one.
            int decidedMatch = Systems_TournamentState.CurrentMatch;
            Systems_TournamentState.ReportWinner(character);
            Systems_Log.Info($"[TOURNAMENT] match {decidedMatch} winner: " +
                      $"{(character != null ? character.behaviorName : "unknown")}");

            // The result card is itself delayed — while the loser flops on a
            // normal finish, or for longer on a knockout so the KO slow-motion
            // plays out first. Wait out whichever delay this match actually used,
            // or the scene changes before the card has been on screen its full
            // resultPause. Hardcoding only limpBeforeAnnounce meant a match ended
            // by the three-knockdown rule showed its result for ~1.5s of the
            // intended 2.5s.
            float announceDelay = _manager.EndedByKnockout
                ? _manager.knockoutAnnounceSeconds
                : _manager.limpBeforeAnnounce;
            _returnAtReal = Time.realtimeSinceStartup + resultPause + announceDelay;
        }

        private void ReturnToBracket()
        {
            // Time.timeScale is GLOBAL and survives a scene load. A knockout finish
            // leaves it at koSlowMoScale until Systems_MatchPresentation's realtime
            // deadline expires, and its OnDisable only restores what it applied —
            // so a return that lands inside that window carried ~0.25x speed into
            // SCN_TOURNAMENT. The other two exits from a decided bout
            // (Systems_BotLadderReporter.Return and the CONTINUE button's
            // ContinueToBracket) both clear it; this one did not, and was the odd
            // one of the three.
            Time.timeScale = 1f;
            SceneManager.LoadScene(bracketScene);
        }
    }
}
