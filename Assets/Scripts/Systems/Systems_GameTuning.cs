using UnityEngine;

namespace PoSumo
{
    /// Single source of truth for camera and match tuning. Scene components
    /// copy from this asset at startup when it is assigned, so numbers live in
    /// one place instead of being scattered across serialized scene values.
    [CreateAssetMenu(fileName = "GameTuning", menuName = "PoSumo/Game Tuning")]
    public sealed class Systems_GameTuning : ScriptableObject
    {
        [Header("Camera")]
        public float minOrtho = 1.9f;
        public float maxOrtho = 3.5f;
        [Tooltip("Camera centers this far below the wrestlers' average torso — at the feet.")]
        public float feetDrop = 0.95f;
        public float horizontalMargin = 0.5f;
        public float smoothing = 4f;
        [Tooltip("Ortho for the wide establishing shot used by the walk-in and the post-match pull-back. ~14 holds the +/-6 m walk-in start marks at portrait aspect.")]
        public float wideOrtho = 14f;


        [Header("Ring")]
        // The ring lives here rather than only on the arena scene because it is
        // serialized into it, and a public field already written into a scene
        // ignores any later change to its code default — the trap this asset
        // exists to avoid.
        [Tooltip("Half-width of the FIGHTING ring in metres. This is also the physical platform half-width and is fed to the agents as an edge-distance observation, so changing it changes what every brain sees.\n\n4.0, down from 5.5. The 5.5 ring was the documented reason bouts stopped finishing: with the fighters starting 0.9 m apart there was 4.6 m of mat to drive an opponent across, and measured sustained push in contact was only 71-500 N against 614 N of static friction. Shrinking the ring and widening the stand-off cuts that distance to about 1.5 m.")]
        public float ringHalfWidth = 4f;

        [Tooltip("Half the gap the fighters stand at when a round opens. 2.5 (was 0.9): starting them near the tawara rather than nose-to-nose in the middle means a drive of ~1.5 m wins the round instead of ~4.6 m. Costs nothing in retraining — it is a spawn position, not an observation.")]
        public float neutralGapHalf = 2.5f;

        [Tooltip("Friction of the dohyo clay in the GAME. 0.55, down from 0.9.\n\nAt 0.9 the force to start sliding a 69.6 kg opponent is 0.9*69.6*9.81 = 614 N, and measured sustained push in contact was 71-500 N — so no fighter could move another at all. 0.55 drops the wall to ~375 N, which the harder pushes clear.\n\nSafe without retraining: Systems_SumoMatchManager already randomises friction over [0.5, 1.1] during training, so 0.55 is inside the distribution every brain has seen.")]
        public float surfaceFriction = 0.55f;

        [Tooltip("Width in metres of the low-friction tawara band at each rim. A fighter driven onto the bales loses grip and slides out instead of planting — which is what the bales physically do. 0 disables the band.\n\nSystems_SumoMatchManager carries its own copy and now writes it onto the training arena too (2026-08-15), so keep the two equal. The asset ships 1.2; this code default is the old 0.7 and only applies when no tuning asset is assigned.")]
        public float tawaraBandWidth = 0.7f;

        [Tooltip("Friction inside the tawara band. Deliberately slick so 'almost out' becomes 'out'.")]
        public float tawaraFriction = 0.18f;

        [Tooltip("Seconds into a round before the ring starts closing in. 0 disables the shrinking ring entirely.\n\nMEASURED REASON: a 14-round tournament finished 57% on a timeout decision and 7% on a timeout draw — 64% settled by the clock — against a single ring-out. The [ROUND] logs show why: at the bell the fighters are bunched within ~2 m of the centre, so nobody is anywhere near an edge. They lock up in the middle and grind. A shrinking ring converts a stalemate into the sport's own win condition instead of handing it to a judge.\n\nThe machinery is not new: Systems_SumoArena.SetPlatformHalfWidth already resizes the platform collider and the walk-in has always used it. Ring-out detection needs no change either, because OutOfRing is purely vertical — it fires when a foot drops below the mat surface, so withdrawing the floor is detected for free.")]
        public float shrinkStartSeconds = 8f;

