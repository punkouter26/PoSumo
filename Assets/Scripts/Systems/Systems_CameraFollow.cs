using UnityEngine;

namespace PoSumo
{
    /// Broadcast-style follow camera for portrait viewing: tracks the midpoint
    /// of both wrestlers and zooms as tight as possible while keeping both in
    /// frame (plus margin). Attach to the camera.
    [RequireComponent(typeof(Camera))]
    public sealed class Systems_CameraFollow : MonoBehaviour
    {
        // Pulled back 30% from the original 1.9 / 3.5 / 0.95: the tighter framing
        // read as a close-up on two bodies rather than a bout on a dohyo, and the
        // arena dressing (roof, crowd, banners) was mostly off-screen.
        // min 2.47: tightest zoom, still fits a full body (~1.85 m) with headroom.
        // max 4.55: in portrait the visible width is ortho * aspect (~0.56), so
        // anything tighter crops the wrestlers off-screen at spawn separation.
        // feetDrop scales with the zoom so the feet stay at the same screen height.
        // NOTE: GameTuning.asset (and scene-serialized values) win over these
        // defaults — change the asset, not just here.
        public float minOrtho = 2.47f;
        // Scaled with the ring when it doubled (4.55 -> 9.1), so the same
        // fraction of the mat stays visible at full separation. minOrtho is
        // deliberately unchanged: close-quarters framing, where most of the bout
        // happens, should look exactly as it did.
        public float maxOrtho = 9.1f;
        [Tooltip("Ortho for the wide establishing shot, used by both the walk-in and the post-match pull-back. Sized to hold the walk-in start marks: at portrait aspect ~0.462 this shows about +/-6.5 m, so fighters spawning at +/-6 are on screen from their first step.")]
        public float wideOrtho = 14f;
        // Camera centres this far below the average torso — roughly at the feet.
        // Lowered 1.24 -> 0.95 to reclaim dead screen. Fitting a 5.5 m ring into a
        // 0.46-aspect portrait viewport forces a ~3.7 m visible half-height for
        // maybe 2 m of actual content, and the clamp below pins the mat feetDrop
        // above the frame centre — so the larger this is, the higher the dohyo
        // rides and the more pure black sits under it. A smaller value trades that
        // black for the arena dressing above. It cannot go much lower without
        // pushing the roof and banners off the top, and cropping WIDTH instead
        // was already tried and reverted (see minOrtho/maxOrtho above).
        // MEASURED DEAD END, 2026-08-05 — do not retry this without reading first.
        // Roughly 27% of a portrait frame below the dohyo is pure black during a
        // bout, and lowering feetDrop looks like the obvious lever for it. It is
        // not. Dropping the asset value 0.95 -> 0.4 and capturing a live bout put
        // the fighters entirely OFF-SCREEN below the frame: the clamp further down
        // pins the mat relative to the frame centre, so shrinking this does not
        // slide the action down into the black, it slides the whole clamped frame
        // and takes the fighters with it. Reverted to 0.95.
        //
        // The dead space is structural, not a mistuning: portrait aspect is ~0.462,
        // so covering the +/-2.5 m opening stand-off needs ortho >= ~5.4, which buys
        // ~11 m of VERTICAL view for about 2 m of fighter. No camera value fixes
        // that. The space has to be FILLED (backdrop or HUD), not squeezed out.
        [Tooltip("Ortho the camera SNAPS to when a round opens, before the follow logic zooms it back in.\n\nSeparate from wideOrtho (14), which is the ceremony and post-match pull-back: that one frames the whole hall and is allowed to be extravagant because nothing is happening. This one opens a ROUND, so it has to show the arena without making the fighters unreadable — 9.5 holds the full 7 m mat plus the near crowd at portrait aspect.\n\nThe zoom back in is not scripted. This is only the STARTING value; the ordinary separation-driven target (halfDist + margin) / aspect takes over immediately and the existing smoothing eases between them, so the camera closes in exactly as fast as the fighters actually close on each other.")]
        public float establishOrtho = 9.5f;

        public float feetDrop = 0.95f;
        public float horizontalMargin = 0.5f;

