using UnityEngine;

namespace PoSumo
{
    /// Bracket state for an 8-entrant single-elimination tournament.
    ///
    /// This is static on purpose: each match is played by loading SCN_SUMO and
    /// coming back, so the bracket has to outlive a scene load. Statics persist
    /// for the whole play session, which is exactly the lifetime needed — and it
    /// avoids a DontDestroyOnLoad object that would then have to be cleaned up.
    ///
    /// Match indices, seeds 0-7:
    ///   0..3  quarterfinals — seeds (0,1) (2,3) (4,5) (6,7)
    ///   4     semifinal     — winners of 0 and 1
    ///   5     semifinal     — winners of 2 and 3
    ///   6     final         — winners of 4 and 5
    public static class Systems_TournamentState
    {
        public const int SEED_COUNT = 8;
        public const int MATCH_COUNT = 7;
        public const int FINAL_MATCH = 6;

        /// This project runs with Enter Play Mode domain reload DISABLED, so
        /// statics survive Stop -> Play and a finished bracket would still be on
        /// screen next session. SubsystemRegistration runs once at the start of
        /// every play session, before the first scene loads, which is exactly the
        /// hook needed to clear it — relying on the domain reload does not work.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ClearOnPlaySessionStart() => ResetAll();

        private static readonly Agent_CharacterDefinition[] _seeds = new Agent_CharacterDefinition[SEED_COUNT];
        private static readonly Agent_CharacterDefinition[] _winners = new Agent_CharacterDefinition[MATCH_COUNT];

        /// True while a tournament is being played; SCN_SUMO checks this to know
        /// it should use the bracket's pairing rather than its own roster.
        public static bool Active { get; private set; }

        /// Index of the match currently being played (0..6).
        public static int CurrentMatch { get; private set; }

        public static Agent_CharacterDefinition Champion => _winners[FINAL_MATCH];
        public static bool IsComplete => Champion != null;

        /// Human-readable name of the round a match belongs to. Lives here rather
        /// than on the bracket screen because the arena needs it too — a fighter
        /// watching a bout could not tell a quarterfinal from the final.
        public static string RoundName(int match)
        {
            if (match >= FINAL_MATCH) return "FINAL";
            if (match >= 4) return "SEMIFINAL";
            return "QUARTERFINAL";
        }

        public static Agent_CharacterDefinition GetSeed(int slot) => _seeds[slot];
        public static Agent_CharacterDefinition GetWinner(int match) => _winners[match];

        public static void SetSeed(int slot, Agent_CharacterDefinition character)
        {
            if (slot < 0 || slot >= SEED_COUNT) return;
            _seeds[slot] = character;
        }

        public static void SwapSeeds(int a, int b)
        {
            if (a == b) return;
            if (a < 0 || a >= SEED_COUNT || b < 0 || b >= SEED_COUNT) return;
            (_seeds[a], _seeds[b]) = (_seeds[b], _seeds[a]);
        }

        /// Entrants for a match, resolved from seeds or earlier winners.
        /// Either may be null while the bracket is still being filled in.
        public static void GetEntrants(int match, out Agent_CharacterDefinition a,
                                       out Agent_CharacterDefinition b)
        {
            if (match < 4)
            {
                a = _seeds[match * 2];
                b = _seeds[match * 2 + 1];
                return;
            }
            int feederA = (match - 4) * 2;      // match 4 <- 0,1   match 5 <- 2,3
            if (match == FINAL_MATCH)
            {
                feederA = 4;                     // final <- 4,5
            }
            a = _winners[feederA];
            b = _winners[feederA + 1];
        }

        public static Agent_CharacterDefinition CurrentA
        {
            get { GetEntrants(CurrentMatch, out var a, out _); return a; }
        }

        public static Agent_CharacterDefinition CurrentB
        {
            get { GetEntrants(CurrentMatch, out _, out var b); return b; }
        }

        /// Every seed slot filled — required before the bracket can start.
        public static bool SeedsReady()
        {
            for (int slot = 0; slot < SEED_COUNT; slot++)
            {
                if (_seeds[slot] == null) return false;
            }
            return true;
        }

