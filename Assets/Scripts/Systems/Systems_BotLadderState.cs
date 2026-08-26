using UnityEngine;

namespace PoSumo
{
    /// The BOT LADDER (2026-08-26): pick a trained fighter, beat the rules-based
    /// `Bot_v01` at EASY, then MEDIUM, then HARD. Each rung unlocks the next and the
    /// highest rung beaten is remembered per fighter, so it is the instant win path
    /// for a new player before the bracket.
    ///
    /// Static like `Systems_TournamentState`, for the same reason: it has to
    /// outlive the `LoadScene` that plays the bout. `Active` is cleared on
    /// `SubsystemRegistration` (domain reload is off); the per-fighter progress is
    /// in `PlayerPrefs`, not in `career.json`, because a ladder bout is UNRATED —
    /// `Systems_CareerRecorder.NameOf` already returns null for the brainless Bot,
    /// so nothing here touches Elo, W/L or the banzuke.
    ///
    /// Difficulty is one number: the Bot's whole-body torque multiplier
    /// (`Agent_BipedBody.torqueMultiplier`), applied by `Systems_MatchRoster` before
    /// the joints are built. It changes how hard the Bot pushes, not how it thinks.
    public static class Systems_BotLadderState
    {
        public const int TIER_COUNT = 3;
        public static readonly string[] TierNames = { "EASY", "MEDIUM", "HARD" };
        /// Bot torque per tier. 0.7 loses to every shipped brain most of the time;
        /// 1.3 is above any trained fighter's `torqueScale` (Kim, the heaviest, is
        /// the strongest at 1.0 × 1.45 mass).
        public static readonly float[] TierTorque = { 0.7f, 1.0f, 1.3f };

        private const string PREF_PREFIX = "ladder.";

        public static bool Active { get; private set; }
        public static int Tier { get; private set; }
        public static Agent_CharacterDefinition Challenger { get; private set; }
        public static Agent_CharacterDefinition Bot { get; private set; }

        /// The last decided ladder bout, held for exactly one reader (the bracket
        /// screen's news line). Consume-once like the recorder's RankChange.
        public readonly struct Result
        {
            public readonly string Challenger;
            public readonly int Tier;
            public readonly bool Won;
            public Result(string challenger, int tier, bool won)
            {
                Challenger = challenger;
                Tier = tier;
                Won = won;
            }
        }

        private static Result _pending;
        private static bool _hasPending;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ClearOnPlaySessionStart()
        {
            Active = false;
            Tier = 0;
            Challenger = null;
            Bot = null;
            _pending = default;
            _hasPending = false;
        }

        /// Highest tier this fighter has beaten, 0..TIER_COUNT (0 = none yet).
        public static int RungsBeaten(string behaviorName) =>
            string.IsNullOrEmpty(behaviorName) ? 0
                : Mathf.Clamp(PlayerPrefs.GetInt(PREF_PREFIX + behaviorName, 0), 0, TIER_COUNT);

        /// A tier is open once every tier below it has been beaten.
        public static bool IsUnlocked(string behaviorName, int tier) =>
            tier >= 0 && tier < TIER_COUNT && RungsBeaten(behaviorName) >= tier;

        public static void Begin(Agent_CharacterDefinition challenger, Agent_CharacterDefinition bot, int tier)
        {
            Challenger = challenger;
            Bot = bot;
            Tier = Mathf.Clamp(tier, 0, TIER_COUNT - 1);
            Active = true;
        }

        /// Called by Systems_BotLadderReporter when the bout is decided.
        public static void Report(bool challengerWon)
        {
            if (!Active) return;
            string name = Challenger != null ? Challenger.behaviorName : null;
            if (challengerWon && name != null && RungsBeaten(name) < Tier + 1)
            {
                PlayerPrefs.SetInt(PREF_PREFIX + name, Tier + 1);
                PlayerPrefs.Save();
            }
            _pending = new Result(name, Tier, challengerWon);
            _hasPending = true;
            Active = false;
        }

        public static bool TryTakeResult(out Result result)
        {
            result = _pending;
            bool had = _hasPending;
            _hasPending = false;
            _pending = default;
            return had;
        }

        public static void Stop() => Active = false;
    }
}
