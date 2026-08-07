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
        public float feetDrop = 0.95f;
        public float horizontalMargin = 0.5f;
        public float smoothing = 4f;
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
        public void PunchIn(Transform focus, float ortho, float realSeconds)
        {
            _focus = focus;
            _focusOrtho = ortho;
            _focusUntil = Time.realtimeSinceStartup + realSeconds;
        }

        /// Hold a wide establishing shot of the whole arena, centred on the ring
        /// rather than on the fighters, for `realSeconds`.
        ///
        /// Separate from PunchIn because it has to beat the ring clamp and the
        /// fighter-following entirely: after a match the interesting subject is
        /// the arena, not the pair, and one of them is usually off the edge
        /// dragging the midpoint with him.
        public void PullBackWide(float realSeconds)
        {
            _wideUntil = Time.realtimeSinceStartup + realSeconds;
        }

        /// Cancels any active punch-in or wide shot and returns to normal follow.
        public void ClearShots()
        {
            _focus = null;
            _focusUntil = 0f;
            _wideUntil = 0f;
        }

        private void Awake()
        {
            _cam = GetComponent<Camera>();
            if (tuning != null)
            {
                minOrtho = tuning.minOrtho;
                maxOrtho = tuning.maxOrtho;
                feetDrop = tuning.feetDrop;
                horizontalMargin = tuning.horizontalMargin;
                smoothing = tuning.smoothing;
                wideOrtho = tuning.wideOrtho;
            }
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
            float orthoNeeded = halfWidthNeeded / _cam.aspect;
            float targetOrtho = Mathf.Clamp(orthoNeeded, minOrtho, MaxOrthoForAspect());

            // The wide shot outranks the punch-in: it is what the punch-in hands
            // over to, so if both are somehow live the pull-back wins.
            bool wideActive = Time.realtimeSinceStartup < _wideUntil;
            bool focusActive = !wideActive && _focus != null && Time.realtimeSinceStartup < _focusUntil;
            if (focusActive) targetOrtho = Mathf.Min(targetOrtho, _focusOrtho);
            if (wideActive) targetOrtho = wideOrtho;

            float t = 1f - Mathf.Exp(-smoothing * Time.deltaTime);
            _cam.orthographicSize = Mathf.Lerp(_cam.orthographicSize, targetOrtho, t);

            // Vertical center of the frame sits at the wrestlers' feet.
            float midY = (_a.Torso.position.y + _b.Torso.position.y) * 0.5f - feetDrop;

            if (focusActive)
            {
                mid = Mathf.Lerp(mid, _focus.position.x, 0.75f);
                midY = Mathf.Lerp(midY, _focus.position.y, 0.75f);
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
            transform.position = new Vector3(Mathf.Lerp(p.x, mid, t), Mathf.Lerp(p.y, midY, t), p.z);
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
            float ringHalf = _manager != null ? _manager.ringHalfWidth
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

            var agents = FindObjectsByType<Agent_Biped>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (agents.Length >= 2) { _a = agents[0]; _b = agents[1]; }
        }
    }
}
