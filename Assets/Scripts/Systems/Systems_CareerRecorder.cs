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

        /// A rank change worth telling the player about.
        public readonly struct RankChange
        {
            public readonly string Fighter;
            public readonly string ToRank;
            public readonly bool Promoted;

            public RankChange(string fighter, string toRank, bool promoted)
            {
                Fighter = fighter;
                ToRank = toRank;
                Promoted = promoted;
            }
        }

        /// The last promotion or demotion, held for exactly one reader.
        ///
        /// Static because it has to survive the scene load back from SCN_SUMO to
        /// SCN_TOURNAMENT — the bracket is where the player finds out they went up,
        /// and this recorder is destroyed with the arena before the bracket exists.
        /// Cleared on `SubsystemRegistration` like every other static game state in
        /// this project, because Enter Play Mode domain reload is DISABLED here and
        /// it would otherwise still be announcing last session's promotion.
        private static RankChange _pending;
        private static bool _hasPending;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ClearPendingOnPlaySessionStart()
        {
            _pending = default;
            _hasPending = false;
        }

        /// Consume-once: the banner should appear when you get back to the bracket
        /// and not on every subsequent Refresh for the rest of the session.
        public static bool TryTakeRankChange(out RankChange change)
        {
            change = _pending;
            bool had = _hasPending;
            _hasPending = false;
            _pending = default;
            return had;
        }

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
            string winnerName = NameOf(winner);
            string loserName = NameOf(loser);

            // Rank is derived from the record, so it has to be sampled BEFORE the
            // write. Sampling the INDEX and not the Record matters: Systems_CareerStats
            // .Get returns the live object out of its list, so a "before" reference
            // would be mutated by RecordMatch and compare equal to itself.
            // A mirror bout is silently discarded by RecordMatch (winner == loser
            // keys the SAME record, so counting it would credit one fighter with a
            // win and a loss). That guard is right, but it used to be invisible: a
            // measured tournament ended on a Kim-v-Kim final that banked no Elo, no
            // W/L and no rank movement, and nothing anywhere said so. Seeding now
            // keeps mirrors out of the opening round, but later rounds depend on who
            // wins and cannot be prevented by the draw.
            // Non-null on both sides, or this fires for an unrated bout against a
            // brainless fighter — where NameOf returns null for BOTH names and
            // "null == null" reads as a mirror match that never happened.
            if (winnerName != null && winnerName == loserName)
            {
                Debug.LogWarning($"[CAREER] mirror bout ({winnerName} v {winnerName}) — " +
                                 "no Elo, W/L or rank recorded; only the title (if this was the final) counts");
            }

            int winnerRankBefore = Systems_CareerLadder.IndexFor(Systems_CareerStats.Get(winnerName));
            int loserRankBefore = Systems_CareerLadder.IndexFor(Systems_CareerStats.Get(loserName));

            Systems_CareerStats.RecordMatch(winnerName, loserName);

            // The final of a bracket also awards a title. Decided here rather than
            // in the reporter because an exhibition match must never award one.
            if (_isTournamentFinal)
            {
                Systems_CareerStats.RecordTitle(winnerName);
            }

            // AFTER the title, because the top two rungs are gated on titles as well
            // as rating — winning the final is exactly the moment a fighter can clear
            // the Ozeki or Yokozuna gate, and sampling before it would miss the one
            // promotion the player most wants to be told about.
            CaptureRankChange(winnerName, winnerRankBefore, loserName, loserRankBefore);
        }

        /// Records at most one change, preferring the promotion.
        ///
        /// A decided match usually moves both fighters' Elo, so it can produce a
        /// promotion and a demotion at once. Two banners would compete for the same
        /// line on the bracket, and "X PROMOTED TO OZEKI" is the news — the other
        /// fighter sliding a rung is the same event told from the losing side.
        private static void CaptureRankChange(string winnerName, int winnerRankBefore,
                                              string loserName, int loserRankBefore)
        {
            int winnerRankAfter = Systems_CareerLadder.IndexFor(Systems_CareerStats.Get(winnerName));
            // The UNRANKED guard is on BOTH branches, not just the demotion one.
            // UNRANKED is -1, so a fighter's first decided match always moves their
            // index up from -1 to a real rung — without this, every fighter would be
            // announced as "PROMOTED" the first time they won anything, which is an
            // arrival on the banzuke rather than a climb up it.
            if (winnerRankAfter > winnerRankBefore && winnerRankBefore != Systems_CareerLadder.UNRANKED)
            {
                _pending = new RankChange(winnerName,
                                          Systems_CareerLadder.RungAt(winnerRankAfter).Name,
                                          promoted: true);
                _hasPending = true;
                return;
            }

            int loserRankAfter = Systems_CareerLadder.IndexFor(Systems_CareerStats.Get(loserName));
            // UNRANKED is -1, so a fighter's FIRST match always "raises" their index
            // from -1 to a real rung. That is an arrival, not a promotion, and the
            // demotion branch must not read the mirror of it as a fall either.
            if (loserRankAfter < loserRankBefore && loserRankBefore != Systems_CareerLadder.UNRANKED)
            {
                _pending = new RankChange(loserName,
                                          Systems_CareerLadder.RungAt(loserRankAfter).Name,
                                          promoted: false);
                _hasPending = true;
            }
        }

        /// The fighter's ladder identity, or null if the fighter does not RATE.
        ///
        /// A character with no `inferenceModel` has no brain and collapses as a
        /// ragdoll — `Bot_v01` is exactly this, deliberately, and it stays in the
        /// bracket. What it must not do is score: every `Systems_CareerStats` entry
        /// point already discards a null name, so returning null here makes such a
        /// bout UNRATED on both sides — no Elo moves, no W/L, no round record, no
        /// rank change — while leaving the match itself completely untouched.
        ///
        /// Measured 2026-08-07, which is why this exists: a played bracket had Bot
        /// on 6 wins, 18 losses and **1 title** in `career.json`. A title is the
        /// gate on OZEKI and YOKOZUNA, so a brainless ragdoll was holding a
        /// promotion key, and every fighter who beat it was banking Elo for it.
        /// Beating a body that cannot fight is not evidence of rank either way, so
        /// the honest record is no record — the same treatment a bye would get.
        ///
        /// Note this deliberately does NOT block `RecordTitle`: a real fighter who
        /// wins the final against Bot still won the bracket, and `RecordTitle` is
        /// called with their own name, which is not null.
        private static string NameOf(Agent_Biped fighter)
        {
            if (fighter == null)
            {
                return null;
            }
            if (fighter.character != null && fighter.character.inferenceModel == null)
            {
                return null;
            }
            return fighter.character != null ? fighter.character.behaviorName : fighter.behaviorName;
        }
    }
}
