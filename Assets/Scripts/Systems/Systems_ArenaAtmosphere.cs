using System.Collections.Generic;
using UnityEngine;

namespace PoSumo
{
    /// Depth and air for the arena: parallax on the far layers, atmospheric
    /// haze-tinting so the background sits behind the fighters, a crowd that
    /// breathes, and light shafts falling from the tsuriyane.
    ///
    /// Everything the arena builds sits on one plane at z = 0, which is why the
    /// backdrop reads as a diagram rather than a place. This does not move it in
    /// z — the 2D renderer would not care — it fakes depth the two ways that
    /// actually work in a flat scene: layers that slide at different rates as the
    /// camera tracks, and distance-tinting toward the light so far things lose
    /// contrast. That second one is standing in for depth of field, which URP's
    /// 2D renderer cannot do because sprites write no depth.
    ///
    /// Operates on the BAKED arena children, matched by sorting order, so it needs
    /// no scene wiring. Nothing here touches physics or gameplay geometry: the
    /// dohyo, the crowd floor and the fighters all sit at sorting order -6 or
    /// above and are deliberately excluded.
    public sealed class Systems_ArenaAtmosphere : MonoBehaviour
    {
        [Header("Parallax")]
        [Tooltip("Sorting order at or below which a renderer belongs to the back wall. Everything at or below this moves as ONE plane. The crowd stands (-8), crowd (-6/-7), dohyo (-5) and floor (-6) must stay above it or the arena tears apart.")]
        public int backgroundOrderThreshold = -9;
        [Tooltip("How far the back wall slides against the camera. Small: the wall is a painted backdrop a few metres behind the ring, not a distant horizon.")]
        [Range(0f, 1f)] public float parallaxStrength = 0.22f;

        [Header("Atmospheric perspective")]
        [Tooltip("Far layers are tinted toward this and lose saturation, which is what puts air between them and the fighters.")]
        public Color hazeColor = new Color(0.42f, 0.44f, 0.55f);
        // Lowered 0.28 -> 0.12 when the lighting rig was re-enabled 2026-08-25. With
        // the rig off the haze sat over a near-black backdrop and barely read; with
        // the key light on it washes the upper backdrop into flat grey and eats the
        // depth the rig exists to create. Measured against a live capture, not
        // guessed — the two switches interact and the haze was tuned for the wrong one.
        [Range(0f, 1f)] public float maxHaze = 0.06f;

        [Header("Crowd")]
        public bool swayCrowd = true;
        public float swayAmplitude = 0.035f;
        public float swayHz = 0.45f;

        [Header("Light shafts")]
        // OFF. These are cheap sprite quads, NOT the key light's volumetric cone —
        // they were the structure (four discrete beams) laid over the cone's radial
        // falloff. With keyVolumeIntensity at 0 there is no cone to fan from, so
        // four bright quads hanging in front of everything at sorting order 20 read
        // as smears on the lens rather than as light. Turn this on together with
        // the key volumetric, not alone.
        public bool showShafts = false;
        public int shaftCount = 4;
        public float shaftTopY = 6.1f;
        public float shaftLength = 6.4f;
        public float shaftWidth = 1.15f;
        public float shaftSpread = 3.2f;
        [Range(0f, 0.4f)] public float shaftOpacity = 0.075f;
        public float shaftShimmerHz = 0.23f;

        private struct ParallaxLayer
        {
            public Transform transform;
            public Vector3 baseLocalPosition;
        }

        private struct SwayTarget
        {
            public Transform transform;
            public Vector3 baseLocalPosition;
            public float phase;
            /// True when this transform also belongs to the parallax plane, so
            /// the sway pass carries the parallax offset instead of erasing it.
            public bool parallax;
        }

        private readonly List<ParallaxLayer> _layers = new List<ParallaxLayer>();
        private readonly List<SwayTarget> _sway = new List<SwayTarget>();
        private readonly List<SpriteRenderer> _shafts = new List<SpriteRenderer>();

        private Transform _arena;
        private Camera _camera;

        /// Seconds between Camera.main probes while no main camera exists. See LateUpdate.
        private const float CAMERA_PROBE_INTERVAL = 0.5f;
        private float _nextCameraProbe;
        private float _arenaCenterX;
        private static Sprite _shaftSprite;

