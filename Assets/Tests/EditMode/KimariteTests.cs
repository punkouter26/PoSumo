using NUnit.Framework;
using PoSumo;

namespace PoSumo.Tests
{
    /// EditMode tests for the kimarite classifier.
    ///
    /// `Systems_Kimarite` was written as a pure static over a struct precisely so
    /// it could be tested here — it joins `Systems_CareerLadder` and
    /// `Reward_Context.San` as the only logic in this project that needs no
    /// MonoBehaviour, no physics step and no disk.
    ///
    /// What these tests DO cover: that a given set of measurements maps to the
    /// technique it should. What they do NOT and cannot cover: whether
    /// `Systems_KimariteCaller` measures those numbers correctly off a live
    /// ragdoll. That half is behavioural and is verified with a harness run, per
    /// `CLAUDE.md`. A green run here is not evidence the call is right on screen.
    public sealed class KimariteTests
    {
        private const float RING = 3.5f;

        /// A ring-out with every field at a neutral default; each test overrides
        /// only the field it is about, which keeps the intent of each case visible.
        private static Systems_Kimarite.Input Out(
            float edge = 3.4f,
            float sinceContact = 0.05f,
            bool arms = false,
            float speed = 2.0f,
            float winnerHeight = 0.7f,
            bool fellInward = false)
        {
            return new Systems_Kimarite.Input(
                Systems_GameMatchManager.RoundOutcome.RingOut,
                edge, RING, false, sinceContact, arms, speed, winnerHeight, fellInward);
        }

        private static Systems_Kimarite.Input Down(
            float sinceContact = 0.05f,
            bool arms = false,
            float speed = 2.0f,
            float winnerHeight = 0.7f,
            bool fellInward = false)
        {
            return new Systems_Kimarite.Input(
                Systems_GameMatchManager.RoundOutcome.DownOut,
                1.0f, RING, true, sinceContact, arms, speed, winnerHeight, fellInward);
        }

        // ---- ring-outs -------------------------------------------------------

        [Test]
        public void LowTrunkDrive_IsYorikiri()
        {
            var r = Systems_Kimarite.Classify(Out(arms: false, winnerHeight: 0.6f));
            Assert.AreEqual("YORIKIRI", r.Name);
            Assert.IsTrue(r.IsTechnique);
        }

        [Test]
        public void UprightTrunkContact_IsOshidashi()
        {
            var r = Systems_Kimarite.Classify(Out(arms: false, winnerHeight: 1.0f));
            Assert.AreEqual("OSHIDASHI", r.Name);
        }

        [Test]
        public void FastArmContact_IsTsukidashi()
        {
            var r = Systems_Kimarite.Classify(Out(arms: true, speed: 5.0f));
            Assert.AreEqual("TSUKIDASHI", r.Name);
        }

        [Test]
        public void SlowArmContact_IsOshidashi()
        {
            var r = Systems_Kimarite.Classify(Out(arms: true, speed: 1.5f));
            Assert.AreEqual("OSHIDASHI", r.Name);
        }

        /// The strike threshold is calibrated against the 3.9-5.3 m/s band
        /// `Systems_StrikeImpulse` measured. A threshold above that range would
        /// never fire — the same "term outside the observed distribution" failure
        /// that cost two walk-tall retrains — so pin it with a test.
        [Test]
        public void StrikeThreshold_SitsInsideMeasuredStrikeSpeeds()
        {
            Assert.AreEqual("OSHIDASHI",
                Systems_Kimarite.Classify(Out(arms: true, speed: 3.8f)).Name,
                "below the measured strike band should not read as a thrust");
            Assert.AreEqual("TSUKIDASHI",
                Systems_Kimarite.Classify(Out(arms: true, speed: 4.5f)).Name,
                "inside the measured strike band must read as a thrust");
        }

        [Test]
        public void OutAtRimWithoutContact_IsIsamiashi()
        {
            var r = Systems_Kimarite.Classify(Out(edge: 3.45f, sinceContact: 2f));
            Assert.AreEqual("ISAMIASHI", r.Name);
        }

        [Test]
        public void OutMidMatWithoutContact_IsJibason()
        {
            var r = Systems_Kimarite.Classify(Out(edge: 1.0f, sinceContact: 2f));
            Assert.AreEqual("JIBASON", r.Name);
        }

        // ---- knockdowns ------------------------------------------------------

        [Test]
        public void FallingInwardAfterArmContact_IsHatakikomi()
        {
            var r = Systems_Kimarite.Classify(Down(arms: true, fellInward: true));
            Assert.AreEqual("HATAKIKOMI", r.Name);
        }

        [Test]
        public void FallingInwardAfterTrunkContact_IsHikiotoshi()
        {
            var r = Systems_Kimarite.Classify(Down(arms: false, fellInward: true));
            Assert.AreEqual("HIKIOTOSHI", r.Name);
        }

        [Test]
        public void DrivenDownLow_IsYoritaoshi()
        {
            var r = Systems_Kimarite.Classify(Down(winnerHeight: 0.6f, speed: 2f));
            Assert.AreEqual("YORITAOSHI", r.Name);
        }

        [Test]
        public void DownWithoutContact_IsTsukihiza()
        {
            var r = Systems_Kimarite.Classify(Down(sinceContact: 5f));
            Assert.AreEqual("TSUKIHIZA", r.Name);
        }

        // ---- non-techniques --------------------------------------------------

        /// Draws and decisions are not kimarite in real sumo, and the classifier
        /// must not dress them up as one — `IsTechnique` is what the caller uses to
        /// pick the gold styling, so a false positive here reads on screen as the
        /// game claiming a technique nobody performed.
        [Test]
        public void DrawAndDecision_AreNotTechniques()
        {
            var draw = Systems_Kimarite.Classify(new Systems_Kimarite.Input(
                Systems_GameMatchManager.RoundOutcome.TimeoutDraw,
                0f, RING, false, 999f, false, 0f, 0f, false));
            Assert.IsFalse(draw.IsTechnique);

            var decision = Systems_Kimarite.Classify(new Systems_Kimarite.Input(
                Systems_GameMatchManager.RoundOutcome.TimeoutDecision,
                0.2f, RING, false, 1f, false, 0f, 0.9f, false));
            Assert.IsFalse(decision.IsTechnique);
        }

        /// Every outcome the referee can produce must classify to something with a
        /// non-empty name. This is the guard that matters most: `RoundOutcome` is
        /// an enum that has grown before (Gibbed and DownOut were both added
        /// later), and a new member would otherwise fall through to a blank banner
        /// with no error anywhere.
        [Test]
        public void EveryRoundOutcome_ProducesANamedResult()
        {
            foreach (Systems_GameMatchManager.RoundOutcome outcome in
                     System.Enum.GetValues(typeof(Systems_GameMatchManager.RoundOutcome)))
            {
                var r = Systems_Kimarite.Classify(new Systems_Kimarite.Input(
                    outcome, 2f, RING, false, 0.1f, false, 2f, 0.8f, false));
                Assert.IsFalse(string.IsNullOrEmpty(r.Name), $"{outcome} produced no name");
                Assert.IsFalse(string.IsNullOrEmpty(r.Gloss), $"{outcome} produced no gloss");
            }
        }
    }
}
