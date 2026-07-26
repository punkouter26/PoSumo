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
    public class Systems_TournamentReporter : MonoBehaviour
    {
        [Tooltip("Bracket scene to return to when the match is decided.")]
        public string bracketScene = "SCN_TOURNAMENT";
        [Tooltip("Seconds to hold on the result before returning to the bracket.")]
        public float resultPause = 2.5f;
        [Tooltip("Fallback when the match manager has no GameTuning asset assigned. 2 = best of three.")]
        public int roundsToWinMatchFallback = 2;

        Systems_GameMatchManager _manager;
        bool _returning;

        void Start()
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
            _manager.MatchEnded += OnMatchEnded;
        }

        void OnDisable()
        {
            if (_manager != null) _manager.MatchEnded -= OnMatchEnded;
        }

        void OnMatchEnded(Agent_Biped winner)
        {
            if (_returning || winner == null) return;
            _returning = true;

            Agent_CharacterDefinition character = winner.character;
            Systems_TournamentState.ReportWinner(character);
            Debug.Log($"[TOURNAMENT] match {Systems_TournamentState.CurrentMatch} winner: " +
                      $"{(character != null ? character.behaviorName : "unknown")}");

            // The result card is itself delayed while the loser flops, so wait
            // that out too or the scene would change before it is readable.
            Invoke(nameof(ReturnToBracket), resultPause + _manager.limpBeforeAnnounce);
        }

        void ReturnToBracket()
        {
            SceneManager.LoadScene(bracketScene);
        }
    }
}
