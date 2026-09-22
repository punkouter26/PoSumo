using UnityEngine;

namespace PoSumo
{
    /// The muscle cam: tints each limb by the stress of the joint that drives it,
    /// so the audience sees the failing knee two seconds before the collapse.
    ///
    /// Stress per joint = max(its accumulated FATIGUE, this step's LOAD), both of
    /// which the body already maintains — `JointFatigue` is the array
    /// `IntegrateFatigue` integrates and `JointLoad01` is the same
    /// `GetMotorTorque` read the fatigue model charges. Nothing new is measured;
    /// this component only paints what the simulation already knows, at 10 Hz.
    ///
    /// The tint is written through `SpriteRenderer.color` — vertex colour, not a
    /// material property. That keeps every renderer on its shared BodyLit
    /// material (no MaterialPropertyBlock, which would break the SRP batcher for
    /// these renderers, and no `.material` clone, which is forbidden here), and
    /// lerps FROM the part's own base colour so a fighter never stops reading as
    /// their team colour. The head carries face art and is not a PART_DEFS part,
    /// so it is not tinted.
    ///
    /// Read-only with respect to the fight: colour only, spawned behind
    /// `enableJointHeatmap`, and self-restoring in OnDisable.
    public sealed class Systems_JointHeatmap : MonoBehaviour
    {
        /// Paint cadence. Fatigue moves on seconds, not frames; 10 Hz is more
        /// than the eye needs and a rounding error on the CPU.
        private const float PAINT_INTERVAL = 0.1f;

        /// Colour changes smaller than this are not written — style writes are
        /// not free and heat is mostly still between samples.
        private const float WRITE_EPSILON = 0.03f;

        /// The heat ramp: base -> amber -> red. Ambers early so a working joint
        /// reads as "hot" well before it is failing; red is reserved for the
        /// last quarter, which is where the collapses actually come from
        /// (FATIGUE_DEPTH 0.35 means a spent joint still delivers 65%, so the
        /// drama is in the load spike, not in the fatigue reaching 1).
        private static readonly Color HeatAmber = new Color(1f, 0.72f, 0.20f);
        private static readonly Color HeatRed = new Color(1f, 0.22f, 0.12f);

        /// How much of the ramp a fully-stressed joint earns. Below 1 on
        /// purpose: full red on every limb during a heavy exchange reads as the
        /// fighter being on fire rather than as effort.
        private const float HEAT_GAIN = 0.85f;

        /// Load spikes read stronger than slow fatigue — a braced shove is the
        /// effort the audience should feel immediately.
        private const float LOAD_WEIGHT = 0.9f;

        private Systems_GameMatchManager _manager;
        private Agent_BipedBody _bodyA, _bodyB;

        /// Resting colour per part, cached once per body resolve.
        private readonly Color[] _baseA = new Color[16];
        private readonly Color[] _baseB = new Color[16];
        private readonly Color[] _shownA = new Color[16];
        private readonly Color[] _shownB = new Color[16];

        private float _paintLeft;
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
            // Never leave a tinted body behind — this component can be destroyed
            // by the scene load between bracket bouts while the bodies it painted
            // are mid-flop.
            RestoreAll();
        }

        private void Start()
        {
            if (_manager == null) _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            Subscribe();
            ResolveBodies();
        }

        private void Subscribe()
        {
            if (_manager == null || _subscribed) return;
            _manager.RoundStarted += OnRoundStarted;
            _manager.MatchReset += OnRoundStarted;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (_manager == null || !_subscribed) return;
            _manager.RoundStarted -= OnRoundStarted;
            _manager.MatchReset -= OnRoundStarted;
            _subscribed = false;
        }

        private void OnRoundStarted()
        {
            ResolveBodies();
        }

        private void ResolveBodies()
        {
            if (_manager == null) return;
            _bodyA = _manager.wrestlerA != null
                ? _manager.wrestlerA.GetComponent<Agent_BipedBody>() : null;
            _bodyB = _manager.wrestlerB != null
                ? _manager.wrestlerB.GetComponent<Agent_BipedBody>() : null;
            CacheBase(_bodyA, _baseA, _shownA);
            CacheBase(_bodyB, _baseB, _shownB);
        }

