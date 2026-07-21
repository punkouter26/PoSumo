using UnityEngine;

namespace PoSumo
{
    /// Builds a raised dohyo at this object's position: a small elevated clay
    /// platform (top surface at local y=0) with tawara bales and shikiri-sen,
    /// a lower arena floor with a seated crowd on both sides (where pushed-off
    /// wrestlers land), a warm gridded backdrop, and the hanging tsuriyane roof.
    public class Systems_SumoArena : MonoBehaviour
    {
        public float ringHalfWidth = 2.45f;  // tawara position
        public float groundWidth = 5.5f;     // platform width (edge = +/- groundWidth/2)
        public float platformDrop = 0.6f;    // how far down the crowd floor sits
        public float floorWidth = 22f;       // lower arena floor span
        public bool showPosts = true;        // tawara + dressing on/off
        public bool deluxe = false;          // full-stadium dressing (SCN_SUMO)

        static Sprite NoiseSprite(Color baseColor, float amount)
        {
            const int S = 256;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float n = Mathf.PerlinNoise(x * 0.11f, y * 0.11f)
                            + 0.5f * Mathf.PerlinNoise(x * 0.31f, y * 0.31f);
                    float v = 1f + (n / 1.5f - 0.5f) * 2f * amount;
                    tex.SetPixel(x, y, new Color(baseColor.r * v, baseColor.g * v, baseColor.b * v, 1f));
                }
            tex.Apply();
            tex.wrapMode = TextureWrapMode.Repeat;
            return Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), S,
                                 0, SpriteMeshType.FullRect);
        }

        static Sprite GradientSprite(Color top, Color bottom)
        {
            const int S = 128;
            var tex = new Texture2D(4, S, TextureFormat.RGBA32, false);
            for (int y = 0; y < S; y++)
            {
                var c = Color.Lerp(bottom, top, (float)y / (S - 1));
                for (int x = 0; x < 4; x++) tex.SetPixel(x, y, c);
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, 4, S), new Vector2(0.5f, 0.5f), S,
                                 0, SpriteMeshType.FullRect);
        }

        static Sprite ConeSprite()
        {
            const int S = 128;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    // Triangle widening toward the bottom, soft edges, fades upward.
                    float ny = (float)y / (S - 1);              // 0 bottom, 1 top
                    float halfW = Mathf.Lerp(0.5f, 0.06f, ny);  // wide at bottom
                    float dx = Mathf.Abs((float)x / (S - 1) - 0.5f);
                    float a = dx < halfW ? (1f - dx / halfW) * (1f - ny) : 0f;
                    tex.SetPixel(x, y, new Color(1f, 0.95f, 0.8f, a * 0.16f));
                }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0f), S,
                                 0, SpriteMeshType.FullRect);
        }

        void Awake()
        {
            var clay = new Color(0.72f, 0.57f, 0.4f);
            var clayLight = new Color(0.8f, 0.66f, 0.47f);
            var clayDark = new Color(0.55f, 0.42f, 0.3f);

            // --- Dohyo platform: top surface at y=0, sides slightly stepped.
            float half = groundWidth * 0.5f;
            var platform = new GameObject("DohyoPlatform");
            platform.transform.SetParent(transform, false);
            platform.transform.localPosition = new Vector3(0f, -platformDrop * 0.5f, 0f);
            platform.transform.localScale = new Vector3(groundWidth, platformDrop, 1f);
            var psr = platform.AddComponent<SpriteRenderer>();
            psr.sprite = deluxe ? NoiseSprite(clay, 0.06f) : Agent_BipedBody.BoxSprite();
            psr.color = deluxe ? Color.white : clay;
            psr.sortingOrder = -5;
            var pcol = platform.AddComponent<BoxCollider2D>();
            pcol.sharedMaterial = new PhysicsMaterial2D("Ground") { friction = 0.9f, bounciness = 0f };

            // Slightly wider base lip to suggest the mound shape.
            var lip = new GameObject("DohyoBase");
            lip.transform.SetParent(transform, false);
            lip.transform.localPosition = new Vector3(0f, -platformDrop + 0.08f, 0f);
            lip.transform.localScale = new Vector3(groundWidth + 0.7f, 0.16f, 1f);
            var lsr0 = lip.AddComponent<SpriteRenderer>();
            lsr0.sprite = Agent_BipedBody.BoxSprite();
            lsr0.color = clayDark;
            lsr0.sortingOrder = -4;

            // Packed-clay surface highlight.
            var surface = new GameObject("ClaySurface");
            surface.transform.SetParent(transform, false);
            surface.transform.localPosition = new Vector3(0f, -0.04f, 0f);
            surface.transform.localScale = new Vector3(groundWidth, 0.08f, 1f);
            var ssr = surface.AddComponent<SpriteRenderer>();
            ssr.sprite = Agent_BipedBody.BoxSprite();
            ssr.color = clayLight;
            ssr.sortingOrder = -4;

            // --- Lower arena floor (crowd level) + collider so fallers land.
            var floor = new GameObject("ArenaFloor");
            floor.transform.SetParent(transform, false);
            floor.transform.localPosition = new Vector3(0f, -platformDrop - 0.5f, 0f);
            floor.transform.localScale = new Vector3(floorWidth, 1f, 1f);
            var fsr = floor.AddComponent<SpriteRenderer>();
            fsr.sprite = Agent_BipedBody.BoxSprite();
            fsr.color = new Color(0.24f, 0.18f, 0.15f);
            fsr.sortingOrder = -6;
            var fcol = floor.AddComponent<BoxCollider2D>();
            fcol.sharedMaterial = new PhysicsMaterial2D("Floor") { friction = 0.8f, bounciness = 0f };

            // --- Shikiri-sen: the two white starting lines at ring center.
            for (int s = -1; s <= 1; s += 2)
            {
                var line = new GameObject(s < 0 ? "ShikiriLeft" : "ShikiriRight");
                line.transform.SetParent(transform, false);
                line.transform.localPosition = new Vector3(s * 0.35f, 0.015f, 0f);
                line.transform.localScale = new Vector3(0.25f, 0.035f, 1f);
                var lsr = line.AddComponent<SpriteRenderer>();
                lsr.sprite = Agent_BipedBody.BoxSprite();
                lsr.color = new Color(0.95f, 0.93f, 0.88f);
                lsr.sortingOrder = -3;
            }

            // --- Deluxe: gradient arena wall behind everything.
            if (deluxe)
            {
                var wall = new GameObject("ArenaWall");
                wall.transform.SetParent(transform, false);
                wall.transform.localPosition = new Vector3(0f, 4.4f, 0f);
                wall.transform.localScale = new Vector3(floorWidth, 12f, 1f);
                var wsr = wall.AddComponent<SpriteRenderer>();
                wsr.sprite = GradientSprite(new Color(0.09f, 0.06f, 0.07f), new Color(0.25f, 0.17f, 0.13f));
                wsr.sortingOrder = -12;
            }

            // --- Warm gridded backdrop (1 m cells, heavy line each 5 m).
            const int PX = 500;
            var tex = new Texture2D(PX, PX, TextureFormat.RGBA32, false);
            var bg = deluxe ? new Color(0f, 0f, 0f, 0f) : new Color(0.17f, 0.13f, 0.12f, 1f);
            var thin = deluxe ? new Color(0.62f, 0.54f, 0.45f, 0.38f) : new Color(0.24f, 0.19f, 0.17f, 1f);
            var heavy = deluxe ? new Color(0.75f, 0.65f, 0.52f, 0.55f) : new Color(0.36f, 0.29f, 0.25f, 1f);
            for (int y = 0; y < PX; y++)
                for (int x = 0; x < PX; x++)
                {
                    bool heavyLine = x < 3 || y < 3 || x >= PX - 3 || y >= PX - 3;
                    bool thinLine = (x % 100) < 2 || (y % 100) < 2;
                    tex.SetPixel(x, y, heavyLine ? heavy : (thinLine ? thin : bg));
                }
            tex.Apply();
            tex.wrapMode = TextureWrapMode.Repeat;
            var gridSprite = Sprite.Create(tex, new Rect(0, 0, PX, PX), new Vector2(0.5f, 0.5f),
                                           100f, 0, SpriteMeshType.FullRect);
            var grid = new GameObject("Grid");
            grid.transform.SetParent(transform, false);
            grid.transform.localPosition = new Vector3(0f, 4.4f, 0f);
            var gsr2 = grid.AddComponent<SpriteRenderer>();
            gsr2.sprite = gridSprite;
            gsr2.drawMode = SpriteDrawMode.Tiled;
            gsr2.size = new Vector2(floorWidth, 12f);
            gsr2.sortingOrder = -10;

            if (!showPosts) return;

            // --- Tawara: straw bales at the ring edges.
            var straw = new Color(0.78f, 0.68f, 0.42f);
            var strawDark = new Color(0.62f, 0.53f, 0.3f);
            for (int s = -1; s <= 1; s += 2)
            {
                for (int b = 0; b < 2; b++)
                {
                    var bale = new GameObject((s < 0 ? "TawaraLeft" : "TawaraRight") + b);
                    bale.transform.SetParent(transform, false);
                    bale.transform.localPosition =
                        new Vector3(s * (ringHalfWidth - b * 0.24f), 0.08f, 0f);
                    bale.transform.localScale = new Vector3(0.3f, 0.17f, 1f);
                    var bsr = bale.AddComponent<SpriteRenderer>();
                    bsr.sprite = Agent_BipedBody.BoxSprite();
                    bsr.color = b == 0 ? strawDark : straw;
                    bsr.sortingOrder = -3 + b;
                }
            }

            // --- Crowd: two seated rows each side at floor level, plus a raised
            // back row on step blocks. Deterministic palette by seat index.
            var kimono = new[]
            {
                new Color(0.45f, 0.3f, 0.5f), new Color(0.3f, 0.42f, 0.55f),
                new Color(0.55f, 0.35f, 0.28f), new Color(0.35f, 0.5f, 0.38f),
                new Color(0.5f, 0.45f, 0.3f), new Color(0.42f, 0.28f, 0.32f)
            };
            var skin = new Color(0.92f, 0.78f, 0.62f);
            float floorTop = -platformDrop;
            for (int s = -1; s <= 1; s += 2)
            {
                for (int row = 0; row < 2; row++)
                {
                    float rowY = floorTop + row * 0.3f;
                    float startX = half + 0.9f + row * 0.28f;
                    int seats = 6;
                    if (row == 1)
                    {
                        // Step block the back row sits on.
                        var step = new GameObject(s < 0 ? "StepLeft" : "StepRight");
                        step.transform.SetParent(transform, false);
                        step.transform.localPosition =
                            new Vector3(s * (startX + seats * 0.55f * 0.5f), rowY - 0.15f, 0f);
                        step.transform.localScale = new Vector3(seats * 0.55f + 0.6f, 0.3f, 1f);
                        var stsr = step.AddComponent<SpriteRenderer>();
                        stsr.sprite = Agent_BipedBody.BoxSprite();
                        stsr.color = new Color(0.2f, 0.15f, 0.13f);
                        stsr.sortingOrder = -8;
                    }
                    for (int i = 0; i < seats; i++)
                    {
                        float px = s * (startX + i * 0.55f);
                        int idx = (s + 1) * 7 + row * 13 + i * 5;
                        var c = kimono[idx % kimono.Length];
                        if (row == 1) c *= 0.75f;

                        var body = new GameObject("Spectator");
                        body.transform.SetParent(transform, false);
                        body.transform.localPosition = new Vector3(px, rowY + 0.19f, 0f);
                        body.transform.localScale = new Vector3(0.34f, 0.38f, 1f);
                        var bsr2 = body.AddComponent<SpriteRenderer>();
                        bsr2.sprite = Agent_BipedBody.BoxSprite();
                        bsr2.color = new Color(c.r, c.g, c.b, 1f);
                        bsr2.sortingOrder = row == 0 ? -6 : -7;

                        var head = new GameObject("Head");
                        head.transform.SetParent(body.transform, false);
                        head.transform.localScale = new Vector3(0.2f / 0.34f, 0.2f / 0.38f, 1f);
                        head.transform.localPosition = new Vector3(0f, 0.75f, 0f);
                        var hsr = head.AddComponent<SpriteRenderer>();
                        hsr.sprite = Agent_BipedBody.CircleSprite();
                        hsr.color = row == 1 ? skin * 0.8f : skin;
                        hsr.sortingOrder = bsr2.sortingOrder;
                    }
                }
            }

            // --- Deluxe stadium dressing.
            if (deluxe)
            {
                // Spotlight pools on the ring.
                var cone = ConeSprite();
                foreach (float sx in new[] { -1.2f, 1.2f })
                {
                    var spot = new GameObject("Spotlight");
                    spot.transform.SetParent(transform, false);
                    spot.transform.localPosition = new Vector3(sx, 0f, 0f);
                    spot.transform.localScale = new Vector3(3.4f, 6.2f, 1f);
                    var ssr2 = spot.AddComponent<SpriteRenderer>();
                    ssr2.sprite = cone;
                    ssr2.sortingOrder = -2;
                }

                // Lantern row under the roof.
                for (int i = 0; i < 5; i++)
                {
                    float lx = Mathf.Lerp(-(half + 1.2f), half + 1.2f, i / 4f);
                    var halo = new GameObject("LanternHalo");
                    halo.transform.SetParent(transform, false);
                    halo.transform.localPosition = new Vector3(lx, 5.55f, 0f);
                    halo.transform.localScale = new Vector3(0.55f, 0.55f, 1f);
                    var hsr2 = halo.AddComponent<SpriteRenderer>();
                    hsr2.sprite = Agent_BipedBody.CircleSprite();
                    hsr2.color = new Color(1f, 0.8f, 0.45f, 0.18f);
                    hsr2.sortingOrder = -6;
                    var lamp = new GameObject("Lantern");
                    lamp.transform.SetParent(transform, false);
                    lamp.transform.localPosition = new Vector3(lx, 5.55f, 0f);
                    lamp.transform.localScale = new Vector3(0.2f, 0.26f, 1f);
                    var lsr2 = lamp.AddComponent<SpriteRenderer>();
                    lsr2.sprite = Agent_BipedBody.CircleSprite();
                    lsr2.color = new Color(1f, 0.85f, 0.55f, 0.95f);
                    lsr2.sortingOrder = -5;
                }

                // Wall banners.
                var bannerCols = new[]
                {
                    new Color(0.55f, 0.16f, 0.14f), new Color(0.16f, 0.3f, 0.45f),
                    new Color(0.5f, 0.38f, 0.12f), new Color(0.2f, 0.38f, 0.24f)
                };
                for (int i = 0; i < 4; i++)
                {
                    float bx = Mathf.Lerp(-(half + 4.2f), half + 4.2f, i / 3f);
                    var banner = new GameObject("Banner");
                    banner.transform.SetParent(transform, false);
                    banner.transform.localPosition = new Vector3(bx, 3.6f, 0f);
                    banner.transform.localScale = new Vector3(0.55f, 1.5f, 1f);
                    var bsr3 = banner.AddComponent<SpriteRenderer>();
                    bsr3.sprite = Agent_BipedBody.BoxSprite();
                    bsr3.color = bannerCols[i % bannerCols.Length];
                    bsr3.sortingOrder = -9;
                    var motif = new GameObject("Motif");
                    motif.transform.SetParent(banner.transform, false);
                    motif.transform.localScale = new Vector3(0.6f, 0.18f, 1f);
                    motif.transform.localPosition = new Vector3(0f, 0.22f, 0f);
                    var msr = motif.AddComponent<SpriteRenderer>();
                    msr.sprite = Agent_BipedBody.BoxSprite();
                    msr.color = new Color(0.94f, 0.9f, 0.82f, 0.9f);
                    msr.sortingOrder = -8;
                }

                // Distant back-row crowd silhouettes on both sides.
                for (int s = -1; s <= 1; s += 2)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        float px = s * (half + 0.7f + i * 0.42f);
                        var sil = new GameObject("CrowdSilhouette");
                        sil.transform.SetParent(transform, false);
                        sil.transform.localPosition = new Vector3(px, -platformDrop + 0.62f, 0f);
                        sil.transform.localScale = new Vector3(0.3f, 0.34f, 1f);
                        var ssr3 = sil.AddComponent<SpriteRenderer>();
                        ssr3.sprite = Agent_BipedBody.CircleSprite();
                        ssr3.color = new Color(0.1f, 0.08f, 0.08f, 0.9f);
                        ssr3.sortingOrder = -9;
                    }
                }
            }

            // --- Tsuriyane: hanging roof with the four traditional fusa tassels.
            var roof = new GameObject("Tsuriyane");
            roof.transform.SetParent(transform, false);
            roof.transform.localPosition = new Vector3(0f, 6.4f, 0f);
            roof.transform.localScale = new Vector3(groundWidth + 3.5f, 0.5f, 1f);
            var rsr = roof.AddComponent<SpriteRenderer>();
            rsr.sprite = Agent_BipedBody.BoxSprite();
            rsr.color = new Color(0.3f, 0.17f, 0.13f);
            rsr.sortingOrder = -6;
            var ridge = new GameObject("TsuriyaneRidge");
            ridge.transform.SetParent(transform, false);
            ridge.transform.localPosition = new Vector3(0f, 6.77f, 0f);
            ridge.transform.localScale = new Vector3(groundWidth + 4.3f, 0.16f, 1f);
            var rid = ridge.AddComponent<SpriteRenderer>();
            rid.sprite = Agent_BipedBody.BoxSprite();
            rid.color = new Color(0.2f, 0.11f, 0.09f);
            rid.sortingOrder = -6;
            var fusa = new[]
            {
                new Color(0.12f, 0.12f, 0.12f), new Color(0.2f, 0.5f, 0.28f),
                new Color(0.75f, 0.2f, 0.16f), new Color(0.92f, 0.9f, 0.85f)
            };
            for (int i = 0; i < 4; i++)
            {
                float fx = Mathf.Lerp(-(half + 1.4f), half + 1.4f, i / 3f);
                var tassel = new GameObject("Fusa" + i);
                tassel.transform.SetParent(transform, false);
                tassel.transform.localPosition = new Vector3(fx, 5.9f, 0f);
                tassel.transform.localScale = new Vector3(0.12f, 0.5f, 1f);
                var tsr = tassel.AddComponent<SpriteRenderer>();
                tsr.sprite = Agent_BipedBody.BoxSprite();
                tsr.color = fusa[i];
                tsr.sortingOrder = -5;
            }
        }
    }
}
