using System.Text;
using UnityEditor;
using UnityEngine;

namespace PoSumo.EditorTools
{
    /// Runs a series of matches in the open sumo scene and reports who actually
    /// wins, so characters are judged on results rather than on one eyeballed
    /// round. Enter Play mode, then call Run(n).
    ///
    /// The match manager normally waits for Space/REMATCH at match end, so this
    /// calls its public ResetMatch() to chain matches unattended. That reset also
    /// cancels the announce still in flight — without it the previous match's
    /// result card revealed itself ~1.2 s into the NEXT match, froze both
    /// fighters and left every chained match to expire on the clock, which is
    /// what the tally below would then have been measuring.
    public static class MatchTestHarness
    {
        private static Systems_GameMatchManager _manager;
        private static int _targetMatches;
        private static int _matchesSeen;
        private static int _roundsSeen;
        private static int _baseWinsA;
        private static int _baseWinsB;
        private static float _roundStartTime;
        private static readonly StringBuilder Log = new StringBuilder();

        public static void Run(int matches)
        {
            if (!EditorApplication.isPlaying)
            {
                Debug.LogError("HARNESS: enter Play mode first.");
                return;
            }
            _manager = Object.FindAnyObjectByType<Systems_GameMatchManager>();
            if (_manager == null)
            {
                Debug.LogError("HARNESS: no match manager in the open scene.");
                return;
            }

            // Always detach first: a previous Run() that never reached its match
            // target left its handlers attached, and re-subscribing double-counted
            // every round (each one logged twice, the second with a 0.0s duration).
            _manager.RoundEnded -= OnRoundEnded;
            _manager.MatchEnded -= OnMatchEnded;

            _targetMatches = matches;
            _matchesSeen = 0;
            _roundsSeen = 0;
            _roundStartTime = Time.realtimeSinceStartup;
            // MatchWinsA/B accumulate from the manager's Start and are NOT reset
            // by ResetMatch, so reporting them raw counted matches played before
            // this Run — a second Run() in one Play session printed a tally whose
            // two halves did not sum to the match count.
            _baseWinsA = _manager.MatchWinsA;
            _baseWinsB = _manager.MatchWinsB;
            Log.Clear();

            _manager.RoundEnded += OnRoundEnded;
            _manager.MatchEnded += OnMatchEnded;
            Debug.Log($"HARNESS: running {matches} matches — {_manager.nameA} vs {_manager.nameB}");
        }

        private static void OnRoundEnded(Agent_Biped winner, Agent_Biped loser)
        {
            _roundsSeen++;
            float seconds = Time.realtimeSinceStartup - _roundStartTime;
            _roundStartTime = Time.realtimeSinceStartup;
            string who = winner == null
                ? "DRAW"
                : (winner == _manager.wrestlerA ? _manager.nameA : _manager.nameB);
            Log.AppendLine($"  round {_roundsSeen}: {who} ({seconds:F1}s)");
        }

        private static void OnMatchEnded(Agent_Biped winner)
        {
            _matchesSeen++;
            string who = winner == _manager.wrestlerA ? _manager.nameA : _manager.nameB;
            Debug.Log($"HARNESS: match {_matchesSeen}/{_targetMatches} won by {who} " +
                      $"({_manager.ScoreA}-{_manager.ScoreB})");

            if (_matchesSeen >= _targetMatches)
            {
                Finish();
                return;
            }
            // Chain into the next match without waiting for the REMATCH button.
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlaying && _manager != null)
                {
                    _manager.ResetMatch();
                }
            };
        }

        private static void Finish()
        {
            _manager.RoundEnded -= OnRoundEnded;
            _manager.MatchEnded -= OnMatchEnded;
            int winsA = _manager.MatchWinsA - _baseWinsA;
            int winsB = _manager.MatchWinsB - _baseWinsB;
            Debug.Log($"HARNESS RESULT: {_manager.nameA} {winsA} — {winsB} {_manager.nameB} " +
                      $"over {_matchesSeen} matches / {_roundsSeen} rounds | " +
                      $"longest round {_manager.LongestRound:F1}s\n{Log}");
        }
    }
}