        /// Fill the bracket with each supplied character an equal number of
        /// times, shuffled. With 4 characters that is two appearances each.
        public static void AutoSeed(Agent_CharacterDefinition[] roster, int shuffleSalt)
        {
            if (roster == null || roster.Length == 0) return;
            for (int slot = 0; slot < SEED_COUNT; slot++)
            {
                _seeds[slot] = roster[slot % roster.Length];
            }
            // Deterministic Fisher-Yates from a caller-supplied salt: scenes must
            // not call Random during load order, and a salt keeps reshuffles
            // reproducible if the user wants the same draw again.
            var rng = new System.Random(shuffleSalt);
            for (int index = SEED_COUNT - 1; index > 0; index--)
            {
                int j = rng.Next(index + 1);
                (_seeds[index], _seeds[j]) = (_seeds[j], _seeds[index]);
            }

            SeparateFirstRoundMirrors();
        }

        /// Repairs any opening-round pair that drew the same fighter twice.
        ///
        /// The roster is four fighters in eight slots, so every fighter appears
        /// twice and a plain shuffle regularly pairs a fighter against itself. A
        /// mirror bout is the worst match in the game: the two sides are the same
        /// policy, so the result is a coin flip, and `Systems_CareerStats.RecordMatch`
        /// guards `winner == loser` — it banks no Elo, no W/L, no head-to-head and
        /// moves nobody on the banzuke. Measured play produced one in the
        /// quarterfinals AND a Kim-v-Kim final.
        ///
        /// **This can only fix the FIRST round, and that limit is structural.** Later
        /// mirrors depend on who wins, not on the draw: with two copies of a fighter
        /// alive in opposite halves, nothing in a seeding pass can stop them meeting
        /// in the final. The only real cure is a roster that fills eight slots
        /// without duplicates. Four of the seven matches are the opening round, so
        /// this removes the bulk of them and the ones that remain are at least an
        /// earned "beat your own clone" rather than a bad draw.
        private static void SeparateFirstRoundMirrors()
        {
            for (int pair = 0; pair < SEED_COUNT / 2; pair++)
            {
                int left = pair * 2;
                int right = left + 1;
                if (_seeds[left] != _seeds[right]) continue;

                // Swap the right-hand slot with any slot from another pair where the
                // exchange leaves BOTH pairs clean. With each of four fighters
                // appearing exactly twice such a slot always exists — a mirror-free
                // opening round is always constructible (A B / C D / A B / C D) — so
                // this cannot loop or fail to find one.
                for (int other = 0; other < SEED_COUNT; other++)
                {
                    if (other == left || other == right) continue;
                    int otherPartner = (other % 2 == 0) ? other + 1 : other - 1;
                    if (otherPartner == left || otherPartner == right) continue;

                    bool fixesThisPair = _seeds[other] != _seeds[left];
                    bool keepsOtherPairClean = _seeds[right] != _seeds[otherPartner];
                    if (fixesThisPair && keepsOtherPairClean)
                    {
                        (_seeds[right], _seeds[other]) = (_seeds[other], _seeds[right]);
                        break;
                    }
                }
            }
        }

        public static void BeginTournament()
        {
            for (int match = 0; match < MATCH_COUNT; match++)
            {
                _winners[match] = null;
            }
            CurrentMatch = 0;
            Active = true;
        }

        /// Record the result of the current match and advance. Returns true when
        /// the tournament still has matches left to play.
        public static bool ReportWinner(Agent_CharacterDefinition winner)
        {
            if (!Active) return false;
            _winners[CurrentMatch] = winner;
            if (CurrentMatch >= FINAL_MATCH)
            {
                Active = false;
                return false;
            }
            CurrentMatch++;
            return true;
        }

        /// Leave tournament mode without clearing the bracket, so the results
        /// stay on screen after the final.
        public static void Stop() => Active = false;

        public static void ResetAll()
        {
            for (int slot = 0; slot < SEED_COUNT; slot++) _seeds[slot] = null;
            for (int match = 0; match < MATCH_COUNT; match++) _winners[match] = null;
            CurrentMatch = 0;
            Active = false;
        }
    }
}