        [Tooltip("How close (m) a fighter has to get to the CURRENT mat edge before the camera widens to keep that edge in frame. 0 restores the old pair-only framing exactly.\n\nRing-out is the only losing condition, so the rim is the thing a viewer needs to read — but framing the whole mat all round would throw away the tight follow. This buys the edge only when it is about to matter. Deliberately not on GameTuning: it is a framing detail of this component, and adding it to the asset would give it a second place to go stale.")]
        public float edgeAwareness = 1.1f;
        public float smoothing = 4f;

        // ---- impact shake ---------------------------------------------------
        //
        // Trauma-based rather than a fixed-duration wobble: impacts ADD trauma,
        // trauma decays on its own, and the offset is trauma SQUARED. Squaring is
        // what makes a big hit feel different in kind rather than merely bigger --
        // small traumas produce almost nothing, so the ordinary noise of two
        // ragdolls leaning on each other never shakes the camera.
        //
        // The offset is applied AFTER the follow lerp writes the position, so it
        // never feeds back into the smoothing and cannot accumulate drift.

        /// Peak positional offset in metres at full trauma.
        private const float SHAKE_MAX_OFFSET = 0.22f;
        /// Trauma lost per second. ~0.9 gives a hit a visible but short life.
        private const float TRAUMA_DECAY = 1.7f;
        /// Frequency of the noise walk. High enough to read as an impact, low
        /// enough not to alias into a buzz at 60 FPS.
        private const float SHAKE_FREQUENCY = 26f;

        private float _trauma;
        private float _shakeSeed;
        [Tooltip("Keep the dohyo in frame: the follow target is clamped to the ring plus this margin, so a fighter flung off the edge cannot drag the camera off the mat.")]
        public bool clampToRing = true;
        public float ringMargin = 0.9f;
        public Systems_GameTuning tuning;

        private Camera _cam;
        private Agent_Biped _a, _b;
        private Systems_GameMatchManager _manager;

        /// Seconds between fallback scans for the two fighters. See ResolveFighters.
        private const float FIGHTER_SCAN_INTERVAL = 0.25f;
        private float _nextFighterScan;

        // Temporary punch-in override (slow-mo finishes): blend toward a focus
        // transform at a tighter ortho until the realtime deadline passes.
        private Transform _focus;
        private float _focusOrtho;
        private float _focusUntil;
        private float _wideUntil;
        /// Blend rate while a shot is live; 0 means "use `smoothing`".
        ///
        /// Exists because `smoothing` is tuned for FOLLOWING — it has to ignore the
        /// fighters' frame-to-frame jitter, so it is slow on purpose. A ceremony
        /// beat is the opposite problem: a deliberate one-second move from the wide
        /// establishing shot (ortho 14) down to a head (0.85). At smoothing 4 that
        /// move is only ~half done when the beat expires, and the result reads as
        /// the camera drifting rather than punching in.
        private float _shotSmoothing;
        /// How hard a punch-in pulls the frame onto its focus, 0..1, per axis. The
        /// default 0.75 deliberately keeps some of the two-shot in view for a
        /// slow-mo finish; a face close-up needs 1 horizontally, or the residual
        /// quarter-pull toward the pair's midpoint slides the head out of frame.
        ///
        /// The two axes are separate because the countdown digit is drawn dead
        /// centre of the stage band: with vertical centring also at 1 the head
        /// lands exactly under it and the numeral is painted across the face. The
        /// base target is the fighters' feet, so anything below 1 here leaves the
        /// head riding high in frame with the digit clear of it.
        private float _focusCenteringX = FOCUS_CENTERING_DEFAULT;
        private float _focusCenteringY = FOCUS_CENTERING_DEFAULT;
        private const float FOCUS_CENTERING_DEFAULT = 0.75f;

        /// What to punch in on for a fighter: the head if it has drawn face art,
        /// otherwise the torso. Lives here rather than at each call site because
        /// three of them (round end, match end, countdown) had their own copy, so a
        /// change to what the camera favours only ever landed in one of the three.
        public static Transform FocusPoint(Agent_Biped fighter)
        {
            if (fighter == null) return null;
            var body = fighter.GetComponent<Agent_BipedBody>();
            if (body != null && body.HeadRenderer != null) return body.HeadRenderer.transform;
            return fighter.Torso != null ? fighter.Torso.transform : null;
        }

