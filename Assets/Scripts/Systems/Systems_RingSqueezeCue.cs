using UnityEngine;

namespace PoSumo
{
    /// Makes the closing mat VISIBLE at the rim.
    ///
    /// **Why this exists.** Measured over 17 logged rounds of a live bracket, 16
    /// ran past `shrinkStartSeconds` (8 s) and every one of the 17 ended in a
    /// ring-out, mean round 18.0 s, with the loser's final X clustered at −2.4 to
    /// −3.5 against a mat that had already closed ~40%. In other words the
    /// contraction — not a push — is what decides almost every round, and nothing
    /// on screen said so. A viewer saw two fighters milling about until one
    /// inexplicably fell off the edge.
    ///
    /// The fix is legibility, not a rule change. The mat is allowed to be the
    /// referee as long as the player can watch it happen: `Systems_FightHud`'s MAT
    /// meter gives the number, and this gives the thing itself — two bands that
    /// stand on the live edge of the clay and burn brighter as it closes in.
    ///
    /// Deliberately READ-ONLY with respect to the fight. It moves no body, ends no
    /// round and writes nothing anyone else reads, so it has no equivalent in
    /// `Systems_SumoMatchManager` and no brain can meet a rule it never trained
    /// against — the same standing as every other presentation companion.
    ///
    /// Spawned by `Systems_GameMatchManager` behind `enableRingSqueezeCue`.
    public sealed class Systems_RingSqueezeCue : MonoBehaviour
    {
        [Tooltip("Width (m) of the danger band drawn standing on each edge of the mat.")]
        public float bandWidth = 0.22f;
        [Tooltip("Height (m) the band rises above the mat surface. Short on purpose: this marks the floor line, it is not a wall, and a tall band would draw across the fighters' legs at the exact moment they are worth watching.")]
        public float bandHeight = 0.5f;
        [Tooltip("Opacity the bands hold while the mat is still at its full width. Low, because at that point they are only orientation — the edge is not yet news.")]
        [Range(0f, 1f)] public float restOpacity = 0.1f;
        [Tooltip("Opacity at a fully closed mat.")]
        [Range(0f, 1f)] public float peakOpacity = 0.62f;
        [Tooltip("Pulses per second once the mat is closing. 0 disables the pulse and leaves a steady band.")]
        public float pulseHz = 1.4f;

        [Header("Squeeze dust")]
        [Tooltip("Clay dust puffs per second, per rim, at a fully closed mat. The contraction decides almost every round, so the EDGE should shed clay like the active thing it is, not sit there as a painted band. Rate scales with how closed the mat is; 0 disables.")]
        public float squeezeDustHz = 5f;
        [Tooltip("Dust puffs only once the mat is at least this far closed (0-1), or the opening stand-off smokes for nothing.")]
        [Range(0f, 1f)] public float squeezeDustOnset = 0.12f;

        [Tooltip("Sorting order for the bands. Above the arena dressing and below the fighters (whose parts run 0-3 and whose head is 4), so a band never draws over a body.")]
        public int sortingOrder = -1;

        private static readonly Color Safe = new Color(0.95f, 0.78f, 0.3f);
        private static readonly Color Danger = new Color(0.95f, 0.3f, 0.22f);

        private Systems_GameMatchManager _manager;
        private Systems_SumoArena _arena;
        private SpriteRenderer _left;
        private SpriteRenderer _right;
        private static Sprite _edgeFade;
        private float _lastClosed = -1f;
        private float _dustDebt;

