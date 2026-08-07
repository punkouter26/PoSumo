using NUnit.Framework;
using PoSumo;
using UnityEngine;

namespace PoSumo.Tests
{
    /// `Reward_Context.San` is the NaN/Inf guard every value entering a reward
    /// passes through. A single NaN reaching `AddReward` poisons the whole
    /// advantage estimate for that trajectory, and the trainer does not error — it
    /// just learns nothing, which is the most expensive failure mode this project
    /// has. Cheap to pin down, so it is pinned down.
    public sealed class RewardContextTests
    {
        [Test]
        public void FiniteValuesPassThroughUnchanged()
        {
            Assert.AreEqual(0f, Reward_Context.San(0f));
            Assert.AreEqual(1.25f, Reward_Context.San(1.25f));
            Assert.AreEqual(-1.25f, Reward_Context.San(-1.25f));
            Assert.AreEqual(float.MaxValue, Reward_Context.San(float.MaxValue));
            Assert.AreEqual(float.MinValue, Reward_Context.San(float.MinValue));
        }

        [Test]
        public void NonFiniteValuesCollapseToZero()
        {
            Assert.AreEqual(0f, Reward_Context.San(float.NaN));
            Assert.AreEqual(0f, Reward_Context.San(float.PositiveInfinity));
            Assert.AreEqual(0f, Reward_Context.San(float.NegativeInfinity));
        }

        [Test]
        public void SanOutputIsAlwaysFinite()
        {
            float[] inputs =
            {
                float.NaN, float.PositiveInfinity, float.NegativeInfinity,
                0f, -0f, 1f, -1f, 1e30f, -1e30f, float.Epsilon,
            };
            for (int inputIndex = 0; inputIndex < inputs.Length; inputIndex++)
            {
                Assert.IsTrue(float.IsFinite(Reward_Context.San(inputs[inputIndex])),
                              $"San leaked a non-finite value for input {inputs[inputIndex]}");
            }
        }

        [Test]
        public void ContextPreservesEveryFieldItIsGiven()
        {
            // The constructor takes 14 positional arguments of which 10 are floats,
            // so a transposed pair is both easy to write and invisible at runtime —
            // it would present as a fighter that trains badly, not as an error.
            var context = new Reward_Context(
                facingSign: -1f,
                arenaGroundY: 0.5f,
                torsoPosition: new Vector2(1f, 2f),
                torsoVelocity: new Vector2(3f, 4f),
                lastTorsoY: 5f,
                upright: 0.6f,
                kneeBend: 0.7f,
                energy: 0.8f,
                effort: 0.9f,
                jerk: 0.11f,
                pendingImpact: 12f,
                isDown: true,
                hasOpponent: true,
                opponentX: 13f);

            Assert.AreEqual(-1f, context.FacingSign);
            Assert.AreEqual(0.5f, context.ArenaGroundY);
            Assert.AreEqual(new Vector2(1f, 2f), context.TorsoPosition);
            Assert.AreEqual(new Vector2(3f, 4f), context.TorsoVelocity);
            Assert.AreEqual(5f, context.LastTorsoY);
            Assert.AreEqual(0.6f, context.Upright);
            Assert.AreEqual(0.7f, context.KneeBend);
            Assert.AreEqual(0.8f, context.Energy);
            Assert.AreEqual(0.9f, context.Effort);
            Assert.AreEqual(0.11f, context.Jerk);
            Assert.AreEqual(12f, context.PendingImpact);
            Assert.IsTrue(context.IsDown);
            Assert.IsTrue(context.HasOpponent);
            Assert.AreEqual(13f, context.OpponentX);
        }
    }
}
