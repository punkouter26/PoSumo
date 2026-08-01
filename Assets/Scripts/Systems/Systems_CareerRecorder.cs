using UnityEngine;

namespace PoSumo
{
    /// Bridges live match events into the persistent career record. Spawned at
    /// runtime by Systems_GameMatchManager alongside the other match companions,
    /// so exhibition and tournament bouts are both counted with nothing to wire up
    /// in a scene.
    ///
    /// Fighters are identified by behavior name, which is stable across renames of
    /// folders and assets, and is what Systems_CareerStats keys on.
    public sealed class Systems_CareerRecorder : MonoBehaviour
    {
        private Systems_GameMatchManager _manager;

        /// Snapshotted at Start, not read at MatchEnded.
        ///
        /// Systems_TournamentReporter also subscribes to MatchEnded, and it
        /// subscribes FIRST — its GameObject is created in Systems_MatchRoster's
        /// Awake at execution order -500, while this one is created inside the
        /// match manager's Start. Its handler calls ReportWinner, which clears
        /// Active on the final, so by the time this recorder ran the guard the
        /// bracket already looked inactive and RecordTitle — the only title
        /// writer in the project — never fired once. Reading the state before
        /// any of that happens removes the ordering dependency instead of
        /// trading one fragile order for another.
        private bool _isTournamentFinal;

        private void Start()
        {
            _isTournamentFinal = Systems_TournamentState.Active
                && Systems_TournamentState.CurrentMatch == Systems_TournamentState.FINAL_MATCH;

            _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            if (_manager == null)
            {
                return;
            }
            _manager.RoundEnded += OnRoundEnded;
            _manager.MatchEnded += OnMatchEnded;
        }

        private void OnDisable()
        {
            if (_manager != null)
            {
                _manager.RoundEnded -= OnRoundEnded;
                _manager.MatchEnded -= OnMatchEnded;
            }
        }

        private void OnRoundEnded(Agent_Biped winner, Agent_Biped loser)
        {
            // Both null on a draw, which scores for nobody.
            Systems_CareerStats.RecordRound(NameOf(winner), NameOf(loser));
        }

        private void OnMatchEnded(Agent_Biped winner)
        {
            if (winner == null)
            {
                return;
            }
            Agent_Biped loser = winner == _manager.wrestlerA ? _manager.wrestlerB : _manager.wrestlerA;
            Systems_CareerStats.RecordMatch(NameOf(winner), NameOf(loser));

            // The final of a bracket also awards a title. Decided here rather than
            // in the reporter because an exhibition match must never award one.
            if (_isTournamentFinal)
            {
                Systems_CareerStats.RecordTitle(NameOf(winner));
            }
        }

        private static string NameOf(Agent_Biped fighter)
        {
            if (fighter == null)
            {
                return null;
            }
            return fighter.character != null ? fighter.character.behaviorName : fighter.behaviorName;
        }
    }
}
