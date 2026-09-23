using UnityEngine;

namespace PoSumo
{
    // `partial`: the ROUND/MATCH DECISION half of the game referee — everything
    // that decides a bout — extracted from Systems_GameMatchManager.cs on
    // 2026-09-22 so the state machine can be read (and tuned) on its own:
    //
    //   * TickShrinkingRing      the mat closes; crumble mutator accumulates on top
    //   * OutOfRing/BelowFloor   the two ring-out tests (stepping-out + world-exit)
    //   * GoLimp                 cut a loser's motors so it flops off lifelessly
    //   * EndRound               THE funnel: scores, logs [ROUND], fires RoundEnded,
    //                            opens RoundEnded or ends the match
    //   * OnGibbed/OnKnockout    the two GAME-ONLY rules layered on the sumo rules
    //   * EndMatchByKnockout     three-knockdown match end
    //   * IsLimp                 announce-delay helper
    //
    // The phase FIELD and its transitions stay in the main file next to
    // Update/FixedUpdate; this file only contains what those transitions CALL to
    // decide a round or a match. `Systems_SumoMatchManager` is the training
    // twin — losing conditions changed here must change there too or the brains
    // never learn the new rule (see CLAUDE.md).
    //
    // No behaviour changed in the extraction: the bodies moved verbatim, and
    // they may freely touch private fields/methods of the manager (same class).
    public sealed partial class Systems_GameMatchManager
    {
        /// Closes the mat in as the round clock runs down, so a stalemate finishes
        /// the way sumo finishes instead of being handed to a judge.
        ///
        /// Nothing here detects anything: `OutOfRing` is purely vertical (a foot
        /// below the mat surface), and `SetPlatformHalfWidth` moves the real
        /// platform collider, so withdrawing the floor produces an ordinary
        /// RingOut through the existing path. That is why this is a dozen lines
        /// rather than a new rule.
        ///
        /// Deliberately starts LATE (shrinkStartSeconds, 8 of 20). The opening
        /// exchange should be fought on a full mat; the squeeze is what breaks a
        /// grapple that has gone nowhere, and starting it at t=0 would just make
        /// every round a scramble for the middle.
        private void TickShrinkingRing()
        {
            if (_arena == null || shrinkStartSeconds <= 0f) return;

            // The contraction has its own duration (shrinkSeconds) rather than
            // borrowing the clock's, because there may be no clock. Progress is
            // NOT clamped at 1: past shrinkToHalfWidth the mat keeps closing at the
            // same rate all the way to zero, so with the timeout off a round still
            // cannot last forever — at 3.5 -> 1.8 over 12 s that is ~0.14 m/s and
            // the floor is gone about 33 s in.
            float target = Systems_RingShrink.ShrinkTarget(ringHalfWidth, shrinkToHalfWidth,
                                                          _elapsed, shrinkStartSeconds, shrinkSeconds);

            // The crumble's extra closure ACCUMULATES rather than scaling the
            // rate, so the edge never moves backwards and the curve stays
            // continuous through the boost and past its end. Floored at 0, not
            // at a sliver: Systems_SumoArena.VANISH_HALF withdraws the platform
            // collider below 0.12 m, so the last centimetres of contraction end
            // the round physically instead of leaving a pillar two fighters can
            // clinch on top of forever.
            if (_crumbleLeft > 0f)
            {
                _crumbleLeft -= Time.fixedDeltaTime;
                _crumbleExtra += _crumbleRate * Time.fixedDeltaTime;
            }
            target = Mathf.Max(0f, target - _crumbleExtra);

            // 1 cm of hysteresis: below that the contraction is invisible and only
            // costs a collider rebuild.
            if (Mathf.Abs(target - _appliedHalfWidth) < 0.01f) return;
            _appliedHalfWidth = target;
            PublishRingHalfWidth(target);
            _arena.SetPlatformHalfWidth(target);
            // The slick rim has to travel with the edge, or the bales stay out at
            // the original radius and the contracted mat has full grip right up to
            // a cliff — which is the one thing the tawara exists to prevent.
            _arena.EnsureTawaraBands(target);
        }

        /// Cut a fighter's motors so it flops lifelessly off the edge instead of
        /// holding a rigid pose on the way down.
        private static void GoLimp(Agent_Biped fighter)
        {
            fighter.actionsEnabled = false;
            var body = fighter.GetComponent<Agent_BipedBody>();
            if (body != null) body.GoLimp();
        }

