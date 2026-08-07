using NUnit.Framework;
using PoSumo;

namespace PoSumo.Tests
{
    /// EditMode tests for the banzuke.
    ///
    /// `Systems_CareerLadder` is the one piece of real game logic in this project
    /// that is genuinely unit-testable: pure functions over a plain `Record`, no
    /// MonoBehaviour, no physics, no disk. Everything else worth checking is
    /// behavioural and belongs in `MatchTestHarness.Run(n)`.
    ///
    /// Nothing here touches `Systems_CareerStats`' static store — every record is
    /// constructed locally. Calling `Get`/`RecordMatch` would load and then REWRITE
    /// `career.json` in `Application.persistentDataPath`, i.e. a test run would
    /// silently destroy the player's real career.
    public sealed class CareerLadderTests
    {
        /// A record the ladder will actually rank. `IndexFor` returns UNRANKED
        /// while `matchWins + matchLosses == 0`, so a decided match is the price of
        /// entry for every rating assertion below.
        private static Systems_CareerStats.Record Rated(float elo, int titles = 0)
        {
            return new Systems_CareerStats.Record
            {
                fighter = "Test",
                elo = elo,
                titles = titles,
                matchWins = 1,
            };
        }

        // ---- The ladder itself -----------------------------------------------

        [Test]
        public void RungFloorsAscendStrictly()
        {
            // The bands fan out from 1000 and the walk in IndexFor is a downward
            // scan, which is only correct on a sorted ladder.
            for (int rungIndex = 1; rungIndex < Systems_CareerLadder.RungCount; rungIndex++)
            {
                Assert.Less(Systems_CareerLadder.RungAt(rungIndex - 1).EloFloor,
                            Systems_CareerLadder.RungAt(rungIndex).EloFloor,
                            $"rung {rungIndex} does not sit above rung {rungIndex - 1}");
            }
        }

        [Test]
        public void RungAtClampsOutOfRange()
        {
            Assert.AreEqual(Systems_CareerLadder.RungAt(0).Name,
                            Systems_CareerLadder.RungAt(-5).Name);
            Assert.AreEqual(Systems_CareerLadder.RungAt(Systems_CareerLadder.RungCount - 1).Name,
                            Systems_CareerLadder.RungAt(999).Name);
        }

        // ---- UNRANKED --------------------------------------------------------

        [Test]
        public void NoDecidedMatchIsUnranked()
        {
            var record = new Systems_CareerStats.Record { fighter = "Test", elo = 1000f };
            Assert.AreEqual(Systems_CareerLadder.UNRANKED, Systems_CareerLadder.IndexFor(record));
            Assert.AreEqual("UNRANKED", Systems_CareerLadder.NameFor(record));
        }

        [Test]
        public void NullRecordIsUnranked()
        {
            Assert.AreEqual(Systems_CareerLadder.UNRANKED, Systems_CareerLadder.IndexFor(null));
            Assert.AreEqual("UNRANKED", Systems_CareerLadder.NameFor(null));
        }

        [Test]
        public void UnrankedProgressIsFiniteAndZero()
        {
            // Regression: the rung above UNRANKED is RUNGS[0], whose floor is
            // negative infinity, so the span came out -inf - -inf = NaN and the
            // career screen set a NaN percent bar width from it.
            var record = new Systems_CareerStats.Record { fighter = "Test", elo = 1000f };

            float progress = Systems_CareerLadder.ProgressToNext(record, out string requirement);

            Assert.IsFalse(float.IsNaN(progress), "progress must never be NaN — it drives a style width");
            Assert.AreEqual(0f, progress);
            Assert.IsNotNull(requirement, "an unranked fighter needs to be told what is missing");
        }

        [Test]
        public void UnrankedEloToNextIsNotNegative()
        {
            var record = new Systems_CareerStats.Record { fighter = "Test", elo = 1000f };
            Assert.GreaterOrEqual(Systems_CareerLadder.EloToNext(record), 0);
        }

        // ---- Rating placement ------------------------------------------------

        [Test]
        public void FreshFighterAtEloStartIsMakushita()
        {
            // Documented invariant: 1000 is rung 3 of 10 — room to fall three and
            // climb six, which is what makes early matches feel like they move
            // something.
            Assert.AreEqual("MAKUSHITA", Systems_CareerLadder.NameFor(Rated(1000f)));
            Assert.AreEqual(3, Systems_CareerLadder.IndexFor(Rated(1000f)));
        }

        [Test]
        public void FloorsAreInclusive()
        {
            Assert.AreEqual("JURYO", Systems_CareerLadder.NameFor(Rated(1005f)));
            Assert.AreEqual("MAKUSHITA", Systems_CareerLadder.NameFor(Rated(1004.99f)));
        }

        [Test]
        public void FarBelowTheLadderIsTheBottomRung()
        {
            Assert.AreEqual("JONOKUCHI", Systems_CareerLadder.NameFor(Rated(1f)));
            Assert.AreEqual(0, Systems_CareerLadder.IndexFor(Rated(1f)));
        }

        // ---- The title gate --------------------------------------------------

        [Test]
        public void TitleGateDoesNotStrandAHighRating()
        {
            // IndexFor walks DOWNWARD precisely so this lands on SEKIWAKE rather
            // than stopping at the first rung it fails.
            Assert.AreEqual("SEKIWAKE", Systems_CareerLadder.NameFor(Rated(1200f, titles: 0)));
        }