        /// Blend the camera toward `focus` at `ortho` for `realSeconds` of
        /// unscaled time. Used by the match presentation on round-deciding falls.
        ///
        /// `blendSpeed` 0 keeps the normal follow smoothing; pass a higher rate for
        /// a shot that has to ARRIVE inside its own duration rather than merely
        /// lean that way. `centeringX`/`centeringY` at 1 frame the focus alone on
        /// that axis. See the fields.
        public void PunchIn(Transform focus, float ortho, float realSeconds,
                            float blendSpeed = 0f,
                            float centeringX = FOCUS_CENTERING_DEFAULT,
                            float centeringY = FOCUS_CENTERING_DEFAULT)
        {
            _focus = focus;
            _focusOrtho = ortho;
            _focusUntil = Time.realtimeSinceStartup + realSeconds;
            _shotSmoothing = blendSpeed;
            _focusCenteringX = Mathf.Clamp01(centeringX);
            _focusCenteringY = Mathf.Clamp01(centeringY);
        }

        /// Hold a wide establishing shot of the whole arena, centred on the ring
        /// rather than on the fighters, for `realSeconds`.
        ///
        /// Separate from PunchIn because it has to beat the ring clamp and the
        /// fighter-following entirely: after a match the interesting subject is
        /// the arena, not the pair, and one of them is usually off the edge
        /// dragging the midpoint with him.
        public void PullBackWide(float realSeconds, float blendSpeed = 0f)
        {
            _wideUntil = Time.realtimeSinceStartup + realSeconds;
            _shotSmoothing = blendSpeed;
        }

        /// Cancels any active punch-in or wide shot and returns to normal follow.
        /// Opens a round on the whole arena, then lets the fighters pull the camera
        /// in as they close.
        ///
        /// Deliberately a SNAP of the current ortho rather than a timed override
        /// like PullBackWide. A timed override pins the camera for a fixed duration
        /// and then hands back, which is what produced the old opening: a hard hold
        /// at 14 for the entire walk-in followed by a jump to follow framing. By
        /// setting the starting value instead, the target for the very next frame is
        /// already the ordinary separation-driven one, so the move in is continuous
        /// and its SPEED is the speed the fighters actually approach at — which is
        /// what was asked for, and it costs no new state, no new mode in the
        /// FixedUpdate switch, and nothing to unwind if the round ends early.
        ///
        /// Called on every round open (Systems_GameMatchManager.StartCountdown), so
        /// rounds 2+ get it too. Those open at the stand-off with barely any gap to
        /// close, so there the snap plus smoothing reads as a short push-in rather
        /// than a long approach — which is the correct emphasis, since round 1 is
        /// the one with the ceremony.
        ///
        /// Safe to call while a shot is live: an active PunchIn or PullBackWide
        /// still wins on the very next frame, because both are applied after this
        /// value is read.
        public void BeginEstablishingShot()
        {
            if (_cam == null)
            {
                return;
            }
            _cam.orthographicSize = Mathf.Max(_cam.orthographicSize, establishOrtho);
        }

        public void ClearShots()
        {
            _focus = null;
            _focusUntil = 0f;
            _wideUntil = 0f;
            _shotSmoothing = 0f;
            _focusCenteringX = FOCUS_CENTERING_DEFAULT;
            _focusCenteringY = FOCUS_CENTERING_DEFAULT;
        }

        private void Awake()
        {
            _shakeSeed = Random.value * 100f;
            _cam = GetComponent<Camera>();
            if (tuning != null)
            {
                minOrtho = tuning.minOrtho;
                maxOrtho = tuning.maxOrtho;
                feetDrop = tuning.feetDrop;
                horizontalMargin = tuning.horizontalMargin;
                smoothing = tuning.smoothing;
                wideOrtho = tuning.wideOrtho;
                enableArenaBand = tuning.enableArenaBand;
                arenaBandBottom = tuning.arenaBandBottom;
                arenaBandTop = tuning.arenaBandTop;
            }

            ApplyArenaBand();
        }