        /// Sumo's stepping-out rule: the moment either foot drops off the edge of
        /// the mat the bout is lost, without waiting for the body to tumble clear.
        /// A foot cannot get below the mat surface while still standing on it, so
        /// dipping under footOffMatY means it has gone over the edge.
        ///
        /// The torso/fallY test is kept as a backstop for a body that leaves the
        /// mat without a foot leading — thrown clear, or landing head-first.
        private bool OutOfRing(Agent_Biped w, Agent_BipedBody body)
        {
            if (body != null)
            {
                float matTop = transform.position.y;
                float limit = matTop + footOffMatY;
                // Only feet still joined to the body count. A leg torn off by
                // Systems_BodyDamage is debris — it gets shoved around and will
                // very likely slide off the edge, and losing the round because
                // your severed leg fell off the mat is the opposite of what the
                // blow that took it off earned.
                if (body.FootNear != null && body.FootNearAttached
                    && body.FootNear.position.y < limit) return true;
                if (body.FootFar != null && body.FootFarAttached
                    && body.FootFar.position.y < limit) return true;
            }
            return w.Torso.position.y < fallY;
        }

        /// Backstop for ringOutOnFloorContact: the torso is 2 m under the arena floor,
        /// i.e. the body left the world, not just the mat. fallY (-0.2) cannot be
        /// used here — the floor itself is ~0.6 m below the mat, so a fighter LYING
        /// on it would trip fallY before the head rule ever got its say.
        private bool BelowFloor(Agent_Biped w)
        {
            float floorTop = _arena != null && _arena.FloorCollider != null
                ? _arena.FloorCollider.bounds.max.y
                : transform.position.y - 1f;
            return w.Torso.position.y < floorTop - 2f;
        }