        [Tooltip("Half-width the ring closes to by the time the round clock expires. Reached by a linear contraction from ringHalfWidth starting at shrinkStartSeconds.\n\n1.8 m leaves a mat about two body-widths across at the bell: tight enough that a grapple in the middle runs out of floor, wide enough that it is still wrestling rather than a coin toss. Set equal to ringHalfWidth to disable the contraction while keeping the timer.")]
        public float shrinkToHalfWidth = 1.8f;
        [Tooltip("Seconds the contraction takes from ringHalfWidth to shrinkToHalfWidth. It used to be implied by roundTimeoutSeconds - shrinkStartSeconds (20 - 8 = 12); it has its own number now because the clock can be OFF. The mat does not stop at shrinkToHalfWidth — it keeps closing at the same rate to zero, so a round with no clock still ends: at the defaults the floor is gone ~33 s in.")]
        public float shrinkSeconds = 12f;
        [Tooltip("Play the ceremonial walk-in at the start of each match. Lives here because the arena scenes each serialize their own copy of the flag, and SCN_SUMO had it switched OFF — so the ceremony was silently never running no matter what the code default said.")]
        public bool enableWalkIn = true;
        [Tooltip("Platform half-width during the ceremonial walk-in. The mat contracts to ringHalfWidth before the bell.")]
        public float walkInHalfWidth = 8f;
        [Tooltip("Half the gap the fighters start the walk-in from. Was 3 (6 m apart) and the walk-in then FAILED ON EVERY BOUT — see walkInTouchGap. 1.8 gives 3.6 m, which the measured gait closes comfortably.")]
        // 3 -> 1.8 on 2026-08-16. The previous note here reasoned that 3 was already
        // the cautious value because 6 had run past 9 s; it was still too far. Logged
        // failures read `STALLED after 3.3s, surfaceGap=5.05, bestGap=4.87` and
        // `timed out after 7.0s, surfaceGap=0.80, bestGap=0.69`.
        public float walkInStartGapHalf = 1.8f;
        [Tooltip("Surface gap in metres between the two fighters' colliders at which they count as having met — this is the moment the policy flips from its walk task to its fight task. Measured body-to-body rather than torso-to-torso, because limb pose swings torso-separation-at-contact between about 0.9 m and 1.8 m.")]
        // 0.05 -> 0.6 on 2026-08-16, with the start gap above.
        //
        // 0.05 demanded literal contact and NO BOUT EVER ACHIEVED IT. Every match
        // logged a stall or a timeout, and the referee fell back to parking the pair
        // at the stand-off — so the ceremony this whole code path exists for was
        // never once seen. The gait's measured floor is bestGap 0.69 m; a threshold
        // an order of magnitude below what the walker can actually reach is not a
        // strict setting, it is an unreachable one.
        //
        // 0.6 is a CEREMONY threshold, not a physics one. Nothing downstream needs
        // the colliders to touch — it only decides when the task flag flips — and at
        // 0.6 m of surface separation the two men read as having met in the middle.
        // Fixing the gait itself is a training problem with four failed runs behind
        // it (see CLAUDE.md); this makes the presentation work with the gait we have.
        public float walkInTouchGap = 0.6f;
        [Tooltip("Hard cap on the walk-in, in seconds. 30 since 2026-08-26 (was 9): a pair that is still edging together is left to edge together, and on the cap — or after 4 s without 8 cm of new closing — the fight brains simply take over where they stand. Nothing is parked or re-counted any more.")]
        public float walkInTimeout = 30f;

