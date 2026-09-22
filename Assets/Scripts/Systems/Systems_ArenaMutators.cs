using UnityEngine;

namespace PoSumo
{
    /// Telegraphed arena mutators: once a round, with ~3 seconds of warning, the
    /// arena itself turns against both fighters.
    ///
    ///   SALT    — a slick patch appears on the clay. Telegraphed as a pulsing
    ///             patch, then its low-friction collider goes live.
    ///   CRUMBLE — the tawara gives: the shrinking mat briefly closes at several
    ///             times its usual rate, through `AccelerateShrink` on the
    ///             manager so the agents' edge observations keep tracking the
    ///             real edge (PublishRingHalfWidth is already on that path).
    ///
    /// GAME-ONLY, and deliberately so — the same class as
    /// `enableStrikeImpulse` and `knockoutsToLoseMatch`. The training referee
    /// has no equivalent, so no brain has ever trained against a mutator;
    /// porting one into `Systems_SumoMatchManager` would mean teaching the
    /// policy a rule it can neither observe nor predict, which is exactly how a
    /// shaping exploit gets invented. It changes who wins rounds; that is the
    /// point, and it is why the flag defaults live next to those other
    /// spectacle rules.
    ///
    /// Cross-companion signalling goes through the `Telegraphed` STATIC event —
    /// the `Sensor_Impact.AnyImpact` pattern — so `Systems_Caster` can announce
    /// a mutator without either component holding a reference to the other.
    /// Statics outlive scene loads, so the delegate clears itself below, and
    /// this component unsubscribes nothing here (it is the PUBLISHER).
    public sealed class Systems_ArenaMutators : MonoBehaviour
    {
        /// Fired with a spectator-readable line the moment a mutator is
        /// telegraphed. Subscribe in OnEnable, unsubscribe in OnDisable.
        public static event System.Action<string> Telegraphed;

        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Telegraphed = null;
        }

        // ---- Tuning (consts, the SoftBodyJiggle/TournamentBracket pattern: these
        // must not be overridable per scene, so they cannot go stale silently) --
        private const float CHANCE_PER_ROUND = 0.45f;
        private const float TELEGRAPH_DELAY = 6f;
        private const float TELEGRAPH_SECONDS = 3f;
        private const float SALT_FRICTION = 0.08f;
        private const float SALT_PATCH_HALF = 0.7f;
        private const float SALT_MAT_SHARE = 0.35f;   // where on the mat, as a share of half-width
        private const float CRUMBLE_EXTRA_RATE = 0.22f; // m/s of EXTRA contraction
        private const float CRUMBLE_SECONDS = 6f;

        private Systems_GameMatchManager _manager;

        private bool _scheduled;
        private bool _isSalt;
        private bool _announced;
        private bool _fired;
        private float _telegraphAt, _fireAt;

        private Transform _patch;
        private SpriteRenderer _patchRenderer;
        private BoxCollider2D _patchCollider;
        private PhysicsMaterial2D _saltMaterial;
        private float _patchX;

        private bool _subscribed;

        private void Awake()
        {
            _manager = GetComponentInParent<Systems_GameMatchManager>();
        }

        private void OnEnable()
        {
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
            DespawnPatch();
        }

        private void Subscribe()
        {
            if (_manager == null || _subscribed) return;
            _manager.RoundStarted += OnRoundStarted;
            _manager.RoundEnded += OnRoundEnded;
            _manager.MatchReset += OnRoundEnded;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (_manager == null || !_subscribed) return;
            _manager.RoundStarted -= OnRoundStarted;
            _manager.RoundEnded -= OnRoundEnded;
            _manager.MatchReset -= OnRoundEnded;
            _subscribed = false;
        }

        private void OnRoundStarted()
        {
            DespawnPatch();
            _scheduled = Random.value < CHANCE_PER_ROUND;
            _announced = false;
            _fired = false;
            if (_scheduled)
            {
                _isSalt = Random.value < 0.5f;
                _telegraphAt = TELEGRAPH_DELAY;
                _fireAt = TELEGRAPH_DELAY + TELEGRAPH_SECONDS;
            }
        }

