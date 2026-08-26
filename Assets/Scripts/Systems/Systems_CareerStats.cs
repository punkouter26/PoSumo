using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PoSumo
{
    /// Persistent career record: per-fighter W/L, head-to-head, Elo and titles.
    ///
    /// Static for the same reason [[Systems_TournamentState]] is — it has to
    /// outlive the scene loads between bracket and arena — but unlike that one it
    /// also survives the whole session, so it loads from disk instead of clearing.
    /// This project runs with Enter Play Mode domain reload DISABLED, so statics
    /// carry over between Play sessions with stale values; SubsystemRegistration
    /// runs once before the first scene of every session, which is the hook that
    /// makes "reload from disk" reliable.
    ///
    /// Records are keyed by behavior name (the fighter's stable identity across
    /// renames of folders and assets).
    public static class Systems_CareerStats
    {
        private const string FILE_NAME = "career.json";
        private const float ELO_START = 1000f;
        private const float ELO_K = 24f;

        /// Rating gap above which beating a stronger fighter counts as an UPSET.
        private const float UPSET_GAP = 40f;
        /// Extra rating TRANSFERRED loser -> winner on an upset. A transfer, not a
        /// grant: the banzuke thresholds are tuned for a zero-sum pool pinned at
        /// 1000 (see Systems_CareerLadder), so every bonus here moves points
        /// between the two fighters and never mints any.
        private const float UPSET_BONUS = 8f;
        /// Streak bonus per consecutive win from the third onward, transferred the
        /// same way, capped at STREAK_BONUS_MAX so a long run cannot snowball.
        private const float STREAK_BONUS = 4f;
        private const float STREAK_BONUS_MAX = 12f;
        private const int STREAK_FROM = 3;

        /// What the last decided match earned beyond plain Elo. Read by
        /// Systems_CareerRecorder immediately after RecordMatch, on the same frame;
        /// not persisted and not consume-once.
        public readonly struct MatchBonus
        {
            public readonly bool Upset;
            public readonly int Streak;
            public readonly float BonusElo;

            public MatchBonus(bool upset, int streak, float bonusElo)
            {
                Upset = upset;
                Streak = streak;
                BonusElo = bonusElo;
            }

            /// "UPSET +8 · 3-WIN STREAK +4", or empty when nothing extra happened.
            public string Describe()
            {
                if (BonusElo <= 0f) return string.Empty;
                var sb = new System.Text.StringBuilder(48);
                if (Upset) sb.Append("UPSET +").Append(Mathf.RoundToInt(UPSET_BONUS));
                if (Streak >= STREAK_FROM)
                {
                    if (sb.Length > 0) sb.Append(" · ");
                    sb.Append(Streak).Append("-WIN STREAK +")
                      .Append(Mathf.RoundToInt(StreakBonusFor(Streak)));
                }
                return sb.ToString();
            }
        }

        public static MatchBonus LastBonus { get; private set; }

        private static float StreakBonusFor(int streak) =>
            streak < STREAK_FROM ? 0f : Mathf.Min(STREAK_BONUS_MAX, STREAK_BONUS * (streak - STREAK_FROM + 1));

        [Serializable]
        public sealed class Record
        {
            public string fighter;
            public int matchWins;
            public int matchLosses;
            public int roundWins;
            public int roundLosses;
            public int titles;              // tournaments won
            public float elo = ELO_START;
            public int winStreak;           // consecutive decided-match wins, 0 after a loss
            public int bestStreak;
            public int upsets;              // wins over a higher-rated opponent (see UPSET_GAP)
            /// Flattened head-to-head: opponent names and wins against each, kept
            /// as parallel lists because Unity's JsonUtility cannot serialize a
            /// Dictionary.
            public List<string> vsNames = new List<string>();
            public List<int> vsWins = new List<int>();
            public List<int> vsLosses = new List<int>();
        }

        [Serializable]
        private sealed class SaveFile
        {
            public int version = 1;
            public int matchesPlayed;
            public List<Record> records = new List<Record>();
        }

        private static SaveFile _data = new SaveFile();
        private static bool _loaded;

        public static int MatchesPlayed => _data.matchesPlayed;

        private static string Path => System.IO.Path.Combine(Application.persistentDataPath, FILE_NAME);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ReloadOnPlaySessionStart()
        {
            _loaded = false;
            _data = new SaveFile();
            Load();
        }

        private static void Load()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            try
            {
                if (File.Exists(Path))
                {
                    string json = File.ReadAllText(Path);
                    SaveFile parsed = JsonUtility.FromJson<SaveFile>(json);
                    if (parsed != null && parsed.records != null)
                    {
                        _data = parsed;
                    }
                }
            }
            catch (Exception e)
            {
                // A corrupt or unreadable save must never block play — start fresh.
                Debug.LogWarning($"Systems_CareerStats: could not read {Path} ({e.Message}); starting a new career.");
                _data = new SaveFile();
            }
        }

        private static void Save()
        {
            try
            {
                File.WriteAllText(Path, JsonUtility.ToJson(_data, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Systems_CareerStats: could not write {Path} ({e.Message}).");
            }
        }

        public static Record Get(string fighter)
        {
            Load();
            if (string.IsNullOrEmpty(fighter))
            {
                return null;
            }
            for (int recordIndex = 0; recordIndex < _data.records.Count; recordIndex++)
            {
                if (_data.records[recordIndex].fighter == fighter)
                {
                    return _data.records[recordIndex];
                }
            }
            var fresh = new Record { fighter = fighter };
            _data.records.Add(fresh);
            return fresh;
        }

        /// Every fighter with a record, strongest first.
        public static List<Record> Ranked()
        {
            Load();
            var list = new List<Record>(_data.records);
            list.Sort((a, b) => b.elo.CompareTo(a.elo));
            return list;
        }

        public static void RecordRound(string winner, string loser)
        {
            if (string.IsNullOrEmpty(winner) || string.IsNullOrEmpty(loser) || winner == loser)
            {
                // A draw scores for nobody, and neither does a mirror bout: both
                // names key the SAME record, so counting it credited one fighter
                // with a round win and a round loss for every round of a
                // Matt-vs-Matt match. RecordMatch has always had this guard.
                return;
            }
            Get(winner).roundWins++;
            Get(loser).roundLosses++;
            Save();
        }

        /// A decided match: updates W/L, head-to-head and both Elo ratings.
        public static void RecordMatch(string winner, string loser)
        {
            if (string.IsNullOrEmpty(winner) || string.IsNullOrEmpty(loser) || winner == loser)
            {
                return;
            }
            Record w = Get(winner);
            Record l = Get(loser);

            w.matchWins++;
            l.matchLosses++;
            Bump(w, loser, won: true);
            Bump(l, winner, won: false);

            // Standard Elo. K is deliberately low-ish (24): these are deterministic
            // policies, so ratings should settle rather than swing on one bout.
            // Upset is judged on the ratings BEFORE this match moves them.
            bool upset = l.elo - w.elo >= UPSET_GAP;

            float expectedW = 1f / (1f + Mathf.Pow(10f, (l.elo - w.elo) / 400f));
            w.elo += ELO_K * (1f - expectedW);
            l.elo -= ELO_K * (1f - expectedW);

            // Streak + upset bonuses (2026-08-26). Both are TRANSFERS so the pool
            // stays zero-sum — see the constants. A loss ends the loser's streak.
            w.winStreak++;
            w.bestStreak = Mathf.Max(w.bestStreak, w.winStreak);
            l.winStreak = 0;
            float bonus = StreakBonusFor(w.winStreak);
            if (upset)
            {
                w.upsets++;
                bonus += UPSET_BONUS;
            }
            w.elo += bonus;
            l.elo -= bonus;
            LastBonus = new MatchBonus(upset, w.winStreak, bonus);

            _data.matchesPlayed++;
            Save();
        }

        public static void RecordTitle(string champion)
        {
            if (string.IsNullOrEmpty(champion))
            {
                return;
            }
            Get(champion).titles++;
            Save();
        }

        /// Wins-losses of `fighter` against one specific opponent.
        public static void HeadToHead(string fighter, string opponent, out int wins, out int losses)
        {
            wins = 0;
            losses = 0;
            Record r = Get(fighter);
            if (r == null)
            {
                return;
            }
            int index = r.vsNames.IndexOf(opponent);
            if (index < 0)
            {
                return;
            }
            wins = r.vsWins[index];
            losses = r.vsLosses[index];
        }

        public static void ResetAll()
        {
            _data = new SaveFile();
            _loaded = true;
            Save();
        }

        private static void Bump(Record record, string opponent, bool won)
        {
            int index = record.vsNames.IndexOf(opponent);
            if (index < 0)
            {
                record.vsNames.Add(opponent);
                record.vsWins.Add(0);
                record.vsLosses.Add(0);
                index = record.vsNames.Count - 1;
            }
            if (won)
            {
                record.vsWins[index]++;
            }
            else
            {
                record.vsLosses[index]++;
            }
        }
    }
}