        [Header("Match")]
        [Tooltip("Round wins needed in a standalone exhibition match.")]
        public int pointsToWin = 3;
        [Tooltip("Round wins needed to take a tournament bracket. 2 = best of three. Single source for both modes so they cannot drift apart.")]
        public int tournamentPointsToWin = 2;
        [Tooltip("Round clock. 0 = NO CLOCK: no timeout decision, no draw, the clock label stays blank, and the shrinking mat (see shrinkSeconds) is what ends a stalemate — every round finishes on a ring-out. Set to 0 on 2026-08-26; a bracket had just gone 3 timeout decisions in 8 rounds.")]
        public float roundTimeoutSeconds = 0f;
        public float betweenRoundsPause = 2.5f;
        public float graceSeconds = 0.4f;
        // downGraceSeconds, downOutSeconds, knockdownLoses and headTouchLoses were
        // DELETED 2026-08-26 from this asset and both referees. The ring-out is
        // the only losing condition; the shrinking mat (shrinkSeconds) is what
        // ends a stalemate, squeezing a fighter who stays down off the edge.
        [Tooltip("A fighter that loses all four limbs to a single blow loses the round instantly.\n\nGAME-ONLY: Systems_SumoMatchManager has no equivalent, so no brain has trained against it. Safe for the same reason the rest are — the victim is a limbless torso and could not have continued.\n\nTurning this OFF does not let a gibbed fighter keep going: the shrinking mat squeezes it off the edge instead. The only difference is the wait.\n\nHow often it fires is set on Systems_BodyDamage (gibSpeed / gibChance), not here.")]
        public bool gibLosesRound = true;
        [Tooltip("THE RING-OUT RULE (2026-08-26). ON: a fighter is out when ANY body part touches the arena floor below the dohyo — a static contact more than 0.3 m under the mat top (Sensor_FloorContact on every part, feet and head included). Feet dipping over the edge no longer end the round by themselves — they cut the motors so the fall plays out (see GoLimp) — and the torso backstop is 2 m below the floor. OFF: the old rule, a foot below footOffMatY.\n\nIt was 'the HEAD only' for one afternoon and a bracket stalled forever on two limp fighters resting on each other with neither head down; any-part was chosen to replace it.\n\nBoth referees carry this; Systems_SumoMatchManager.ringOutOnFloorContact must stay equal.")]
        [UnityEngine.Serialization.FormerlySerializedAs("ringOutOnHeadFloor")]
        public bool ringOutOnFloorContact = true;
        [Tooltip("Head knockouts one fighter can suffer before losing the whole match on the spot — boxing's three-knockdown rule. 0 disables it. GAME-ONLY: Systems_SumoMatchManager has no equivalent, so the brains never train against it; it is a spectacle rule layered on top of the sumo rules, not one of them.")]
        public int knockoutsToLoseMatch = 3;
        [Tooltip("Realtime seconds between the deciding knockout and the result card. Must outlast Systems_MatchPresentation.koSlowMoRealSeconds or the card cuts off the slow-motion replay of the hit that ended it.")]
        public float knockoutAnnounceSeconds = 2.2f;

        // Which runtime companions the match manager spawns.
        //
        // These were per-scene booleans on Systems_GameMatchManager, which meant
        // every arena scene carried its own serialized copy — and a public field
        // already written into a scene IGNORES any later change to its code
        // default, so "I flipped the default and nothing happened" was a recurring
        // trap. Here there is exactly one copy for every arena. (There were three
        // arena scenes when this was written; SCN_SUMO_ICE and SCN_SUMO_STICKY
        // were deleted 2026-07-28 and SCN_SUMO is now the only one — which makes
        // the asset less necessary and no less correct.)
        [Header("Arena band")]
        [Tooltip("Confine the arena camera to a horizontal BAND of the screen instead of letting it own the whole frame.\n\nPortrait leaves roughly 30% of every frame as black nothing below the dohyo, and no camera VALUE fixes it - minOrtho, feetDrop and the rest only shuffle the dead space around, because they all work inside a fixed 9:16 viewport. The band changes the aspect the orthographic maths divides by, which is the one lever that changes the trade instead of moving it.\n\nThis is a RENDERING change, not just a framing one: the region outside a camera rect is not drawn by that camera, so Systems_CameraFollow also spawns an ArenaBandClear camera whose only job is to clear it. Without that the outside keeps the previous frame and smears.")]
        public bool enableArenaBand = true;
        // OFF after being measured on 2026-09-05, and the measurement is the point.
        // The band DOES do what it promises to the maths: at 1440x3088 it took the
        // aspect 0.466 -> 0.752 and the fighters rendered far larger. It still looked
        // WORSE, because the arena has nothing to put in the space it opens up. The
        // band is filled by backdrop grid and the empty crowd wall, and the pull-back
        // shots (wideOrtho 14, the establishing shot at 9.5) do not shrink with the
        // aspect, so a wide beat shows MORE emptiness than it did before, not less.
        // Turning this on is therefore blocked on arena dressing that reaches the
        // band edges - not on anything in this file.
        [Tooltip("Bottom edge of the band as a fraction of screen height.")]
        [Range(0f, 0.45f)] public float arenaBandBottom = 0.20f;
        [Tooltip("Top edge of the band as a fraction of screen height. The gap above the band is where the score and the round banner live, so this is deliberately short of 1.")]
        [Range(0.55f, 1f)] public float arenaBandTop = 0.88f;