        private static void CacheBase(Agent_BipedBody body, Color[] baseColours, Color[] shown)
        {
            if (body == null || body.ArtRenderers == null) return;
            int count = Mathf.Min(baseColours.Length, body.ArtRenderers.Length);
            for (int partIndex = 0; partIndex < count; partIndex++)
            {
                baseColours[partIndex] = body.PartBaseColor(partIndex);
                shown[partIndex] = baseColours[partIndex];
                if (body.ArtRenderers[partIndex] != null)
                {
                    body.ArtRenderers[partIndex].color = baseColours[partIndex];
                }
            }
        }

        private void Update()
        {
            if (_manager == null) return;

            // Paint only while a round is actually being fought; between rounds
            // every body shows its resting colour (and ResetPose has already
            // cleared fatigue, so there is nothing honest to display anyway).
            if (!_manager.RoundActive)
            {
                return;
            }

            if (_bodyA == null || _bodyB == null)
            {
                ResolveBodies();
            }

            _paintLeft -= Time.unscaledDeltaTime;
            if (_paintLeft > 0f) return;
            _paintLeft = PAINT_INTERVAL;

            PaintBody(_bodyA, _baseA, _shownA);
            PaintBody(_bodyB, _baseB, _shownB);
        }

        private void RestoreAll()
        {
            if (_bodyA != null) Restore(_bodyA, _baseA, _shownA);
            if (_bodyB != null) Restore(_bodyB, _baseB, _shownB);
        }

        private static void Restore(Agent_BipedBody body, Color[] baseColours, Color[] shown)
        {
            if (body.ArtRenderers == null) return;
            int count = Mathf.Min(baseColours.Length, body.ArtRenderers.Length);
            for (int partIndex = 0; partIndex < count; partIndex++)
            {
                if (body.ArtRenderers[partIndex] != null)
                {
                    body.ArtRenderers[partIndex].color = baseColours[partIndex];
                }
                shown[partIndex] = baseColours[partIndex];
            }
        }

        private void PaintBody(Agent_BipedBody body, Color[] baseColours, Color[] shown)
        {
            if (body == null || body.ArtRenderers == null || body.Joints == null) return;

            // One stress value per part, folded from every joint that drives it.
            // The spine is three joints painting three different parts, so this
            // is a fold rather than an index — but no part is driven by more than
            // one joint today, so a straight max costs nothing.
            for (int jointIndex = 0; jointIndex < Agent_BipedBody.JointCount; jointIndex++)
            {
                int partIndex = Agent_BipedBody.JointChildPart(jointIndex);
                if (partIndex < 0 || partIndex >= baseColours.Length) continue;
                if (!Agent_BipedBody.JointPowered(jointIndex)) continue;

                // A severed joint drives nothing and reads as rest, exactly as
                // the fatigue model treats it.
                float stress = body.IsDetached(jointIndex)
                    ? 0f
                    : Mathf.Max(body.JointFatigue(jointIndex),
                                body.JointLoad01(jointIndex) * LOAD_WEIGHT);

                Color want = HeatColour(baseColours[partIndex], stress * HEAT_GAIN);
                if (ColourClose(want, shown[partIndex])) continue;
                shown[partIndex] = want;
                if (body.ArtRenderers[partIndex] != null)
                {
                    body.ArtRenderers[partIndex].color = want;
                }
            }
        }

        /// Base -> amber for the first half of the ramp, amber -> red for the
        /// second — the same two-stop shape the damage mannequins use, so the
        /// two instruments read as one visual language.
        private static Color HeatColour(Color baseColour, float heat01)
        {
            float h = Mathf.Clamp01(heat01);
            return h < 0.5f
                ? Color.Lerp(baseColour, HeatAmber, h * 2f)
                : Color.Lerp(HeatAmber, HeatRed, (h - 0.5f) * 2f);
        }

        private static bool ColourClose(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < WRITE_EPSILON
                && Mathf.Abs(a.g - b.g) < WRITE_EPSILON
                && Mathf.Abs(a.b - b.b) < WRITE_EPSILON;
        }
    }
}
