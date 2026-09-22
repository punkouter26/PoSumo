using UnityEngine;

namespace PoSumo
{
    /// Emergent storylines for a bout, derived from the career record the way a
    /// broadcast desk would: head-to-head, streaks, rating gaps, titles.
    ///
    /// PURE LOGIC ON PURPOSE — the same split as `Systems_Kimarite` /
    /// `Systems_KimariteCaller`: this class decides, its consumers present. It
    /// reads `Systems_CareerStats` and returns a string, or null when the pair
    /// has no story worth a line. No MonoBehaviour, no physics, no disk writes;
    /// the only mutation in the file is the `Enabled` switch below.
    ///
    /// There is deliberately no PostMatch half. `MatchEnded` handlers run in
    /// subscription order and `Systems_CareerRecorder` writes the career record
    /// from that same event, so a post-match line could not know whether it was
    /// reading the record before or after this match was banked. Pre-fight is
    /// unambiguous: the previous results are final by the time a bout opens.
    ///
    /// Consumed by `Systems_FighterPanel` (a second line under the stakes) and
    /// `Systems_Caster` (the opening call). Keyed by BEHAVIOUR NAME, like every
    /// other consumer of the career record.
    public static class Systems_Storylines
    {
        /// Set from `GameTuning.enableStorylines` by the match manager in Start.
        /// Static config rather than a companion flag because this type has no
        /// lifecycle of its own — and because Enter Play Mode domain reload is
        /// DISABLED in this project, it resets itself below like every other
        /// piece of static game state.
        public static bool Enabled = true;

        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlaySessionStart()
        {
            Enabled = true;
        }

        /// Rating gap that makes one side a certified underdog. The career pool
        /// is zero-sum at 1000, so gaps are relative — 80 is roughly the distance
        /// a mid-ladder fighter climbs in a bracket.
        private const int UPSET_GAP = 80;

        /// A streak only reads as a storyline once it is one.
        private const int STREAK_NOTABLE = 3;

        /// The opening line for a pair, or null for "nothing to sell".
        ///
        /// `behaviourA/B` are the career keys (null for the heuristic bot, which
        /// is unrated on purpose); `displayA/B` are what the screen shows them
        /// as — a mirror bout's second side carries a name suffix the record
        /// does not.
        public static string PreFight(string behaviourA, string behaviourB,
                                      string displayA, string displayB)
        {
            if (!Enabled || behaviourA == null || behaviourB == null)
            {
                return null;
            }

            Systems_CareerStats.Record recordA = Systems_CareerStats.Get(behaviourA);
            Systems_CareerStats.Record recordB = Systems_CareerStats.Get(behaviourB);
            if (recordA == null || recordB == null)
            {
                return null;
            }

            // Head-to-head, from A's side of the ledger. One lookup, three
            // parallel lists — JsonUtility cannot serialize a Dictionary, so
            // this is the shape the save file ships in.
            int index = recordA.vsNames.IndexOf(behaviourB);
            int winsA = index >= 0 ? recordA.vsWins[index] : 0;
            int lossesA = index >= 0 ? recordA.vsLosses[index] : 0;
            int meetings = winsA + lossesA;

            var sb = new System.Text.StringBuilder(64);

            // Priority 1 — a series that is on the line. The rubber match is the
            // strongest line in sport; a lopsided series is the second.
            if (meetings >= 2 && winsA == lossesA)
            {
                sb.Append("RUBBER MATCH - ALL TIME ").Append(winsA).Append('-').Append(lossesA);
                return sb.ToString();
            }
            if (meetings >= 3 && System.Math.Abs(winsA - lossesA) >= 2)
            {
                bool aLeads = winsA > lossesA;
                sb.Append(aLeads ? displayA : displayB).Append(" LEADS THE SERIES ")
                  .Append(aLeads ? winsA : lossesA).Append('-').Append(aLeads ? lossesA : winsA);
                return sb.ToString();
            }

            // Priority 2 — a live streak. The record's streak is the one the
            // career ledger banked; it spans opponents, which is what makes it
            // a run rather than a matchup note.
            if (recordA.winStreak >= STREAK_NOTABLE || recordB.winStreak >= STREAK_NOTABLE)
            {
                bool aHot = recordA.winStreak >= recordB.winStreak;
                int streak = aHot ? recordA.winStreak : recordB.winStreak;
                sb.Append(aHot ? displayA : displayB).Append(" ON ")
                  .Append(streak).Append(" STRAIGHT");
                return sb.ToString();
            }

            // Priority 3 — a rating gap big enough to call an upset watch.
            int gap = System.Math.Abs(Mathf.RoundToInt(recordA.elo - recordB.elo));
            if (gap >= UPSET_GAP)
            {
                bool aUnder = recordA.elo < recordB.elo;
                sb.Append("UPSET WATCH - ").Append(aUnder ? displayA : displayB)
                  .Append(" UNDER BY ").Append(gap);
                return sb.ToString();
            }

            // Priority 4 — a champion against a non-champion. Only worth a line
            // when exactly one side has a title; two champions is its own thing
            // but reads as a ordinary bout to anyone who cannot see the banners.
            if (recordA.titles > 0 != recordB.titles > 0)
            {
                bool aChamp = recordA.titles > 0;
                sb.Append(aChamp ? displayA : displayB).Append(" - ")
                  .Append(aChamp ? recordA.titles : recordB.titles)
                  .Append("X CHAMPION");
                return sb.ToString();
            }

            return null;
        }
    }
}
