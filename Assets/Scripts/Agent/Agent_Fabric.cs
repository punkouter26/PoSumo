using System.Collections.Generic;
using UnityEngine;

namespace PoSumo
{
    /// Procedural fabric textures for `Agent_ClothingSpec`.
    ///
    /// Every garment pattern is generated in code as a 128x128 sprite whose RGB
    /// is a GREYSCALE weave and whose alpha is the same silhouette the bare part
    /// already draws — an ellipse for limbs, an exact rectangle for the trunk and
    /// the feet. The SpriteRenderer's colour then tints that grey into the
    /// character's garment colour, exactly the way `teamColor` tints bare skin.
    ///
    /// Three consequences worth stating:
    ///
    ///  - The drawn edge is still the colliding edge. Swapping the texture never
    ///    changes the silhouette, so no collider and no brain is touched.
    ///  - No texture assets, nothing to import, nothing to atlas: sprites built at
    ///    runtime are exempt from the SpriteAtlas rule the way the body's own
    ///    sprites already are.
    ///  - Cached per (weave, shape). Domain reload is off in this project and a
    ///    destroyed sprite reads as null through Unity's ==, so a stale entry
    ///    rebuilds itself on the next Play session rather than leaking a dead
    ///    texture into it.
    public static class Agent_Fabric
    {
        /// The pattern families a garment can be cut from. `None` means "bare" and
        /// makes `Sprite` return null so the caller keeps the plain part sprite.
        public enum Weave
        {
            None,
            Denim,     // jeans — 45-degree twill ridges
            Fleece,    // sweatpants — soft mottled knit
            Jersey,    // t-shirt / tank — fine horizontal courses
            Wool,      // sweater — coarse knitted rows
            Canvas,    // shorts / sneakers — over-under weave
            Coarse,    // mawashi — stiff heavily-ribbed cloth
            Lycra,     // singlet — smooth sheen
            Leather,   // boots — blotchy grain
            Rib,       // socks — vertical ribs
        }

        private const int SIZE = 128;
        /// Antialias width of the elliptical edge, in texels — matched to
        /// `Agent_BipedBody.RoundedSprite` so a clothed limb has the identical
        /// silhouette to a bare one.
        private const float EDGE_SOFTNESS = 1.5f;

        private static readonly Dictionary<int, Sprite> _sprites = new Dictionary<int, Sprite>();

        /// Sprite for one weave in one silhouette. `rectangle` = the exact-rectangle
        /// shape box-collidered parts draw; otherwise the limb ellipse. Returns null
        /// for `Weave.None`.
        public static Sprite Sprite(Weave weave, bool rectangle)
        {
            if (weave == Weave.None)
            {
                return null;
            }
            int key = ((int)weave << 1) | (rectangle ? 1 : 0);
            if (_sprites.TryGetValue(key, out Sprite cached) && cached != null)
            {
                return cached;
            }
            Sprite built = Build(weave, rectangle);
            _sprites[key] = built;
            return built;
        }