        private void OnRoundEnded(Agent_Biped winner, Agent_Biped loser)
        {
            DespawnPatch();
            _scheduled = false;
        }

        private void OnRoundEnded()
        {
            DespawnPatch();
            _scheduled = false;
        }

        private void Update()
        {
            if (_manager == null || !_manager.RoundActive) return;
            if (!_scheduled || _fired) return;

            float elapsed = _manager.RoundElapsed;

            if (!_announced && elapsed >= _telegraphAt)
            {
                _announced = true;
                string line = _isSalt ? "SALT ON THE CLAY - STAND CLEAR" : "THE TAWARA IS CRUMBLING";
                Telegraphed?.Invoke(line);
                Systems_Log.Info("[MUTATOR] telegraphed: " + line);
                if (_isSalt) SpawnPatch();
            }

            if (_announced && elapsed >= _fireAt)
            {
                _fired = true;
                if (_isSalt)
                {
                    if (_patchCollider != null) _patchCollider.enabled = true;
                }
                else
                {
                    _manager.AccelerateShrink(CRUMBLE_EXTRA_RATE, CRUMBLE_SECONDS);
                }
            }

            // A salt patch the mat has contracted past is debris hanging over the
            // arena floor — pull it the moment the edge passes it.
            if (_patch != null && _isSalt)
            {
                float edge = _manager.CurrentRingHalfWidth + 0.3f;
                if (Mathf.Abs(_patchX) > edge)
                {
                    DespawnPatch();
                }
            }
        }

        // ---- Salt patch -------------------------------------------------------

        private void SpawnPatch()
        {
            if (_patch != null) return;

            float half = Mathf.Max(1f, _manager.CurrentRingHalfWidth);
            float side = Random.value < 0.5f ? -1f : 1f;
            _patchX = side * (SALT_MAT_SHARE + Random.value * 0.25f) * half;

            var go = new GameObject("SaltPatch");
            go.transform.SetParent(_manager.transform, false);
            go.transform.localPosition = new Vector3(_patchX,
                0.02f,   // proud of the clay by a couple of centimetres
                0f);

            _patchRenderer = go.AddComponent<SpriteRenderer>();
            _patchRenderer.sprite = Agent_BipedBody.BoxSprite();
            _patchRenderer.color = new Color(0.92f, 0.96f, 1f, 0.28f);
            // Under every limb (the far arms sit at -3) and under the tawara, so
            // it reads as ON the clay rather than floating over the fighters.
            _patchRenderer.sortingOrder = -5;
            go.transform.localScale = new Vector3(SALT_PATCH_HALF * 2f, 0.10f, 1f);

            if (_saltMaterial == null)
            {
                _saltMaterial = new PhysicsMaterial2D("SaltPatch")
                {
                    friction = SALT_FRICTION,
                    bounciness = 0f,
                };
            }

            _patchCollider = go.AddComponent<BoxCollider2D>();
            _patchCollider.size = new Vector2(SALT_PATCH_HALF * 2f, 0.04f);
            _patchCollider.offset = new Vector2(0f, 0.01f);
            _patchCollider.sharedMaterial = _saltMaterial;
            // LIVE only after the telegraph — the warning IS the mechanic.
            _patchCollider.enabled = false;

            _patch = go.transform;
        }

        private void DespawnPatch()
        {
            if (_patch != null)
            {
                Destroy(_patch.gameObject);
                _patch = null;
                _patchRenderer = null;
                _patchCollider = null;
            }
        }

        /// Pulse the patch while it telegraphs, settle once live. Realtime clock:
        /// a slow-motion finish must not stretch the pulse.
        private void LateUpdate()
        {
            if (_patchRenderer == null) return;

            float shimmer;
            if (!_fired)
            {
                shimmer = 0.24f + 0.16f * Mathf.Sin(Time.unscaledTime * 7f);
            }
            else
            {
                shimmer = 0.34f + 0.05f * Mathf.Sin(Time.unscaledTime * 2.2f);
            }
            var c = _patchRenderer.color;
            c.a = shimmer;
            _patchRenderer.color = c;
        }
    }
}