        /// Confines the camera to a horizontal band, and spawns the clear camera
        /// that the band makes necessary.
        ///
        /// **Why this is the only lever that works.** `feetDrop`'s own note is
        /// right that no camera VALUE fixes the ~30% of every portrait frame that
        /// is black below the dohyo — but that conclusion assumed the camera owns
        /// the whole screen. It does not have to. `MaxOrthoForAspect` divides the
        /// ring half-width by `_cam.aspect`, and Unity recomputes `aspect` from
        /// the viewport rect, so narrowing the band vertically RAISES the aspect
        /// and lowers the ortho size needed to keep both fighters in frame. At
        /// 1080x1920 a 0.20-0.82 band takes the aspect from 0.563 to 0.907.
        ///
        /// **The clear camera is not optional.** The region outside a camera's
        /// rect is not drawn by that camera at all, so without a second camera
        /// clearing the full viewport the letterbox keeps whatever was in the
        /// backbuffer and smears as the scene moves. It culls everything
        /// (`cullingMask = 0`) and exists purely for its clear, and it sits one
        /// step BELOW the arena camera in depth so it clears first.
        private void ApplyArenaBand()
        {
            if (_cam == null)
            {
                return;
            }
            if (!enableArenaBand)
            {
                _cam.rect = new Rect(0f, 0f, 1f, 1f);
                return;
            }

            float bottom = Mathf.Clamp01(arenaBandBottom);
            float top = Mathf.Clamp01(arenaBandTop);
            if (top - bottom < 0.2f)
            {
                // A degenerate band would drive the ortho maths to absurd values
                // rather than erroring, so refuse it and keep the full frame.
                Debug.LogWarning("[CAMERA] Arena band is degenerate (" + bottom + ".." + top
                                 + ") — falling back to the full viewport.");
                _cam.rect = new Rect(0f, 0f, 1f, 1f);
                return;
            }

            _cam.rect = new Rect(0f, bottom, 1f, top - bottom);

            var clearHost = new GameObject("ArenaBandClear");
            clearHost.transform.SetParent(transform, false);
            var clearCam = clearHost.AddComponent<Camera>();
            clearCam.cullingMask = 0;
            clearCam.clearFlags = CameraClearFlags.SolidColor;
            clearCam.backgroundColor = Color.black;
            clearCam.orthographic = true;
            clearCam.depth = _cam.depth - 1f;
            clearCam.rect = new Rect(0f, 0f, 1f, 1f);
            // Nothing is rendered by this camera, so every one of these is pure
            // cost with no output. An AudioListener in particular would be a
            // SECOND listener in the scene, which Unity warns about and which
            // silently changes how everything is mixed.
            clearCam.useOcclusionCulling = false;
            clearCam.allowHDR = false;
            clearCam.allowMSAA = false;
        }

        /// Serialized copies of the tuning asset's band settings. As everywhere
        /// else in this project the ASSET wins at runtime; these are the fallback
        /// for a scene with no tuning asset assigned.
        [Header("Arena band")]
        public bool enableArenaBand = true;
        [Range(0f, 0.45f)] public float arenaBandBottom = 0.20f;
        [Range(0.55f, 1f)] public float arenaBandTop = 0.88f;

        /// Adds impact shake. `amount01` is clamped and ACCUMULATES, so a flurry
        /// shakes harder than one blow, but the squared falloff keeps it bounded.
        ///
        /// Public because `Systems_ImpactFx` and `Systems_BodyDamage` both call it;
        /// they are companions and may not reach each other, but the camera is a
        /// scene service rather than a companion.
        public void AddTrauma(float amount01)
        {
            _trauma = Mathf.Clamp01(_trauma + Mathf.Clamp01(amount01));
        }

        /// Current shake offset, decayed on the render clock. Returns Vector2.zero
        /// when there is no trauma, which is the overwhelmingly common case.
        private Vector2 ShakeOffset()
        {
            if (_trauma <= 0.0001f) return Vector2.zero;

            _trauma = Mathf.Max(0f, _trauma - TRAUMA_DECAY * Time.unscaledDeltaTime);

            // Squared: see the note on the constants above.
            float magnitude = SHAKE_MAX_OFFSET * _trauma * _trauma;
            float t = Time.unscaledTime * SHAKE_FREQUENCY;

            // Perlin rather than Random: a random offset per frame is a buzz, a
            // coherent noise walk is a shake. Two decorrelated lookups via the seed.
            float x = (Mathf.PerlinNoise(_shakeSeed, t) - 0.5f) * 2f;
            float y = (Mathf.PerlinNoise(_shakeSeed + 37.7f, t) - 0.5f) * 2f;
            return new Vector2(x, y) * magnitude;
        }