        private void EndRound(Agent_Biped roundWinner, RoundOutcome outcome,
                              string drawText, string winText = null)
        {
            LongestRound = Mathf.Max(LongestRound, _elapsed);

            // Every exit from a round passes through here, so this is the one place
            // that can honestly account for all of them. Distances from the centre
            // are included on purpose: a RingOut at 3.9m and a TimeoutDecision at
            // 0.2m tell you the ring is doing its job, whereas a run of timeouts
            // with both fighters parked near the middle says they never engaged.
            LastOutcome = outcome;
            _roundsLogged++;
            _outcomeTally[(int)outcome]++;
            float centre = transform.position.x;
            string winnerName = roundWinner == wrestlerA ? nameA
                              : roundWinner == wrestlerB ? nameB : "—";
            Systems_Log.Info($"[ROUND] {_roundsLogged} {outcome} winner={winnerName} " +
                      $"t={_elapsed:F1}s score={_scoreA}-{_scoreB} " +
                      $"aX={wrestlerA.TorsoX - centre:F2} bX={wrestlerB.TorsoX - centre:F2} " +
                      $"ko={_koA}-{_koB}");

            if (roundWinner == wrestlerA) _scoreA++;
            else if (roundWinner == wrestlerB) _scoreB++;
            UpdateScoreboard(roundWinner != null);

            // Clay dust where the loser went down / out.
            var loser = roundWinner == wrestlerA ? wrestlerB : roundWinner == wrestlerB ? wrestlerA : null;
            if (loser != null)
            {
                Systems_DustPuff.Burst(loser.Torso.position);

                // A RING-OUT is the real sumo win and used to look identical to a
                // knockdown: one puff where the body landed. It now also throws clay
                // OUTWARD off the rim the fighter crossed, which is the read that
                // tells you at a glance which of the two just happened.
                if (outcome == RoundOutcome.RingOut)
                {
                    // `centre` is already in scope from the round log above.
                    float side = Mathf.Sign(loser.TorsoX - centre);
                    if (Mathf.Approximately(side, 0f)) side = 1f;
                    // Torso is a Rigidbody2D, so its position has no z — take the
                    // depth from the manager, which is what everything else uses.
                    var rim = new Vector3(centre + side * ringHalfWidth,
                                          transform.position.y, transform.position.z);
                    // Outward and slightly up, so it reads as displaced clay rather
                    // than as a second landing puff.
                    Systems_DustPuff.SweatSpray(rim, new Vector2(side, 0.45f).normalized, 14);
                    Systems_DustPuff.Burst(rim, 22);
                }
            }

            bool matchOver = _scoreA >= pointsToWin || _scoreB >= pointsToWin;
            if (matchOver)
            {
                // Match decided: the loser goes fully limp and stays down, while
                // the winner keeps its brain running so it carries on moving
                // instead of freezing mid-pose over the body.
                Agent_Biped matchWinner = _scoreA > _scoreB ? wrestlerA : wrestlerB;
                Agent_Biped matchLoser = matchWinner == wrestlerA ? wrestlerB : wrestlerA;
                GoLimp(matchLoser);
                matchWinner.actionsEnabled = true;
            }
            else
            {
                // Between rounds both stop driving; whoever left the mat is
                // already fully limp from GoLimp in FixedUpdate, and the survivor
                // simply holds its stance until the next round resets them.
                wrestlerA.actionsEnabled = false;
                wrestlerB.actionsEnabled = false;
            }

            // A ragdoll flop deserves a beat before the result is declared.
            bool limpFlop = IsLimp(wrestlerA) || IsLimp(wrestlerB);
            long announceDelayMs = limpFlop ? (long)(limpBeforeAnnounce * 1000f) : 0L;

            _countdownLeft = 0f;
            HideCountdown();

            RoundEnded?.Invoke(roundWinner, loser);

            if (matchOver)
            {
                _phase = Phase.MatchOver;
                bool aWon = _scoreA > _scoreB;
                if (aWon) MatchWinsA++; else MatchWinsB++;
                // Cumulative across the whole session, not just this bout — a
                // single match is far too small a sample to say anything about
                // whether ring-outs happen, and the bracket builds a fresh manager
                // per bout so only a static tally can span them.
                Systems_Log.Info("[MATCH] " + OutcomeSummary());
                _hud.HideCentre(_banner);
                _resultTitle.text = $"{WrapName(aWon ? nameA : nameB, aWon ? colorA : colorB)} WINS";
                _resultScore.text = $"{Mathf.Max(_scoreA, _scoreB)} — {Mathf.Min(_scoreA, _scoreB)}";
                _resultCard.style.borderTopColor = aWon ? colorA : colorB;
                // One call now raises the card AND its backdrop; they were two
                // separately scheduled reveals that had to be kept in step. It
                // also freezes both fighters on the same frame.
                ShowResultCardAfter(announceDelayMs);
                // The running scorebug would otherwise sit above the result card
                // competing with it; the card already states the score.
                _scoreBugHide = HideAfter(_scoreBug, announceDelayMs);
                MatchEnded?.Invoke(aWon ? wrestlerA : wrestlerB);
                return;
            }

            _banner.text = roundWinner != null
                ? (winText ?? $"{WrapName(roundWinner == wrestlerA ? nameA : nameB, roundWinner == wrestlerA ? colorA : colorB)} SCORES!")
                : drawText;
            ShowCentreAfter(_banner, announceDelayMs);

            _phase = Phase.RoundEnded;
            // Extend the pause by the announce delay so the result stays on
            // screen just as long as it did before.
            _phaseLeft = betweenRoundsPause + announceDelayMs / 1000f;
        }

