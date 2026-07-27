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
        [Range(0f, 1f)] public float maxHaze = 0.28f;

        [Header("Crowd")]
        public bool swayCrowd = true;
        public float swayAmplitude = 0.035f;
        public float swayHz = 0.45f;

        [Header("Light shafts")]
        public bool showShafts = true;
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
        }

        private readonly List<ParallaxLayer> _layers = new List<ParallaxLayer>();
        private readonly List<SwayTarget> _sway = new List<SwayTarget>();
        private readonly List<SpriteRenderer> _shafts = new List<SpriteRenderer>();

        private Transform _arena;
        private Camera _camera;
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

            for (int i = 0; i < renderers.Length; i++)
            {
                SpriteRenderer renderer = renderers[i];
                Transform target = renderer.transform;

                if (swayCrowd && IsCrowd(target.name))
                {
                    _sway.Add(new SwayTarget
                    {
                        transform = target,
                        baseLocalPosition = target.localPosition,
                        // Deterministic per-seat phase from the position, so the
                        // crowd never moves as one block — and so it looks the same
                        // every run, which matters when comparing screenshots.
                        phase = Mathf.Repeat(target.position.x * 2.3f + target.position.y * 1.7f, Mathf.PI * 2f),
                    });
                }

                if (renderer.sortingOrder > backgroundOrderThreshold)
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
                if (target.parent == _arena)
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
            if (_camera == null)
            {
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
            for (int i = 0; i < _layers.Count; i++)
            {
                ParallaxLayer layer = _layers[i];
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
                for (int i = 0; i < _sway.Count; i++)
                {
                    SwayTarget target = _sway[i];
                    if (target.transform == null)
                    {
                        continue;
                    }
                    Vector3 position = target.baseLocalPosition;
                    position.y += Mathf.Sin(t + target.phase) * swayAmplitude;
                    position.x += Mathf.Cos(t * 0.6f + target.phase) * swayAmplitude * 0.4f;
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
            for (int y = 0; y < H; y++)
            {
                // v = 1 at the top of the sprite.
                float v = (y + 0.5f) / H;
                float vertical = Mathf.Pow(v, 1.6f);
                for (int x = 0; x < W; x++)
                {
                    float u = (x + 0.5f) / W * 2f - 1f;
                    float horizontal = Mathf.Cos(Mathf.Clamp(u, -1f, 1f) * Mathf.PI * 0.5f);
                    horizontal *= horizontal;
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, vertical * horizontal));
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

            for (int i = 0; i < shaftCount; i++)
            {
                float t = shaftCount == 1 ? 0.5f : i / (float)(shaftCount - 1);
                float x = Mathf.Lerp(-shaftSpread, shaftSpread, t);

                var go = new GameObject($"Shaft{i}");
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
            for (int i = 0; i < _shafts.Count; i++)
            {
                SpriteRenderer renderer = _shafts[i];
                if (renderer == null)
                {
                    continue;
                }
                // Each beam breathes on its own phase; dust drifting through a real
                // shaft never makes them all brighten together.
                float shimmer = 0.72f + 0.28f * Mathf.Sin(t + i * 1.9f);
                Color colour = renderer.color;
                colour.a = shaftOpacity * shimmer;
                renderer.color = colour;
            }
        }
    }
}
