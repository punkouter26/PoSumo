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
        // The ring lives here rather than only on the arena scenes because it is
        // serialized into all three of them, and a public field already written
        // into a scene ignores any later change to its code default — the trap
        // this asset exists to avoid.
        [Tooltip("Half-width of the FIGHTING ring in metres. This is also the physical platform half-width and is fed to the agents as an edge-distance observation, so changing it changes what every brain sees.")]
        public float ringHalfWidth = 5.5f;
        [Tooltip("Play the ceremonial walk-in at the start of each match. Lives here because the arena scenes each serialize their own copy of the flag, and SCN_SUMO had it switched OFF — so the ceremony was silently never running no matter what the code default said.")]
        public bool enableWalkIn = true;
        [Tooltip("Platform half-width during the ceremonial walk-in. The mat contracts to ringHalfWidth before the bell.")]
        public float walkInHalfWidth = 8f;
        [Tooltip("Half the gap the fighters start the walk-in from. They walk all the way to contact now, and the measured closing rate is only ~1 m/s for the PAIR, so this is 3 and not 6: from 6 m apart they meet in about 4 s. At 6 (12 m apart) the approach ran past 9 s and never finished.")]
        public float walkInStartGapHalf = 3f;
        [Tooltip("Surface gap in metres between the two fighters' colliders at which they count as having met — 0 is literal contact. This is the moment the walk brains hand off to the fight brains. Measured body-to-body rather than torso-to-torso, because limb pose swings torso-separation-at-contact between about 0.9 m and 1.8 m.")]
        public float walkInTouchGap = 0.05f;
        [Tooltip("Hard cap on the walk-in, in seconds. Must cover the full approach: roughly walkInStartGapHalf metres each at the measured ~1.4 m/s, plus margin for a stumble. On timeout the fighters are parked at the stand-off and the round opens with the normal countdown instead.")]
        public float walkInTimeout = 12f;

        [Header("Match")]
        [Tooltip("Round wins needed in a standalone exhibition match.")]
        public int pointsToWin = 3;
        [Tooltip("Round wins needed to take a tournament bracket. 2 = best of three. Single source for both modes so they cannot drift apart.")]
        public int tournamentPointsToWin = 2;
        public float roundTimeoutSeconds = 30f;
        public float betweenRoundsPause = 2.5f;
        public float graceSeconds = 0.4f;
        [Tooltip("Non-foot ground contact must persist this long to count as a throw-down.")]
        public float downGraceSeconds = 0.2f;
        [Tooltip("Must stay false and match Systems_SumoMatchManager. Falling is not a loss in either referee — if you change it, change it in BOTH or trained brains face a rule they never saw.")]
        public bool knockdownLoses = false;
        [Tooltip("Head knockouts one fighter can suffer before losing the whole match on the spot — boxing's three-knockdown rule. 0 disables it. GAME-ONLY: Systems_SumoMatchManager has no equivalent, so the brains never train against it; it is a spectacle rule layered on top of the sumo rules, not one of them.")]
        public int knockoutsToLoseMatch = 3;
        [Tooltip("Realtime seconds between the deciding knockout and the result card. Must outlast Systems_MatchPresentation.koSlowMoRealSeconds or the card cuts off the slow-motion replay of the hit that ended it.")]
        public float knockoutAnnounceSeconds = 2.2f;

        // Which runtime companions the match manager spawns.
        //
        // These were per-scene booleans on Systems_GameMatchManager, which meant
        // three arena scenes each carried their own serialized copy — and a public
        // field already written into a scene IGNORES any later change to its code
        // default, so "I flipped the default and nothing happened" was a recurring
        // trap. Here there is exactly one copy for every arena.
        [Header("Presentation companions")]
        [Tooltip("Slow-mo finishes, camera punch-in, salt throw.")]
        public bool enablePresentation = true;
        [Tooltip("Impact audio, crowd, ceremony.")]
        public bool enableAudio = true;
        [Tooltip("Per-fighter expression driven by dominance.")]
        public bool enableFaceMood = true;
        [Tooltip("Per-fighter spoken lines. Fighters with no recorded clips stay silent.")]
        public bool enableVoice = true;
        [Tooltip("2D light rig plus the post-processing volume.")]
        public bool enableLighting = true;
        [Tooltip("Dust and sweat bursts scaled by hit strength.")]
        public bool enableImpactFx = true;
        [Tooltip("Backdrop parallax, haze tinting, crowd sway, light shafts.")]
        public bool enableAtmosphere = true;
        [Tooltip("Adaptive layered score.")]
        public bool enableMusic = true;
        [Tooltip("Bruise decals where a fighter is hit, plus the bloody head KO. Presentation only — no referee reads it.")]
        public bool enableBodyDamage = true;
    }
}