        /// Boxing's three-knockdown rule, layered on top of the sumo rules.
        ///
        /// A head knockout was otherwise pure spectacle: Systems_BodyDamage cuts
        /// the fighter's motors and Systems_MatchPresentation slows time, but the
        /// round was still only won by pushing the limp body out, so a clean head
        /// shot could cost the man who landed it nothing at all. Counting them
        /// gives the KO a consequence — take `knockoutsToLoseMatch` in one match
        /// and it ends there, wherever the bodies happen to be standing.
        ///
        /// Deliberately NOT mirrored into Systems_SumoMatchManager. The two
        /// referees are kept in step on the losing conditions a policy has to
        /// learn; this is not one of them. It can only end a MATCH, never a
        /// training episode, so no brain can meet a rule it never trained against.
        /// A fighter lost all four limbs to one blow — the round is over immediately.
        ///
        /// This is a GAME-ONLY rule with no equivalent in Systems_SumoMatchManager,
        /// like knockoutsToLoseMatch. No brain has trained against
        /// it, which is fine: it is spectacle layered on the sumo rules, and the
        /// victim could not have kept fighting anyway.
        ///
        /// Without it the existing down-out rule would still end the round about 3 s
        /// later — a limbless fighter can never satisfy the get-up condition — so
        /// this only removes the wait, and the fallback stays correct if gibLosesRound
        /// is ever turned off.
        private void OnGibbed(Agent_BipedBody victim, Vector3 point)
        {
            if (victim == null) return;

            Agent_Biped loser = victim == _bodyA ? wrestlerA
                              : victim == _bodyB ? wrestlerB : null;
            if (loser == null) return;

            // Recorded FIRST and unconditionally, before any early return. This flag
            // is what lets the FixedUpdate evaluation resolve the race described on
            // _gibbedA — if this callback loses it, the evaluation still knows a gib
            // happened and awards the round correctly instead of calling a draw.
            if (loser == wrestlerA) _gibbedA = true; else _gibbedB = true;

            if (!gibLosesRound) return;
            // Only Fighting can be ended. The blow that gibs often lands in the same
            // physics step as a ring-out, and a second EndRound during RoundEnded or
            // Grace would score the round twice.
            if (_hud == null || _phase != Phase.Fighting) return;

            Systems_Log.Info($"[MATCH] gib on {(loser == wrestlerA ? nameA : nameB)} — round over");
            EndRound(loser == wrestlerA ? wrestlerB : wrestlerA, RoundOutcome.Gibbed,
                     null, "TORN APART");
        }

        private void OnKnockout(Agent_BipedBody victim, Vector3 point)
        {
            if (victim == null || knockoutsToLoseMatch <= 0) return;
            // _hud null means Start has not built the UI yet; MatchOver means the
            // result card already owns the screen.
            if (_hud == null || _phase == Phase.MatchOver) return;

            Agent_Biped loser;
            int suffered;
            if (victim == _bodyA) { loser = wrestlerA; suffered = ++_koA; }
            else if (victim == _bodyB) { loser = wrestlerB; suffered = ++_koB; }
            else return;

            Systems_Log.Info($"[MATCH] knockout {suffered}/{knockoutsToLoseMatch} on " +
                      $"{(loser == wrestlerA ? nameA : nameB)}");
            if (suffered < knockoutsToLoseMatch) return;

            EndMatchByKnockout(loser);
        }

        /// Stops the match on the deciding knockout. The KO'd fighter loses no
        /// matter what the round score says — that is the whole point of the rule.
        private void EndMatchByKnockout(Agent_Biped loser)
        {
            Agent_Biped winner = loser == wrestlerA ? wrestlerB : wrestlerA;
            bool aWon = winner == wrestlerA;

            _phase = Phase.MatchOver;
            EndedByKnockout = true;
            if (aWon) MatchWinsA++; else MatchWinsB++;
            LongestRound = Mathf.Max(LongestRound, _elapsed);

            // The loser is already limp from Systems_BodyDamage; keep it that way
            // and let the winner carry on moving over the body.
            GoLimp(loser);
            winner.actionsEnabled = true;

            _countdownLeft = 0f;
            HideCountdown();
            _hud.HideCentre(_banner);

            _resultTitle.text = $"{WrapName(aWon ? nameA : nameB, aWon ? colorA : colorB)} WINS BY KO";
            _resultScore.text = $"{(aWon ? _koB : _koA)} knockouts · rounds {_scoreA}–{_scoreB}";
            _resultCard.style.borderTopColor = aWon ? colorA : colorB;

            // The UI Toolkit scheduler runs on realtime, as does the presentation's
            // slow-motion timer, so this delay genuinely outlasts the KO replay
            // instead of being stretched along with it.
            long delayMs = (long)(Mathf.Max(0f, knockoutAnnounceSeconds) * 1000f);
            ShowResultCardAfter(delayMs);
            _scoreBugHide = HideAfter(_scoreBug, delayMs);

            // No RoundEnded is fired: no round was won. Raising it would hand the
            // career recorder a round result that never happened.
            MatchEnded?.Invoke(winner);
        }

        private static bool IsLimp(Agent_Biped fighter)
        {
            var body = fighter.GetComponent<Agent_BipedBody>();
            return body != null && body.IsLimp;
        }
    }
}