        /// A 1-px-tall horizontal gradient: opaque at the outer edge, transparent
        /// inward. Drawn per side and mirrored, so the band sits ON the rim and
        /// dissolves toward the middle of the mat instead of ending in a hard line
        /// the eye reads as a second edge.
        private static Sprite EdgeFade()
        {
            if (_edgeFade != null) return _edgeFade;
            const int W = 64;
            var tex = new Texture2D(W, 1, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            for (int columnIndex = 0; columnIndex < W; columnIndex++)
            {
                float t = columnIndex / (float)(W - 1);   // 0 inner -> 1 outer
                tex.SetPixel(columnIndex, 0, new Color(1f, 1f, 1f, t * t));
            }
            tex.Apply();
            _edgeFade = Sprite.Create(tex, new Rect(0f, 0f, W, 1f), new Vector2(0.5f, 0f), W);
            return _edgeFade;
        }

        private void Start()
        {
            _manager = GetComponentInParent<Systems_GameMatchManager>();
            if (_manager == null) _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            _arena = FindAnyObjectByType<Systems_SumoArena>();
            if (_manager == null)
            {
                enabled = false;
                return;
            }

            _left = BuildBand("SqueezeBandLeft", -1f);
            _right = BuildBand("SqueezeBandRight", 1f);
        }

        private SpriteRenderer BuildBand(string objectName, float side)
        {
            var go = new GameObject(objectName);
            go.transform.SetParent(transform, false);
            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = EdgeFade();
            renderer.sortingOrder = sortingOrder;
            // The gradient runs transparent-to-opaque left-to-right, so the RIGHT
            // band uses it as authored and the LEFT one is mirrored on x.
            go.transform.localScale = new Vector3(bandWidth * side, bandHeight, 1f);
            return renderer;
        }

        private void LateUpdate()
        {
            if (_left == null || _right == null) return;

            float full = Mathf.Max(0.01f, _manager.ringHalfWidth);
            float half = _manager.CurrentRingHalfWidth;
            // 0 at the opening width, 1 when the clay is gone. This is the same
            // quantity the HUD's MAT meter prints, so the two cues cannot disagree.
            float closed01 = Mathf.Clamp01(1f - half / full);

            float alpha = Mathf.Lerp(restOpacity, peakOpacity, closed01);
            if (pulseHz > 0f && closed01 > 0.02f)
            {
                // Realtime, not game time: this is a visual and must not stall or
                // stretch with the slow-motion finish, exactly like every other
                // presentation timer in this project.
                float pulse = 0.5f + 0.5f * Mathf.Sin(Time.realtimeSinceStartup * pulseHz * Mathf.PI * 2f);
                alpha *= Mathf.Lerp(1f, 0.55f + 0.45f * pulse, closed01);
            }

            Color tint = Color.Lerp(Safe, Danger, closed01);
            tint.a = alpha;
            _left.color = tint;
            _right.color = tint;

            // Sit ON the live mat surface. The arena is the authority on where the
            // clay actually is — the manager only knows the number it asked for —
            // so the y comes from the arena when there is one.
            float centreX = _manager.transform.position.x;
            float topY = _arena != null ? _arena.transform.position.y : _manager.transform.position.y;
            float z = _manager.transform.position.z;
            _left.transform.position = new Vector3(centreX - half, topY, z);
            _right.transform.position = new Vector3(centreX + half, topY, z);

            EmitSqueezeDust(closed01, centreX, half, topY, z);
        }

        /// Clay shedding off the two rims while the mat contracts. Rate-limited on
        /// the REALTIME clock (a slow-mo finish must not spray dust in slow motion)
        /// and gated on the closure ACTUALLY INCREASING, so a round parked at the
        /// stand-off emits nothing. Emission goes through Systems_DustPuff.Burst,
        /// which is the shared static every dust caller uses — no second particle
        /// system, no second texture, no new draw-call cost beyond the puffs
        /// themselves.
        private void EmitSqueezeDust(float closed01, float centreX, float half, float topY, float z)
        {
            if (squeezeDustHz <= 0f || closed01 < squeezeDustOnset)
            {
                _lastClosed = closed01;
                return;
            }

            // Only closing mat sheds. `closed01` is monotonic through a round, but
            // the walk-in widens the ring first, so guard the direction anyway.
            bool closing = _lastClosed >= 0f && closed01 > _lastClosed;
            _lastClosed = closed01;
            if (!closing)
            {
                return;
            }

            // Debt accumulator: carries fractional puffs across frames so the rate
            // is exact without a per-frame allocation or a coroutine.
            float strength = (closed01 - squeezeDustOnset) / (1f - squeezeDustOnset);
            _dustDebt += squeezeDustHz * strength * Time.unscaledDeltaTime;
            while (_dustDebt >= 1f)
            {
                _dustDebt -= 1f;
                int count = 2 + Mathf.RoundToInt(3f * strength);
                Vector3 position = new Vector3(
                    centreX + (Random.value < 0.5f ? -half : half) + Random.Range(-0.1f, 0.1f),
                    topY + Random.Range(0f, 0.05f),
                    z);
                Systems_DustPuff.Burst(position, count);
            }
        }
    }
}