        [Header("Presentation companions")]
        [Tooltip("Slow-mo finishes, camera punch-in, salt throw.")]
        public bool enablePresentation = true;
        [Tooltip("Impact audio, crowd, ceremony.")]
        public bool enableAudio = true;
        [Tooltip("Per-fighter expression driven by dominance.")]
        public bool enableFaceMood = true;
        [Tooltip("Per-fighter spoken lines. Fighters with no recorded clips stay silent.")]
        public bool enableVoice = true;
        [Tooltip("2D light rig plus the post-processing volume.\n\nMUST STAY ON. The full rig (key, rims, global, volumetrics) has been on since 2026-08-25 and the old LightingEffects / FlatBodyShading switches on Systems_ArenaLighting were deleted — but every sprite in the arena uses a LIT material, and with no Light2D at all they render solid black. This switch decides whether there is a rig at all.")]
        public bool enableLighting = true;
        [Tooltip("Dust and sweat bursts scaled by hit strength.")]
        public bool enableImpactFx = true;
        [Tooltip("Backdrop parallax, haze tinting, crowd sway, light shafts.")]
        public bool enableAtmosphere = true;
        [Tooltip("Adaptive layered score.")]
        public bool enableMusic = true;
        [Tooltip("Bruise decals where a fighter is hit, plus the bloody head KO. Presentation only — no referee reads it.")]
        public bool enableBodyDamage = true;
        [Tooltip("Punches and kicks drive the man they land on backwards, and a good one launches him. GAME-ONLY: the training referee has no equivalent, so no brain has fought against it. Turning this on changes who wins rounds — a launched fighter can be knocked clean off the mat.")]
        public bool enableStrikeImpulse = true;

        [Tooltip("Announce the winning technique (kimarite) after every round. Read-only with respect to the fight — it names the finish, it does not decide it.")]
        public bool enableKimarite = true;

        [Tooltip("The crowd backs whoever is losing, and sustained support grants a small torque boost. THIS CHANGES WHO WINS ROUNDS and the training referee has no equivalent, so no brain has trained against it — game-only, like knockoutsToLoseMatch.")]
        public bool enableCrowdMomentum = true;

        [Tooltip("Real-time diagnostic overlay: frame time, FPS, GC delta, physics step and per-fighter stamina. Development aid — turn OFF for a release build.")]
        public bool enablePerfHud = true;

        [Tooltip("Expanding shock rings on the heaviest moments - a head KO, a limb coming off, and body-on-body contact hard enough to count as a slam. Presentation only: it reads outcomes and changes nothing a referee sees, so it is safe in a bracket and needs no retraining.\n\nEvery other VFX in the game is particles (Systems_DustPuff builds six systems), so the biggest moments all read as more of the same small stuff. One ring is the cheapest way to say that one was different.")]
        public bool enableShockwave = true;

        [Tooltip("Device haptics on the big moments, plus camera shake on the discrete events that produced none (head KO, dismemberment, gib, round end). Presentation only — it reads outcomes and changes nothing a referee sees, so it is safe in a bracket and needs no retraining.\n\nHaptics are Android-only and additionally respect the player's own switch (Systems_FeelFx.HapticsEnabled, persisted in PlayerPrefs). This flag decides whether the system exists at all.")]
        public bool enableFeelFx = true;
    }
}