        private void LateUpdate()
        {
            // Resolved FIRST, because it is also where the fighters come from.
            if (_manager == null) _manager = FindAnyObjectByType<Systems_GameMatchManager>();

            if (_a == null || _b == null)
            {
                ResolveFighters();
                if (_a == null || _b == null) return;
            }

            float ax = _a.TorsoX, bx = _b.TorsoX;
            float mid = (ax + bx) * 0.5f;
            float halfDist = Mathf.Abs(ax - bx) * 0.5f;

            float halfWidthNeeded = halfDist + horizontalMargin;

            // KEEP THE RIM THAT DECIDES THE ROUND ON SCREEN.
            //
            // Ring-out is the only losing condition in this game, and the mat
            // closes under the fighters as the round runs — so the single most
            // important thing in frame is how much clay is left behind whoever is
            // going backwards. The follow above frames only the PAIR: measured in
            // a live bracket at 1440x3088 it sat at ortho 2.47 (the minOrtho
            // floor), showing +/-1.69 m of a +/-3.5 m mat, with the edge a fighter
            // was two steps from entirely out of shot.
            //
            // This widens ONLY when someone is actually inside `edgeAwareness` of
            // an edge, so the tight follow in the middle of the mat — which is
            // most of a round, and the reason the follow is tight at all — is
            // untouched. It reads the LIVE half-width, not the full one: the whole
            // point is the edge that is currently there.
            if (_manager != null && edgeAwareness > 0f)
            {
                float ringCentreX = _manager.transform.position.x;
                float ringHalfNow = _manager.CurrentRingHalfWidth;
                float aOff = ax - ringCentreX;
                float bOff = bx - ringCentreX;
                // Whichever fighter is furthest from the centre is the one in
                // trouble, and his rim is the one worth showing.
                float lead = Mathf.Abs(aOff) >= Mathf.Abs(bOff) ? aOff : bOff;
                if (Mathf.Abs(lead) > ringHalfNow - edgeAwareness)
                {
                    float rimX = ringCentreX + Mathf.Sign(lead == 0f ? 1f : lead) * ringHalfNow;
                    halfWidthNeeded = Mathf.Max(halfWidthNeeded,
                                                Mathf.Abs(rimX - mid) + horizontalMargin);
                }
            }

            float orthoNeeded = halfWidthNeeded / _cam.aspect;
            float targetOrtho = Mathf.Clamp(orthoNeeded, minOrtho, MaxOrthoForAspect());

            // The wide shot outranks the punch-in: it is what the punch-in hands
            // over to, so if both are somehow live the pull-back wins.
            bool wideActive = Time.realtimeSinceStartup < _wideUntil;
            bool focusActive = !wideActive && _focus != null && Time.realtimeSinceStartup < _focusUntil;
            if (focusActive) targetOrtho = Mathf.Min(targetOrtho, _focusOrtho);
            if (wideActive) targetOrtho = wideOrtho;

            // A live shot may override the follow smoothing; see _shotSmoothing.
            // Only while it IS live, so the return to normal follow after a shot
            // expires is at the usual unhurried rate.
            float blend = (wideActive || focusActive) && _shotSmoothing > 0f ? _shotSmoothing : smoothing;
            float t = 1f - Mathf.Exp(-blend * Time.deltaTime);
            _cam.orthographicSize = Mathf.Lerp(_cam.orthographicSize, targetOrtho, t);

            // Vertical center of the frame sits at the wrestlers' feet.
            float midY = (_a.Torso.position.y + _b.Torso.position.y) * 0.5f - feetDrop;

            if (focusActive)
            {
                mid = Mathf.Lerp(mid, _focus.position.x, _focusCenteringX);
                midY = Mathf.Lerp(midY, _focus.position.y, _focusCenteringY);
            }

            // The wide shot frames the ARENA, so it overrides the follow target
            // outright — after a match one fighter is usually off the edge, and
            // following the pair's midpoint would sit the establishing shot
            // somewhere off the side of the dohyo.
            if (wideActive && _manager != null)
            {
                mid = _manager.transform.position.x;
                midY = _manager.transform.position.y;
            }

            // Clamp LAST, after the punch-in blend: otherwise a slow-mo zoom on a
            // loser who has fallen off the edge still drags the camera clear of
            // the mat, which is exactly how the dohyo ended up out of frame.
            if (clampToRing && !wideActive)
            {
                if (_manager != null)
                {
                    float centerX = _manager.transform.position.x;
                    float matY = _manager.transform.position.y;
                    float limit = _manager.ringHalfWidth + ringMargin;
                    mid = Mathf.Clamp(mid, centerX - limit, centerX + limit);
                    midY = Mathf.Max(midY, matY - feetDrop);   // dohyo stays on screen
                }
            }

            var p = transform.position;
            // Shake is added AFTER the lerp so it never feeds back into smoothing.
            Vector2 shake = ShakeOffset();
            transform.position = new Vector3(Mathf.Lerp(p.x, mid, t) + shake.x,
                                             Mathf.Lerp(p.y, midY, t) + shake.y,
                                             p.z);
        }