        private void Start()
        {
            var arena = FindAnyObjectByType<Systems_SumoArena>();
            if (arena == null)
            {
                enabled = false;
                return;
            }
            _arena = arena.transform;
            _arenaCenterX = _arena.position.x;
            _camera = Camera.main;

            Collect();
            if (showShafts)
            {
                BuildShafts();
            }

            // The standing haze belongs to the arena, not to an impact.
            Systems_DustPuff.ConfigureHaze(_arena.position + new Vector3(0f, 1.2f, 0f), 3.2f);
        }

        private void Collect()
        {
            SpriteRenderer[] renderers = _arena.GetComponentsInChildren<SpriteRenderer>(true);

            for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                SpriteRenderer renderer = renderers[rendererIndex];
                Transform target = renderer.transform;

                bool swaying = swayCrowd && IsCrowd(target.name);
                bool background = renderer.sortingOrder <= backgroundOrderThreshold;

                if (swaying)
                {
                    _sway.Add(new SwayTarget
                    {
                        transform = target,
                        baseLocalPosition = target.localPosition,
                        // Deterministic per-seat phase from the position, so the
                        // crowd never moves as one block — and so it looks the same
                        // every run, which matters when comparing screenshots.
                        phase = Mathf.Repeat(target.position.x * 2.3f + target.position.y * 1.7f, Mathf.PI * 2f),
                        // The 16 CrowdSilhouettes are sortingOrder -9 against a -9
                        // threshold, so they are background AND swaying. They used
                        // to be appended to both lists, and the sway pass — which
                        // runs second and writes localPosition outright — erased
                        // the parallax offset, leaving the crowd standing still
                        // while the wall it is painted on slid behind it. Carrying
                        // the offset here keeps them in register.
                        parallax = background && target.parent == _arena,
                    });
                }

                if (!background)
                {
                    continue;
                }

                // ONE depth for the whole back wall. The wall, the backdrop grid,
                // the banners and the distant silhouettes are all painted on the
                // same plane a few metres behind the ring — giving each its own
                // parallax rate by sorting order slides them off each other and
                // visibly tears the backdrop into steps.
                //
                // Only root-most transforms; a child (a banner's Motif) rides its
                // parent and would otherwise get the offset twice.
                // Swaying transforms are driven by the sway pass instead, which
                // applies the same offset — listing them here as well would just
                // be a write that pass immediately overwrites.
                if (target.parent == _arena && !swaying)
                {
                    _layers.Add(new ParallaxLayer
                    {
                        transform = target,
                        baseLocalPosition = target.localPosition,
                    });
                }

                // Atmospheric tint is per-renderer, children included.
                Color colour = renderer.color;
                renderer.color = new Color(
                    Mathf.Lerp(colour.r, hazeColor.r, maxHaze),
                    Mathf.Lerp(colour.g, hazeColor.g, maxHaze),
                    Mathf.Lerp(colour.b, hazeColor.b, maxHaze),
                    colour.a);
            }
        }

        private static bool IsCrowd(string objectName) =>
            objectName == "Spectator" || objectName == "CrowdSilhouette";

        private void LateUpdate()
        {
            // Camera.main is a tagged-object search, so a stretch with no main
            // camera (scene load, camera disabled) would otherwise pay for one
            // every rendered frame until one appears. Probe on an interval
            // instead; the parallax simply holds its last offset until then.
            if (_camera == null)
            {
                if (Time.unscaledTime < _nextCameraProbe)
                {
                    return;
                }
                _nextCameraProbe = Time.unscaledTime + CAMERA_PROBE_INTERVAL;
                _camera = Camera.main;
                if (_camera == null)
                {
                    return;
                }
            }

            // Parallax is driven by how far the follow camera has tracked off the
            // arena centre, not by absolute position, so it is stable whatever the
            // arena's world coordinates are.
            float cameraOffset = _camera.transform.position.x - _arenaCenterX;
            for (int layerIndex = 0; layerIndex < _layers.Count; layerIndex++)
            {
                ParallaxLayer layer = _layers[layerIndex];
                if (layer.transform == null)
                {
                    continue;
                }
                Vector3 position = layer.baseLocalPosition;
                position.x += cameraOffset * parallaxStrength;
                layer.transform.localPosition = position;
            }

            if (swayCrowd)
            {
                float t = Time.time * swayHz * Mathf.PI * 2f;
                for (int swayIndex = 0; swayIndex < _sway.Count; swayIndex++)
                {
                    SwayTarget target = _sway[swayIndex];
                    if (target.transform == null)
                    {
                        continue;
                    }
                    Vector3 position = target.baseLocalPosition;
                    position.y += Mathf.Sin(t + target.phase) * swayAmplitude;
                    position.x += Mathf.Cos(t * 0.6f + target.phase) * swayAmplitude * 0.4f;
                    if (target.parallax)
                    {
                        position.x += cameraOffset * parallaxStrength;
                    }
                    target.transform.localPosition = position;
                }
            }

            AnimateShafts();
        }

