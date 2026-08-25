using UnityEngine;

namespace PoSumo
{
    /// Classifies how a round was won into a named sumo winning technique
    /// (*kimarite*). Real sumo recognises 82 of them and the gyoji announces one
    /// after every bout; this maps the handful this game can actually distinguish.
    ///
    /// **Pure logic, deliberately.** No MonoBehaviour, no scene, no physics, no
    /// disk — it takes a struct of already-measured numbers and returns a struct.
    /// That makes it one of the very few things in this project that is testable in
    /// `Assets/Tests/EditMode` (see `Systems_CareerLadder` and `Reward_Context.San`,
    /// the only other two). `Systems_KimariteCaller` is the MonoBehaviour that
    /// gathers the input and shows the result; keep the split.
    ///
    /// The classification is deliberately CONSERVATIVE. Every branch that cannot be
    /// told apart from another falls back to the broad technique rather than
    /// guessing a specific one — announcing the wrong kimarite is worse than
    /// announcing a general one, because the whole point of the feature is that the
    /// caption explains what the player just watched.
    public static class Systems_Kimarite
    {
        /// Everything the classifier needs, measured at the moment the round ended.
        /// A readonly struct passed by `in` for the same reason `Reward_Context` is:
        /// this is called once per round, but the type sits next to code that runs
        /// at 50 Hz and the convention should not vary within the project.
        public readonly struct Input
        {
            /// How the referee ended the round.
            public readonly Systems_GameMatchManager.RoundOutcome Outcome;
            /// Loser's horizontal distance from the ring centre, in metres.
            public readonly float LoserEdgeDistance;
            /// Ring half-width at the moment of the finish (the mat shrinks).
            public readonly float RingHalfWidth;
            /// True when the loser's torso was below the fall line — i.e. down,
            /// rather than merely off the mat.
            public readonly bool LoserGrounded;
            /// Seconds since the winner last had a body part in contact with the
            /// loser. Large means the loser went out untouched.
            public readonly float SecondsSinceContact;
            /// True when the last contact came from the winner's ARMS (hands on the
            /// chest — a thrust) rather than the trunk (a chest-to-chest drive).
            public readonly bool LastContactWasArms;
            /// Peak relative speed of the winner's last contact, m/s. Distinguishes
            /// a shove from a strike.
            public readonly float LastContactSpeed;
            /// Winner's torso height above the mat at the finish. A winner who is
            /// LOW drove forward; one who is upright slapped down or stepped aside.
            public readonly float WinnerTorsoHeight;
            /// True when the loser fell toward the ring CENTRE rather than the rim —
            /// the signature of a pull-down or a side-step.
            public readonly bool LoserFellInward;

            public Input(Systems_GameMatchManager.RoundOutcome outcome,
                         float loserEdgeDistance, float ringHalfWidth,
                         bool loserGrounded, float secondsSinceContact,
                         bool lastContactWasArms, float lastContactSpeed,
                         float winnerTorsoHeight, bool loserFellInward)
            {
                Outcome = outcome;
                LoserEdgeDistance = loserEdgeDistance;
                RingHalfWidth = ringHalfWidth;
                LoserGrounded = loserGrounded;
                SecondsSinceContact = secondsSinceContact;
                LastContactWasArms = lastContactWasArms;
                LastContactSpeed = lastContactSpeed;
                WinnerTorsoHeight = winnerTorsoHeight;
                LoserFellInward = loserFellInward;
            }
        }

        /// One announced technique: the romaji name the gyoji calls, and a plain
        /// English gloss. Both are shown — the name is the flavour, the gloss is
        /// what makes the round legible to a player who has never watched sumo.
        public readonly struct Result
        {
            public readonly string Name;
            public readonly string Gloss;
            /// False for the two non-techniques (draw, and the gib novelty), which
            /// callers should announce differently or not at all.
            public readonly bool IsTechnique;

            public Result(string name, string gloss, bool isTechnique = true)
            {
                Name = name;
                Gloss = gloss;
                IsTechnique = isTechnique;
            }
        }

        /// Contact older than this counts as "no contact" — the loser went out or
        /// down under their own momentum. 0.35 s is about two decision periods at
        /// the shipped period of 3, so it survives a frame of missed collision
        /// without letting a genuine clinch read as untouched.
        private const float CONTACT_STALE_SECONDS = 0.35f;

