using System;
using System.IO;
using UnityEngine;

namespace PoSumo
{
    /// Bracket state for a single-elimination tournament of UP TO eight entrants.
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
    ///
    /// **Every fighter is seeded exactly ONCE; the slots left over are BYES.**
    /// The roster is five fighters and the frame is eight slots. Until 2026-10-04
    /// the gap was filled by seeding fighters twice, which made mirror bouts
    /// structural — a measured bracket ran Matt v Grandma in BOTH semifinals and
    /// Matt v Matt in the final, and `Systems_CareerStats.RecordMatch` guards
    /// `winner == loser`, so the most important match of the bracket banked
    /// nothing. A seeding pass could only ever repair the first round.
    ///
    /// A bye is an EMPTY seed slot (null). A match with fewer than two entrants
    /// is a WALKOVER: it is decided here, without loading the arena, and whoever
    /// is present advances. The eight-slot / seven-match frame is unchanged, so
    /// every index above still means what it did; what changed is that only the
    /// matches with two entrants are BOUTS. Five fighters play four bouts
    /// (`BoutCount` — one fighter is eliminated per bout, so it is always
    /// entrants minus one, whatever the draw).
    public static class Systems_TournamentState
    {
        public const int SEED_COUNT = 8;
        public const int MATCH_COUNT = 7;
        public const int FINAL_MATCH = 6;

        // What happened to a match slot. A null winner is ambiguous on its own —
        // "not played yet" and "a walkover between two byes" both carry nobody —
        // so the outcome is recorded beside it.
        private const int OUTCOME_PENDING = 0;
        private const int OUTCOME_BOUT = 1;
        private const int OUTCOME_WALKOVER = 2;

        private const string FILE_NAME = "bracket.json";

        /// This project runs with Enter Play Mode domain reload DISABLED, so
        /// statics survive Stop -> Play and a finished bracket would still be on
        /// screen next session. SubsystemRegistration runs once at the start of
        /// every play session, before the first scene loads, which is exactly the
        /// hook needed to clear it — relying on the domain reload does not work.
        ///
        /// MEMORY ONLY. It deliberately does not read `bracket.json` back: a
        /// session always opens on a fresh draw and the bracket screen OFFERS the
        /// saved one (`TryPeekSaved` / `TryResume`). Resuming here would put a
        /// half-played bracket under anything that presses START on a new session
        /// — `BracketTestHarness` first among them — and would do it silently.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ClearOnPlaySessionStart() => ResetAll();

        private static readonly Agent_CharacterDefinition[] _seeds = new Agent_CharacterDefinition[SEED_COUNT];
        private static readonly Agent_CharacterDefinition[] _winners = new Agent_CharacterDefinition[MATCH_COUNT];
        private static readonly int[] _outcomes = new int[MATCH_COUNT];
        /// True once a draw has been made. "Every slot non-null" stopped being the
        /// test for that the day an empty slot started meaning BYE.
        private static bool _seeded;

        /// True while a tournament is being played; SCN_SUMO checks this to know
        /// it should use the bracket's pairing rather than its own roster.
        public static bool Active { get; private set; }

        /// Index of the match currently being played (0..6). Walkovers are
        /// stepped over, so while `Active` this always names a real bout.
        public static int CurrentMatch { get; private set; }

        public static Agent_CharacterDefinition Champion => _winners[FINAL_MATCH];
        public static bool IsComplete => _outcomes[FINAL_MATCH] != OUTCOME_PENDING && Champion != null;

        /// Fighters in the draw — the non-empty seed slots.
        public static int FighterCount
        {
            get
            {
                int count = 0;
                for (int slot = 0; slot < SEED_COUNT; slot++)
                {
                    if (_seeds[slot] != null) count++;
                }
                return count;
            }
        }

        /// Empty seed slots. Each one hands somebody a walkover.
        public static int ByeCount => _seeded ? SEED_COUNT - FighterCount : 0;

        /// Bouts the whole bracket will actually play. Single elimination removes
        /// exactly one fighter per bout and a walkover removes none, so this is
        /// independent of where the byes fall.
        public static int BoutCount => Mathf.Max(0, FighterCount - 1);