        // ------------------------------------------------------------ shafts

        /// A soft vertical beam: bright at the top where it leaves the roof, fading
        /// out before it reaches the clay, with a soft horizontal falloff so the
        /// edges never show a seam.
        private static Sprite ShaftSprite()
        {
            if (_shaftSprite != null)
            {
                return _shaftSprite;
            }
            const int W = 32;
            const int H = 128;
            var texture = new Texture2D(W, H, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "PoSumo_LightShaft",
            };
            for (int rowIndex = 0; rowIndex < H; rowIndex++)
            {
                // v = 1 at the top of the sprite.
                float v = (rowIndex + 0.5f) / H;
                float vertical = Mathf.Pow(v, 1.6f);
                for (int columnIndex = 0; columnIndex < W; columnIndex++)
                {
                    float u = (columnIndex + 0.5f) / W * 2f - 1f;
                    float horizontal = Mathf.Cos(Mathf.Clamp(u, -1f, 1f) * Mathf.PI * 0.5f);
                    horizontal *= horizontal;
                    texture.SetPixel(columnIndex, rowIndex, new Color(1f, 1f, 1f, vertical * horizontal));
                }
            }
            texture.Apply();
            _shaftSprite = Sprite.Create(texture, new Rect(0, 0, W, H), new Vector2(0.5f, 1f), 100f,
                                         0, SpriteMeshType.FullRect);
            return _shaftSprite;
        }

        private void BuildShafts()
        {
            Systems_ArenaLighting lighting = Systems_ArenaLighting.Instance;
            Color tint = lighting != null ? lighting.keyColor : new Color(1f, 0.93f, 0.78f);

            var root = new GameObject("LightShafts");
            root.transform.SetParent(transform, false);
            root.transform.position = new Vector3(_arenaCenterX, 0f, 0f);

            for (int shaftIndex = 0; shaftIndex < shaftCount; shaftIndex++)
            {
                float t = shaftCount == 1 ? 0.5f : shaftIndex / (float)(shaftCount - 1);
                float x = Mathf.Lerp(-shaftSpread, shaftSpread, t);

                var go = new GameObject($"Shaft{shaftIndex}");
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = new Vector3(x, shaftTopY, 0f);
                // Fan outward from the key light, so the beams converge at the roof
                // rather than falling as parallel stripes.
                go.transform.localRotation = Quaternion.Euler(0f, 0f, -x * 2.6f);
                go.transform.localScale = new Vector3(shaftWidth, shaftLength / 1.28f, 1f);

                var renderer = go.AddComponent<SpriteRenderer>();
                renderer.sprite = ShaftSprite();
                renderer.color = new Color(tint.r, tint.g, tint.b, shaftOpacity);
                // In front of everything: a shaft is air between the camera and the
                // scene, so it composites over the fighters, not behind them.
                renderer.sortingOrder = 20;
                _shafts.Add(renderer);
            }
        }

        private void AnimateShafts()
        {
            if (_shafts.Count == 0)
            {
                return;
            }
            float t = Time.time * shaftShimmerHz * Mathf.PI * 2f;
            for (int shaftIndex = 0; shaftIndex < _shafts.Count; shaftIndex++)
            {
                SpriteRenderer renderer = _shafts[shaftIndex];
                if (renderer == null)
                {
                    continue;
                }
                // Each beam breathes on its own phase; dust drifting through a real
                // shaft never makes them all brighten together.
                float shimmer = 0.72f + 0.28f * Mathf.Sin(t + shaftIndex * 1.9f);
                Color colour = renderer.color;
                colour.a = shaftOpacity * shimmer;
                renderer.color = colour;
            }
        }
    }
}