        /// Above this relative speed a contact is a STRIKE rather than a shove.
        /// Calibrated against the same measured range `Systems_StrikeImpulse` uses
        /// (3.9-5.3 m/s) — a threshold above that range would never fire, which is
        /// the "term outside the observed distribution" trap that cost this project
        /// two walk-tall retrains and one dead strike-impulse curve.
        private const float STRIKE_SPEED = 4.0f;

        /// Torso height below which the winner counts as having driven in LOW.
        /// Standing pose is ~1.06 m; measured fighting crouch runs 0.55-0.80.
        private const float LOW_DRIVE_HEIGHT = 0.85f;

        /// Fraction of the ring half-width within which the loser counts as having
        /// gone out at the RIM rather than collapsed mid-mat.
        private const float RIM_FRACTION = 0.75f;

        /// Names the technique. Never returns null; the fallback is the broad
        /// force-out, which is also the most common finish in real sumo.
        public static Result Classify(in Input input)
        {
            switch (input.Outcome)
            {
                case Systems_GameMatchManager.RoundOutcome.TimeoutDraw:
                    return new Result("HIKIWAKE", "draw — no technique", false);

                case Systems_GameMatchManager.RoundOutcome.DoubleOut:
                    return new Result("HIKIWAKE", "both out together", false);

                case Systems_GameMatchManager.RoundOutcome.Gibbed:
                    return new Result("BARABARA", "torn apart", false);

                case Systems_GameMatchManager.RoundOutcome.TimeoutDecision:
                    // Not a kimarite at all in real sumo — there are no decisions.
                    // Named honestly rather than dressed up as a technique.
                    return new Result("YUSHO-KETTEI", "decided on position", false);

                case Systems_GameMatchManager.RoundOutcome.Knockdown:
                case Systems_GameMatchManager.RoundOutcome.DownOut:
                    return ClassifyDown(input);

                case Systems_GameMatchManager.RoundOutcome.RingOut:
                default:
                    return ClassifyOut(input);
            }
        }

        /// The loser left the mat. The question is whether they were driven, thrust,
        /// or simply blundered out.
        private static Result ClassifyOut(in Input input)
        {
            bool nearRim = input.LoserEdgeDistance >= input.RingHalfWidth * RIM_FRACTION;

            // Untouched: nobody put them there.
            if (input.SecondsSinceContact > CONTACT_STALE_SECONDS)
            {
                return nearRim
                    ? new Result("ISAMIASHI", "stepped out on his own")
                    : new Result("JIBASON", "lost the ring unaided");
            }

            if (input.LastContactWasArms)
            {
                // Hands on the chest. Fast is a thrust-out, measured is a push-out.
                return input.LastContactSpeed >= STRIKE_SPEED
                    ? new Result("TSUKIDASHI", "thrust out with the hands")
                    : new Result("OSHIDASHI", "pushed out");
            }

            // Trunk-to-trunk. A low winner drove; an upright one just leaned.
            return input.WinnerTorsoHeight <= LOW_DRIVE_HEIGHT
                ? new Result("YORIKIRI", "drove him out chest to chest")
                : new Result("OSHIDASHI", "pushed out");
        }

        /// The loser went to the clay inside the ring.
        private static Result ClassifyDown(in Input input)
        {
            if (input.SecondsSinceContact > CONTACT_STALE_SECONDS)
            {
                return new Result("TSUKIHIZA", "went down unaided");
            }

            // Falling INWARD after contact is the signature of a pull or a slap
            // down: the winner removed the resistance the loser was leaning on.
            if (input.LoserFellInward)
            {
                return input.LastContactWasArms
                    ? new Result("HATAKIKOMI", "slapped down")
                    : new Result("HIKIOTOSHI", "pulled down");
            }

            if (input.LastContactSpeed >= STRIKE_SPEED)
            {
                return new Result("TSUKITAOSHI", "thrust down");
            }

            // Driven over backwards while still engaged.
            return input.WinnerTorsoHeight <= LOW_DRIVE_HEIGHT
                ? new Result("YORITAOSHI", "forced down chest to chest")
                : new Result("OSHITAOSHI", "pushed over");
        }
    }
}
