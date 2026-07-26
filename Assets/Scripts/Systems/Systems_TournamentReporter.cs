using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoSumo
{
    /// Present in the arena only while a tournament match is being played.
    /// Forces sudden-death rules (one round decides the match), then records the
    /// winner into the bracket and returns to the bracket scene.
    ///
    /// Spawned at runtime by Systems_MatchRoster when Tournament_State.Active, so
    /// SCN_SUMO stays usable as a standalone exhibition scene with nothing to
    /// disable.
    public class Systems_TournamentReporter : MonoBehaviour
    {
        [Tooltip("Bracket scene to return to when the match is decided.")]
        public string bracketScene = "SCN_TOURNAMENT";
        [Tooltip("Seconds to hold on the result before returning to the bracket.")]
        public float resultPause = 2.5f;

        Systems_GameMatchManager _manager;
        bool _returning;

        void Start()
        {
            _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            if (_manager == null) return;

            // Sudden death: one round takes the match. pointsToWin is only read
            // when a round is scored, so setting it here is in time.
            _manager.pointsToWin = 1;
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
            Tournament_State.ReportWinner(character);
            Debug.Log($"[TOURNAMENT] match {Tournament_State.CurrentMatch} winner: " +
                      $"{(character != null ? character.behaviorName : "unknown")}");

            Invoke(nameof(ReturnToBracket), resultPause);
        }

        void ReturnToBracket()
        {
            SceneManager.LoadScene(bracketScene);
        }
    }
}