        /// Bouts decided so far.
        public static int BoutsPlayed
        {
            get
            {
                int count = 0;
                for (int match = 0; match < MATCH_COUNT; match++)
                {
                    if (_outcomes[match] == OUTCOME_BOUT) count++;
                }
                return count;
            }
        }

        /// True when the current bout decides the title: two fighters left alive.
        ///
        /// NOT `CurrentMatch == FINAL_MATCH`. That holds for a balanced draw, but
        /// with byes the last bout need not sit in the final's slot — put every
        /// fighter in one half and the title is settled in a semifinal, with the
        /// final a walkover nobody plays. `Systems_CareerRecorder` awards the
        /// title off this, so it has to follow the fighters and not the frame.
        public static bool IsTitleBout => Active && FighterCount - BoutsPlayed == 2;

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

        /// True when the match was a real bout that has been decided — as opposed
        /// to pending, or settled by a bye.
        public static bool WasBout(int match) => _outcomes[match] == OUTCOME_BOUT;

        /// Who comes out of a match, as far as can be KNOWN right now: the recorded
        /// winner, or — for a match nobody has to fight — the fighter a bye carries
        /// through it. Null when the match is still a real bout to be played, and
        /// also for a walkover between two byes.
        ///
        /// The bracket screen draws through this so a bye reads as an advance the
        /// moment the draw is made ("MATT v BYE -> MATT") instead of as an empty
        /// winner chip that only fills in when START is pressed.
        public static Agent_CharacterDefinition GetAdvancing(int match)
        {
            Resolve(match, out Agent_CharacterDefinition winner);
            return winner;
        }

        /// Returns true when the match's outcome is already determined, with the
        /// fighter it sends on (possibly nobody). Depth is at most three.
        private static bool Resolve(int match, out Agent_CharacterDefinition winner)
        {
            winner = null;
            if (_outcomes[match] != OUTCOME_PENDING)
            {
                winner = _winners[match];
                return true;
            }

            Agent_CharacterDefinition a;
            Agent_CharacterDefinition b;
            bool knownA = true;
            bool knownB = true;
            if (match < 4)
            {
                a = _seeds[match * 2];
                b = _seeds[match * 2 + 1];
            }
            else
            {
                int feederA = FeederOf(match);
                knownA = Resolve(feederA, out a);
                knownB = Resolve(feederA + 1, out b);
            }

            if (!knownA || !knownB) return false;
            if (a != null && b != null) return false;      // a bout: nobody knows yet
            winner = a != null ? a : b;
            return true;
        }

        /// First of the two matches that feed `match` (the other is the next
        /// index): match 4 <- 0,1   match 5 <- 2,3   final <- 4,5.
        private static int FeederOf(int match) => match == FINAL_MATCH ? 4 : (match - 4) * 2;

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

        /// Puts `character` into `slot` WITHOUT duplicating it: if it is already
        /// in the draw, the two slots trade places, so whoever (or whatever bye)
        /// sat in the target goes to where the fighter came from.
        ///
        /// This is what a drop from the roster palette calls. It used to be a
        /// plain `SetSeed`, which is how a fighter was put into the draw a second
        /// time by hand — and the fighter it overwrote silently left the draw.
        public static void PlaceSeed(int slot, Agent_CharacterDefinition character)
        {
            if (slot < 0 || slot >= SEED_COUNT || character == null) return;
            for (int other = 0; other < SEED_COUNT; other++)
            {
                if (_seeds[other] == character)
                {
                    SwapSeeds(other, slot);
                    return;
                }
            }
            _seeds[slot] = character;
        }

