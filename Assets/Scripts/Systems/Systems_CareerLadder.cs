using UnityEngine;

namespace PoSumo
{
    /// The banzuke: maps a fighter's persistent career record onto a sumo rank, so
    /// the Elo that `Systems_CareerStats` has always computed becomes something a
    /// player can climb rather than a four-digit number in a table.
    ///
    /// Pure logic over `Systems_CareerStats.Record` — no state of its own, nothing
    /// serialized, nothing to reset. The record is the truth; a rank is a view of it.
    ///
    /// **The thresholds are tuned for a ZERO-SUM pool and that is the thing to
    /// understand before touching them.** Elo here is closed: four fighters, every
    /// match moves points from the loser to the winner, so the pool mean is pinned
    /// at `ELO_START` (1000) forever. Nobody can grind the whole roster upward, and
    /// there is no absolute scale to calibrate against — only distance from 1000.
    /// A dominant fighter realistically reaches ~+160 over a long career at K = 24
    /// (about 13 net wins against equals), which is what `YOKOZUNA` is set to, and
    /// the bands below fan out from 1000 rather than from zero. Widen them and the
    /// top ranks become unreachable; narrow them and every fighter is a Yokozuna.
    ///
    /// A fresh fighter sits at 1000 = `MAKUSHITA`, index 3 of 10: room to fall
    /// three ranks and climb six, which is what makes early matches feel like they
    /// move something.
    public static class Systems_CareerLadder
    {
        /// One rung. `EloFloor` is inclusive; `TitlesRequired` gates the top two.
        public readonly struct Rung
        {
            public readonly string Name;
            public readonly float EloFloor;
            public readonly int TitlesRequired;

            public Rung(string name, float eloFloor, int titlesRequired)
            {
                Name = name;
                EloFloor = eloFloor;
                TitlesRequired = titlesRequired;
            }
        }

        /// Ascending. Plain ASCII on purpose — the real names carry macrons
        /// (Jūryō, Ōzeki) and `Assets/UI Toolkit/README.md` records that this
        /// project ships no font asset yet, so every screen renders in Unity's
        /// default UI font and an unsupported glyph draws as a box.
        ///
        /// The top two rungs require TITLES as well as rating, and that is the
        /// whole point of the gate: without it the bracket would be a way to farm
        /// Elo and nothing more. Winning a tournament is the only route to Ozeki,
        /// which is exactly how promotion works in the sport.
        private static readonly Rung[] RUNGS =
        {
            new Rung("JONOKUCHI",  float.NegativeInfinity, 0),
            new Rung("JONIDAN",     900f, 0),
            new Rung("SANDANME",    940f, 0),
            new Rung("MAKUSHITA",   975f, 0),
            new Rung("JURYO",      1005f, 0),
            new Rung("MAEGASHIRA", 1035f, 0),
            new Rung("KOMUSUBI",   1065f, 0),
            new Rung("SEKIWAKE",   1095f, 0),
            new Rung("OZEKI",      1125f, 1),
            new Rung("YOKOZUNA",   1160f, 2),
        };

        /// Returned by `IndexFor` for a fighter who has never had a decided match.
        /// Their Elo is the 1000 default, which would otherwise plant them in the
        /// middle of the ladder having done nothing.
        public const int UNRANKED = -1;

        public static int RungCount => RUNGS.Length;

        public static Rung RungAt(int index) =>
            RUNGS[Mathf.Clamp(index, 0, RUNGS.Length - 1)];

        /// The highest rung whose rating floor AND title requirement are both met.
        ///
        /// Walks downward rather than upward so a title gate cannot strand a
        /// fighter: someone rated 1200 with no titles is SEKIWAKE, not stopped at
        /// the first rung they fail.
        public static int IndexFor(Systems_CareerStats.Record record)
        {
            if (record == null || record.matchWins + record.matchLosses == 0)
            {
                return UNRANKED;
            }
            for (int rungIndex = RUNGS.Length - 1; rungIndex >= 0; rungIndex--)
            {
                Rung rung = RUNGS[rungIndex];
                if (record.elo >= rung.EloFloor && record.titles >= rung.TitlesRequired)
                {
                    return rungIndex;
                }
            }
            return 0;
        }

        public static string NameFor(Systems_CareerStats.Record record)
        {
            int index = IndexFor(record);
            return index == UNRANKED ? "UNRANKED" : RUNGS[index].Name;
        }

        /// Progress toward the next rung, 0..1, plus what is still standing in the
        /// way. `requirement` is null when rating alone will do it.
        ///
        /// Returns 1 and a null requirement at the top of the ladder — a Yokozuna
        /// is not working toward anything, and a bar stuck at 0 would read as a
        /// fighter who had stalled rather than one who had arrived.
        public static float ProgressToNext(Systems_CareerStats.Record record,
                                           out string requirement)
        {
            requirement = null;
            if (record == null)
            {
                return 0f;
            }

            int index = IndexFor(record);
            if (index >= RUNGS.Length - 1)
            {
                return 1f;
            }

            Rung next = RUNGS[index + 1];
            if (record.titles < next.TitlesRequired)
            {
                int short_ = next.TitlesRequired - record.titles;
                requirement = short_ == 1
                    ? $"WIN A TOURNAMENT FOR {next.Name}"
                    : $"WIN {short_} TOURNAMENTS FOR {next.Name}";
            }

            // Measured from the CURRENT rung's floor, not from zero. The bottom
            // rung's floor is negative infinity, so it needs a real span to divide
            // by or the fraction is NaN — one band's width below the next floor is
            // the honest equivalent.
            float floor = index == UNRANKED ? next.EloFloor - 100f : RUNGS[index].EloFloor;
            if (float.IsNegativeInfinity(floor))
            {
                floor = next.EloFloor - 100f;
            }

            float span = next.EloFloor - floor;
            if (span <= 0f)
            {
                return 1f;
            }
            return Mathf.Clamp01((record.elo - floor) / span);
        }

        /// Elo still needed for the next rung, rounded up; 0 once the rating is
        /// already there (which is when `requirement` above is the real blocker).
        public static int EloToNext(Systems_CareerStats.Record record)
        {
            if (record == null)
            {
                return 0;
            }
            int index = IndexFor(record);
            if (index >= RUNGS.Length - 1)
            {
                return 0;
            }
            return Mathf.Max(0, Mathf.CeilToInt(RUNGS[index + 1].EloFloor - record.elo));
        }
    }
}
