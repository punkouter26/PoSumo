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
            // This bout's result goes to the bracket and the scene change is
            // scheduled the moment it is decided, so the result card must not
            // offer a rematch it cannot honour.
            _manager.MarkBracketBout();
            _manager.MatchEnded += OnMatchEnded;
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
            Debug.Log($"[TOURNAMENT] match {decidedMatch} winner: " +
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
            Invoke(nameof(ReturnToBracket), resultPause + announceDelay);
        }

        private void ReturnToBracket()
        {
            SceneManager.LoadScene(bracketScene);
        }
    }
}