        private static Sprite Build(Weave weave, bool rectangle)
        {
            var texture = new Texture2D(SIZE, SIZE, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var pixels = new Color32[SIZE * SIZE];
            float half = SIZE * 0.5f;

            for (int y = 0; y < SIZE; y++)
            {
                for (int x = 0; x < SIZE; x++)
                {
                    float alpha = 1f;
                    if (!rectangle)
                    {
                        float dx = Mathf.Abs(x + 0.5f - half);
                        float dy = Mathf.Abs(y + 0.5f - half);
                        alpha = Mathf.Clamp01((half - Mathf.Sqrt(dx * dx + dy * dy)) / EDGE_SOFTNESS);
                    }
                    byte l = (byte)Mathf.RoundToInt(Mathf.Clamp01(Luminance(weave, x, y)) * 255f);
                    pixels[y * SIZE + x] = new Color32(l, l, l, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();
            // Qualified: the Sprite(...) method below would otherwise shadow the
            // UnityEngine.Sprite type name inside this class (CS0119).
            return UnityEngine.Sprite.Create(texture, new Rect(0, 0, SIZE, SIZE),
                                             new Vector2(0.5f, 0.5f), SIZE, 0, SpriteMeshType.FullRect);
        }

        /// Grey level at one texel for one weave, nominally in [0, 1] (clamped by
        /// the caller). Patterns are authored RELATIVE — a mean near 0.9 — so a
        /// garment reads as the same colour as its swatch with the weave laid over
        /// it, rather than as a darker version of it.
        private static float Luminance(Weave weave, int x, int y)
        {
            switch (weave)
            {
                case Weave.Denim:
                {
                    // 45-degree twill: ridges every 6 texels, hard on the rise and
                    // slurred on the fall, which is what makes denim read as denim
                    // rather than as a pinstripe.
                    float phase = ((x + y) % 6) / 6f;
                    float ridge = phase < 0.45f ? 1f : 0.6f;
                    return 0.74f + 0.22f * ridge + 0.05f * Hash01(x, y);
                }
                case Weave.Fleece:
                {
                    // Two octaves of smooth noise: sweatpant fleece is mottled, not
                    // patterned, and anything with a visible period reads as print.
                    float n = 0.6f * SmoothNoise(x, y, 18) + 0.4f * SmoothNoise(x, y, 6);
                    return 0.86f + 0.13f * n + 0.02f * Hash01(x, y);
                }
                case Weave.Jersey:
                {
                    // Fine horizontal courses (the knit rows of a t-shirt).
                    float course = (y % 4) < 2 ? 1f : 0.55f;
                    return 0.88f + 0.08f * course + 0.04f * Hash01(x, y);
                }
                case Weave.Wool:
                {
                    // Coarse knitting: chunky noise plus visible rows, higher
                    // contrast than fleece because a jumper is genuinely bumpy.
                    float rows = (y % 7) < 3 ? 1f : 0.6f;
                    return 0.76f + 0.16f * SmoothNoise(x, y, 9) + 0.06f * rows
                         + 0.04f * Hash01(x, y);
                }
                case Weave.Canvas:
                {
                    return 0.84f + 0.12f * WeaveOverUnder(x, y, 4) + 0.04f * Hash01(x, y);
                }
                case Weave.Coarse:
                {
                    // Mawashi cloth: a coarse over-under weave banded every 16
                    // texels, so it reads as one stiff piece of folded fabric.
                    float band = (y % 16) < 8 ? 1f : 0.72f;
                    return 0.78f + 0.15f * WeaveOverUnder(x, y, 8) * band
                         + 0.05f * band + 0.04f * Hash01(x, y);
                }
                case Weave.Lycra:
                {
                    // Almost smooth — a singlet's tell is a broad sheen, not grain.
                    return 0.92f + 0.06f * SmoothNoise(x, y, 32) + 0.02f * Hash01(x, y);
                }
                case Weave.Leather:
                {
                    float grain = SmoothNoise(x, y, 20);
                    grain = grain * grain;   // sharpen: leather blotches, not clouds
                    return 0.82f + 0.16f * grain + 0.05f * Hash01(x, y);
                }
                case Weave.Rib:
                {
                    float rib = (x % 5) < 2 ? 1f : 0.6f;
                    return 0.85f + 0.11f * rib + 0.04f * Hash01(x, y);
                }
                default:
                {
                    return 1f;
                }
            }
        }

        /// Over-under plain weave: 1 where the warp thread crosses on top, 0 where
        /// the weft does, alternating every `period` texels in both axes.
        private static float WeaveOverUnder(int x, int y, int period)
        {
            int cellX = x / period;
            int cellY = y / period;
            bool over = ((cellX + cellY) & 1) == 0;
            bool alongX = over ? (x % period) < (y % period) : (y % period) < (x % period);
            return alongX ? 1f : 0.55f;
        }

        /// Bilinear-interpolated value noise on a `cell`-texel grid. Smoothstep on
        /// both axes so it mottles instead of tiling.
        private static float SmoothNoise(int x, int y, int cell)
        {
            int cellX = x / cell;
            int cellY = y / cell;
            float fx = (x - cellX * cell) / (float)cell;
            float fy = (y - cellY * cell) / (float)cell;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            float a = Hash01(cellX, cellY);
            float b = Hash01(cellX + 1, cellY);
            float c = Hash01(cellX, cellY + 1);
            float d = Hash01(cellX + 1, cellY + 1);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        /// Deterministic hash to [0, 1]. Never `Random`: two builds of the same
        /// garment must be identical, and a shared seeded Random would make the
        /// pattern depend on how many random calls the physics made first.
        private static float Hash01(int x, int y)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263) + 0x9E3779B9u;
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFFu) / 16777215f;
            }
        }
    }
}