        /// The zoom cap, floored at whatever it actually takes to hold two fighters
        /// on opposite rims at THIS aspect ratio.
        ///
        /// `maxOrtho` is a serialized number and was the single point of failure
        /// here. `orthographicSize` is a VERTICAL half-height, so the visible width
        /// is `ortho * aspect` and a portrait viewport divides the cap by ~0.36-0.46
        /// before it buys any width at all. Measured 2026-08-07 with the asset at 7:
        /// the camera showed 5.06 m of a 7 m mat and clamped there, with one fighter
        /// 0.71 m off the left edge and the other 0.61 m off the right — mid-round,
        /// not during a punch-in. The code default was already 9.1, raised when the
        /// ring grew; `GameTuning.asset` still held the pre-ring-change 7 and the
        /// asset wins at runtime, so the fix never reached the game.
        ///
        /// Deriving the floor removes that whole class of drift: change the ring,
        /// change the margin, or run on a taller phone, and the cap follows. It is a
        /// FLOOR, not a replacement — a larger authored `maxOrtho` still wins, so
        /// this can only ever open the framing up, never tighten it.
        private float MaxOrthoForAspect()
        {
            // The LIVE half-width, not the full one. This is a floor under the cap
            // — it exists so a wide enough mat can never be un-framable — and the
            // mat the camera has to be able to hold is the one that is there now.
            // With maxOrtho at 9.1 this has never actually bound at any aspect the
            // game runs at; reading the live value keeps it honest if it ever does.
            float ringHalf = _manager != null ? _manager.CurrentRingHalfWidth
                           : tuning != null ? tuning.ringHalfWidth
                           : 3.5f;
            float aspect = Mathf.Max(0.01f, _cam.aspect);
            return Mathf.Max(maxOrtho, (ringHalf + horizontalMargin) / aspect);
        }

        /// Both fighters, preferred from the referee and only then by scanning.
        ///
        /// The scan is throttled because `FindObjectsByType` ALLOCATES an array and
        /// this sits in LateUpdate: before the throttle it ran every frame for as
        /// long as either fighter was unresolved, which is every frame of every
        /// scene load into an arena.
        private void ResolveFighters()
        {
            if (_manager != null)
            {
                if (_manager.wrestlerA != null) _a = _manager.wrestlerA;
                if (_manager.wrestlerB != null) _b = _manager.wrestlerB;
                if (_a != null && _b != null) return;
            }

            if (Time.unscaledTime < _nextFighterScan) return;
            _nextFighterScan = Time.unscaledTime + FIGHTER_SCAN_INTERVAL;

            var agents = FindObjectsByType<Agent_Biped>(FindObjectsInactive.Exclude);
            if (agents.Length >= 2) { _a = agents[0]; _b = agents[1]; }
        }
    }
}
