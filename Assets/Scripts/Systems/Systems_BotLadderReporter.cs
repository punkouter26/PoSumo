using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoSumo
{
    /// Present in the arena only during a BOT LADDER bout. Reports whether the
    /// challenger beat the Bot into `Systems_BotLadderState` and returns to the
    /// tournament screen — the ladder's counterpart of `Systems_TournamentReporter`,
    /// spawned the same way by `Systems_MatchRoster`.
    public sealed class Systems_BotLadderReporter : MonoBehaviour
    {
        [Tooltip("Scene to return to when the bout is decided.")]
        public string returnScene = "SCN_TOURNAMENT";
        [Tooltip("Seconds to hold on the result before returning.")]
        public float resultPause = 2.5f;

        private Systems_GameMatchManager _manager;
        private bool _returning;

        private void Start()
        {
            _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            if (_manager == null) return;
            // Same treatment as a bracket bout: no REMATCH, a CONTINUE that goes back
            // to the tournament screen, and best-of-three (the manager reads
            // tournamentPointsToWin while the ladder is active).
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

            bool challengerWon = winner.character != null
                && winner.character == Systems_BotLadderState.Challenger;
            int tier = Systems_BotLadderState.Tier;
            Systems_BotLadderState.Report(challengerWon);
            Systems_Log.Info($"[LADDER] {Systems_BotLadderState.TierNames[tier]} bot " +
                             $"{(challengerWon ? "BEATEN" : "held")} by " +
                             $"{(winner.character != null ? winner.character.behaviorName : "?")}");

            float announceDelay = _manager.EndedByKnockout
                ? _manager.knockoutAnnounceSeconds
                : _manager.limpBeforeAnnounce;
            Invoke(nameof(Return), resultPause + announceDelay);
        }

        private void Return()
        {
            Time.timeScale = 1f;
            SceneManager.LoadScene(returnScene);
        }
    }
}