        /// Entrants for a match, resolved from seeds or earlier winners.
        /// Either may be null: a bye, or an earlier match not yet decided.
        public static void GetEntrants(int match, out Agent_CharacterDefinition a,
                                       out Agent_CharacterDefinition b)
        {
            if (match < 4)
            {
                a = _seeds[match * 2];
                b = _seeds[match * 2 + 1];
                return;
            }
            int feederA = FeederOf(match);
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

        /// A draw exists and holds enough fighters for at least one bout —
        /// required before the bracket can start. Empty slots are byes, not gaps.
        public static bool SeedsReady() => _seeded && FighterCount >= 2;

        /// Draw the bracket: every supplied character ONCE, shuffled, with the
        /// remaining slots left empty as byes.
        ///
        /// The byes are dealt one per quarterfinal pair before any pair gets a
        /// second, so a five-fighter draw is three "fighter v BYE" rows and one
        /// real quarterfinal rather than a dead "BYE v BYE" row beside two real
        /// ones. Which pairs get them, and which side of the pair, is shuffled.
        public static void AutoSeed(Agent_CharacterDefinition[] roster, int shuffleSalt)
        {
            if (roster == null || roster.Length == 0) return;

            // Distinct, non-null entrants, capped at the frame. Compared by
            // reference: one asset is one fighter.
            var entrants = new Agent_CharacterDefinition[SEED_COUNT];
            int entrantCount = 0;
            for (int rosterIndex = 0; rosterIndex < roster.Length && entrantCount < SEED_COUNT; rosterIndex++)
            {
                Agent_CharacterDefinition candidate = roster[rosterIndex];
                if (candidate == null) continue;
                bool seen = false;
                for (int entrantIndex = 0; entrantIndex < entrantCount; entrantIndex++)
                {
                    if (entrants[entrantIndex] == candidate) { seen = true; break; }
                }
                if (!seen) entrants[entrantCount++] = candidate;
            }
            if (entrantCount == 0) return;

            // Deterministic Fisher-Yates from a caller-supplied salt: scenes must
            // not call Random during load order, and a salt keeps reshuffles
            // reproducible if the user wants the same draw again.
            var rng = new System.Random(shuffleSalt);
            for (int index = entrantCount - 1; index > 0; index--)
            {
                int j = rng.Next(index + 1);
                (entrants[index], entrants[j]) = (entrants[j], entrants[index]);
            }

            // How many of each pair's two slots are filled. Everybody gets one
            // before anybody gets two (when there are fewer fighters than pairs
            // the tail pairs stay empty), then the pair order is shuffled so the
            // byes do not always land on the same rows.
            const int PAIRS = SEED_COUNT / 2;
            var fill = new int[PAIRS];
            for (int entrantIndex = 0; entrantIndex < entrantCount; entrantIndex++)
            {
                fill[entrantIndex % PAIRS]++;
            }
            for (int index = PAIRS - 1; index > 0; index--)
            {
                int j = rng.Next(index + 1);
                (fill[index], fill[j]) = (fill[j], fill[index]);
            }

            int next = 0;
            for (int pair = 0; pair < PAIRS; pair++)
            {
                int left = pair * 2;
                _seeds[left] = null;
                _seeds[left + 1] = null;
                if (fill[pair] == 2)
                {
                    _seeds[left] = entrants[next++];
                    _seeds[left + 1] = entrants[next++];
                }
                else if (fill[pair] == 1)
                {
                    _seeds[left + rng.Next(2)] = entrants[next++];
                }
            }
            _seeded = true;
        }

        public static void BeginTournament()
        {
            for (int match = 0; match < MATCH_COUNT; match++)
            {
                _winners[match] = null;
                _outcomes[match] = OUTCOME_PENDING;
            }
            CurrentMatch = 0;
            Active = true;
            AdvanceToNextBout();
            Save();
        }

        /// Record the result of the current match and advance. Returns true when
        /// the tournament still has matches left to play.
        public static bool ReportWinner(Agent_CharacterDefinition winner)
        {
            if (!Active) return false;
            _winners[CurrentMatch] = winner;
            _outcomes[CurrentMatch] = OUTCOME_BOUT;
            if (CurrentMatch < FINAL_MATCH)
            {
                CurrentMatch++;
                AdvanceToNextBout();
            }
            else
            {
                Active = false;
            }

            if (Active)
            {
                Save();
            }
            else
            {
                // Finished: there is nothing left to resume.
                DeleteSave();
            }
            return Active;
        }

        /// Steps `CurrentMatch` forward to the next match that has two entrants,
        /// settling every walkover it passes. Matches are visited in index order
        /// and a match's feeders always have lower indices, so by the time a slot
        /// is looked at both of its entrants are final.
        private static void AdvanceToNextBout()
        {
            while (true)
            {
                GetEntrants(CurrentMatch, out Agent_CharacterDefinition a, out Agent_CharacterDefinition b);
                if (a != null && b != null) return;

                _winners[CurrentMatch] = a != null ? a : b;
                _outcomes[CurrentMatch] = OUTCOME_WALKOVER;
                if (CurrentMatch >= FINAL_MATCH)
                {
                    Active = false;
                    return;
                }
                CurrentMatch++;
            }
        }

        /// Leave tournament mode without clearing the bracket, so the results
        /// stay on screen after the final.
        ///
        /// Also drops the save. The one caller is the player walking out of a
        /// bracket bout (QUIT MATCH), after which the bracket screen offers a
        /// clean restart — an abandoned bracket that then came back as a RESUME
        /// offer on the next launch would contradict the choice just made.
        public static void Stop()
        {
            Active = false;
            DeleteSave();
        }

        /// Clears the bracket IN MEMORY. The save file is left alone on purpose:
        /// this is what runs at the start of every play session, and a reshuffle
        /// of the draw must not throw away a bracket the player has not yet
        /// decided whether to resume. The file goes when a bracket completes, is
        /// walked out of (`Stop`), or is overwritten by the next START.
        public static void ResetAll()
        {
            for (int slot = 0; slot < SEED_COUNT; slot++) _seeds[slot] = null;
            for (int match = 0; match < MATCH_COUNT; match++)
            {
                _winners[match] = null;
                _outcomes[match] = OUTCOME_PENDING;
            }
            CurrentMatch = 0;
            Active = false;
            _seeded = false;
        }

        // --- persistence ---------------------------------------------------
        //
        // The bracket survives the app being killed. Same approach as
        // Systems_CareerStats: one small JSON file in persistentDataPath, written
        // on every state change that matters (START and each reported winner).
        //
        // Fighters are stored by BEHAVIOR NAME — the only identity stable across
        // folder and asset renames. An empty string is a bye / nobody. Arrays,
        // not a Dictionary: JsonUtility cannot serialize one.

        [Serializable]
        private sealed class SaveFile
        {
            public int version = 1;
            public string[] seeds = new string[SEED_COUNT];
            public string[] winners = new string[MATCH_COUNT];
            public int[] outcomes = new int[MATCH_COUNT];
            public int currentMatch;
        }

        /// What a saved bracket would resume into, for the offer on the bracket
        /// screen. Names are behavior names.
        public struct SavedSummary
        {
            public int BoutNumber;      // 1-based: the bout that would be played next
            public int BoutCount;
            public string FighterA;
            public string FighterB;
        }

        private static string SavePath => Path.Combine(Application.persistentDataPath, FILE_NAME);

        private static void Save()
        {
            try
            {
                var file = new SaveFile { currentMatch = CurrentMatch };
                for (int slot = 0; slot < SEED_COUNT; slot++) file.seeds[slot] = NameOf(_seeds[slot]);
                for (int match = 0; match < MATCH_COUNT; match++)
                {
                    file.winners[match] = NameOf(_winners[match]);
                    file.outcomes[match] = _outcomes[match];
                }
                File.WriteAllText(SavePath, JsonUtility.ToJson(file, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Systems_TournamentState: could not save the bracket ({e.Message}). " +
                                 "It will not survive the app closing.");
            }
        }

        private static void DeleteSave()
        {
            try
            {
                if (File.Exists(SavePath)) File.Delete(SavePath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Systems_TournamentState: could not delete the saved bracket ({e.Message}).");
            }
        }

        private static string NameOf(Agent_CharacterDefinition character) =>
            character != null ? character.behaviorName : string.Empty;

        /// Reads and sanity-checks the save. Null when there is none, or when it
        /// is not a bracket this build can pick up mid-flight.
        private static SaveFile LoadSave()
        {
            try
            {
                if (!File.Exists(SavePath)) return null;
                SaveFile file = JsonUtility.FromJson<SaveFile>(File.ReadAllText(SavePath));
                if (file == null || file.seeds == null || file.winners == null || file.outcomes == null) return null;
                if (file.seeds.Length != SEED_COUNT || file.winners.Length != MATCH_COUNT
                    || file.outcomes.Length != MATCH_COUNT) return null;
                if (file.currentMatch < 0 || file.currentMatch >= MATCH_COUNT) return null;
                // Only an IN-PROGRESS bracket is ever written, so the match it
                // points at must still be open.
                if (file.outcomes[file.currentMatch] != OUTCOME_PENDING) return null;
                return file;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Systems_TournamentState: saved bracket is unreadable ({e.Message}).");
                return null;
            }
        }

        private static Agent_CharacterDefinition Find(Agent_CharacterDefinition[] roster, string behaviorName)
        {
            if (roster == null || string.IsNullOrEmpty(behaviorName)) return null;
            for (int index = 0; index < roster.Length; index++)
            {
                if (roster[index] != null && roster[index].behaviorName == behaviorName) return roster[index];
            }
            return null;
        }

        /// Every named fighter in the save still exists in the roster. A save from
        /// before a fighter was renamed or removed cannot be resumed: its bracket
        /// would have a hole in it where a brain used to be.
        private static bool Resolves(SaveFile file, Agent_CharacterDefinition[] roster)
        {
            for (int slot = 0; slot < SEED_COUNT; slot++)
            {
                if (!string.IsNullOrEmpty(file.seeds[slot]) && Find(roster, file.seeds[slot]) == null) return false;
            }
            for (int match = 0; match < MATCH_COUNT; match++)
            {
                if (!string.IsNullOrEmpty(file.winners[match]) && Find(roster, file.winners[match]) == null) return false;
            }
            return true;
        }

        /// Is there a saved in-progress bracket this roster can resume? Reads the
        /// file and changes nothing in memory.
        public static bool TryPeekSaved(Agent_CharacterDefinition[] roster, out SavedSummary summary)
        {
            summary = default;
            SaveFile file = LoadSave();
            if (file == null || !Resolves(file, roster)) return false;

            int fighters = 0;
            for (int slot = 0; slot < SEED_COUNT; slot++)
            {
                if (!string.IsNullOrEmpty(file.seeds[slot])) fighters++;
            }
            int played = 0;
            for (int match = 0; match < MATCH_COUNT; match++)
            {
                if (file.outcomes[match] == OUTCOME_BOUT) played++;
            }

            int current = file.currentMatch;
            if (current < 4)
            {
                summary.FighterA = file.seeds[current * 2];
                summary.FighterB = file.seeds[current * 2 + 1];
            }
            else
            {
                int feederA = FeederOf(current);
                summary.FighterA = file.winners[feederA];
                summary.FighterB = file.winners[feederA + 1];
            }
            // A resumable bracket is parked on a real bout. Anything else is a
            // file this code did not write.
            if (string.IsNullOrEmpty(summary.FighterA) || string.IsNullOrEmpty(summary.FighterB)) return false;

            summary.BoutNumber = played + 1;
            summary.BoutCount = Mathf.Max(0, fighters - 1);
            return true;
        }

        /// Replace the in-memory bracket with the saved one and go live. Returns
        /// false — leaving memory untouched — when there is nothing resumable.
        public static bool TryResume(Agent_CharacterDefinition[] roster)
        {
            if (!TryPeekSaved(roster, out _)) return false;
            SaveFile file = LoadSave();
            if (file == null) return false;

            for (int slot = 0; slot < SEED_COUNT; slot++) _seeds[slot] = Find(roster, file.seeds[slot]);
            for (int match = 0; match < MATCH_COUNT; match++)
            {
                _winners[match] = Find(roster, file.winners[match]);
                _outcomes[match] = file.outcomes[match];
            }
            CurrentMatch = file.currentMatch;
            _seeded = true;
            Active = true;
            return true;
        }
    }
}