        [Test]
        public void OzekiRequiresATitle()
        {
            Assert.AreEqual("SEKIWAKE", Systems_CareerLadder.NameFor(Rated(1125f, titles: 0)));
            Assert.AreEqual("OZEKI", Systems_CareerLadder.NameFor(Rated(1125f, titles: 1)));
        }

        [Test]
        public void YokozunaRequiresTwoTitles()
        {
            Assert.AreEqual("OZEKI", Systems_CareerLadder.NameFor(Rated(1160f, titles: 1)));
            Assert.AreEqual("YOKOZUNA", Systems_CareerLadder.NameFor(Rated(1160f, titles: 2)));
        }

        [Test]
        public void TitlesAloneDoNotPromote()
        {
            // The gate is AND, not OR — a title does not carry a low rating.
            Assert.AreEqual("MAKUSHITA", Systems_CareerLadder.NameFor(Rated(1000f, titles: 5)));
        }

        // ---- Progress and the gap --------------------------------------------

        [Test]
        public void ProgressIsAlwaysFiniteAcrossTheWholeLadder()
        {
            for (float elo = 700f; elo <= 1400f; elo += 5f)
            {
                for (int titles = 0; titles <= 2; titles++)
                {
                    float progress = Systems_CareerLadder.ProgressToNext(Rated(elo, titles), out _);
                    Assert.IsFalse(float.IsNaN(progress), $"NaN at elo {elo}, titles {titles}");
                    Assert.GreaterOrEqual(progress, 0f);
                    Assert.LessOrEqual(progress, 1f);
                }
            }
        }

        [Test]
        public void TopOfTheLadderIsCompleteAndUnblocked()
        {
            // A Yokozuna is not working toward anything; a bar stuck at 0 would
            // read as a fighter who had stalled rather than one who had arrived.
            float progress = Systems_CareerLadder.ProgressToNext(Rated(1400f, titles: 3),
                                                                 out string requirement);
            Assert.AreEqual(1f, progress);
            Assert.IsNull(requirement);
            Assert.AreEqual(0, Systems_CareerLadder.EloToNext(Rated(1400f, titles: 3)));
        }

        [Test]
        public void ProgressRisesWithRatingInsideABand()
        {
            // MAKUSHITA spans 975 → 1005.
            float low = Systems_CareerLadder.ProgressToNext(Rated(980f), out _);
            float high = Systems_CareerLadder.ProgressToNext(Rated(1000f), out _);
            Assert.Less(low, high);
        }

        [Test]
        public void ATitleGateIsReportedAsTheRequirement()
        {
            // Rated past OZEKI's floor but with no tournament win: the caption must
            // say so, or the fighter is told they need 0 more Elo and never promotes.
            Systems_CareerLadder.ProgressToNext(Rated(1200f, titles: 0), out string requirement);
            Assert.IsNotNull(requirement);
            Assert.IsTrue(requirement.Contains("OZEKI"), $"unexpected requirement: {requirement}");
        }

        [Test]
        public void NoRequirementWhenRatingAloneWillDoIt()
        {
            Systems_CareerLadder.ProgressToNext(Rated(1000f), out string requirement);
            Assert.IsNull(requirement);
        }

        // ---- The bands must fit the roster that exists ----------------------

        [Test]
        public void TheMeasuredRosterOccupiesTheUpperLadder()
        {
            // Real ratings out of career.json after 238 matches, 2026-08-07. The
            // bands were re-spread against exactly these numbers; before that the
            // top four rungs of ten were unreachable and Nick, the leader, sat at
            // MAEGASHIRA with 1077. If a future roster drifts, re-measure and move
            // the bands — but never let the top of the ladder go unoccupied again.
            Assert.AreEqual("SEKIWAKE",   Systems_CareerLadder.NameFor(Rated(1077.2f, titles: 6)));
            Assert.AreEqual("JURYO",      Systems_CareerLadder.NameFor(Rated(1020.0f, titles: 5)));
            Assert.AreEqual("JURYO",      Systems_CareerLadder.NameFor(Rated(1017.9f, titles: 6)));
            Assert.AreEqual("MAKUSHITA",  Systems_CareerLadder.NameFor(Rated(984.2f,  titles: 7)));
            Assert.AreEqual("JONIDAN",    Systems_CareerLadder.NameFor(Rated(900.7f,  titles: 1)));
        }

        [Test]
        public void TheTopRungIsReachableFromTheObservedSpread()
        {
            // Max observed deviation from the 1000 mean is +77. YOKOZUNA must sit
            // beyond that (still an achievement) but inside what a closed four-
            // fighter pool can actually produce — not the +160 the old bands
            // assumed and no fighter ever approached.
            float yokozunaFloor = Systems_CareerLadder
                .RungAt(Systems_CareerLadder.RungCount - 1).EloFloor;
            Assert.Greater(yokozunaFloor, 1077f, "top rung must still be above the measured leader");
            Assert.Less(yokozunaFloor, 1160f, "top rung must be closer than the unreachable old floor");
        }

        [Test]
        public void EloToNextRoundsUpAndNeverGoesNegative()
        {
            // MAKUSHITA at 1000 → JURYO at 1005.
            Assert.AreEqual(5, Systems_CareerLadder.EloToNext(Rated(1000f)));
            Assert.AreEqual(1, Systems_CareerLadder.EloToNext(Rated(1004.2f)));
            // Rating already past the next floor but blocked by a title gate.
            Assert.AreEqual(0, Systems_CareerLadder.EloToNext(Rated(1200f, titles: 0)));
        }
    }
}
