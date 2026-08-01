using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace PoSumo
{
    /// Builds a raised dohyo at this object's position: a small elevated clay
    /// platform (top surface at local y=0) with tawara bales and shikiri-sen,
    /// a lower arena floor with a seated crowd on both sides (where pushed-off
    /// wrestlers land), a warm gridded backdrop, and the hanging tsuriyane roof.
    ///
    /// Scenes are BAKED: an editor pass calls Build() once and saves the
    /// children into the scene (sprites/materials become assets under
    /// Assets/Art/Generated), so every piece can be inspected and moved in the
    /// editor. At runtime Awake() only rebinds references when children
    /// already exist; an empty arena object still builds itself as before.
    public sealed class Systems_SumoArena : MonoBehaviour
    {
        // Widths doubled with the fighting ring. The dressing built from these
        // (roof, ridge, arena floor, crowd) is BAKED into the scene, so changing
        // them requires re-running Build() and re-saving — SetPlatformHalfWidth
        // only rescales the platform, surface, lip and tawara at runtime.
        public float ringHalfWidth = 4.9f;   // tawara position
        public float groundWidth = 11f;      // platform width (edge = +/- groundWidth/2)
        public float platformDrop = 0.6f;    // how far down the crowd floor sits
        public float floorWidth = 34f;       // lower arena floor span
        public bool showPosts = true;        // tawara + dressing on/off
        public bool deluxe = false;          // full-stadium dressing (SCN_SUMO)
        [Tooltip("Dohyo top-surface friction. Clay is 0.9; the training referee still randomises this per round.")]
        public float surfaceFriction = 0.9f;

        // Tawara bales, kept so width changes reposition them: (transform, side, index).
        private struct TawaraRef { public Transform t; public int side; public int index; }
        private readonly List<TawaraRef> _tawara = new List<TawaraRef>();
        private Transform _platform, _surface, _baseLip;
        private float _currentHalf = -1f;

        /// The dohyo's CURRENT half-width in metres. Anything drawn against the
        /// mat edge has to read this rather than keep its own copy: the manager
        /// sizes the mat from GameTuning after the fighters are built, the
        /// walk-in widens it, and the engage contracts it again.
        public float CurrentHalfWidth => _currentHalf > 0f ? _currentHalf : groundWidth * 0.5f;

        private static readonly Dictionary<string, Sprite> RuntimeSprites = new Dictionary<string, Sprite>();

        /// Physically resizes the dohyo to the given half-width: platform
        /// visuals AND collider change together, and the tawara ride the
        /// moving edge. Used by the training referee's per-round platform
        /// width randomization.
        public void SetPlatformHalfWidth(float half)
        {
            half = Mathf.Clamp(half, 0.1f, groundWidth * 0.5f);
            if (Mathf.Abs(half - _currentHalf) < 0.0005f) return;
            _currentHalf = half;

            float width = half * 2f;
            if (_platform != null)
                _platform.localScale = new Vector3(width, platformDrop, 1f);
            if (_surface != null)
                _surface.localScale = new Vector3(width, 0.08f, 1f);
            if (_baseLip != null)
                _baseLip.localScale = new Vector3(width + 0.7f, 0.16f, 1f);

            EnsureTawaraBands(half);

            float tawaraHalf = half - 0.3f; // bales stay on the clay, at the edge
            for (int tawaraIndex = 0; tawaraIndex < _tawara.Count; tawaraIndex++)
            {
                var r = _tawara[tawaraIndex];
                var p = r.t.localPosition;
                r.t.localPosition = new Vector3(r.side * (tawaraHalf - r.index * 0.24f), p.y, p.z);
            }
        }

        // --- Sprite / material sources ------------------------------------
        // Edit mode: every generated texture is written to Assets/Art/Generated
        // and referenced as a real asset, so baked scenes survive reload.
        // Play mode (unbaked fallback): generated in memory and cached.

        private static Sprite SourceSprite(string name, float ppu, bool repeat, Vector2 pivot, System.Func<Texture2D> gen)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                const string dir = "Assets/Art/Generated";
                string path = $"{dir}/{name}.png";
                var existing = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                if (existing != null) return existing;
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllBytes(path, gen().EncodeToPNG());
                AssetDatabase.ImportAsset(path);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.textureType = TextureImporterType.Sprite;
                importer.spritePixelsPerUnit = ppu;
                importer.wrapMode = repeat ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
                importer.mipmapEnabled = false;
                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);
                settings.spriteMeshType = SpriteMeshType.FullRect;
                settings.spriteAlignment = (int)SpriteAlignment.Custom;
                settings.spritePivot = pivot;
                importer.SetTextureSettings(settings);
                importer.SaveAndReimport();
                return AssetDatabase.LoadAssetAtPath<Sprite>(path);
            }
#endif
            if (RuntimeSprites.TryGetValue(name, out var cached) && cached != null) return cached;
            var tex = gen();
            if (repeat) tex.wrapMode = TextureWrapMode.Repeat;
            var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), pivot, ppu,
                                       0, SpriteMeshType.FullRect);
            RuntimeSprites[name] = sprite;
            return sprite;
        }

        private static PhysicsMaterial2D SourcePhysMat(string name, float friction, float bounciness)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                const string dir = "Assets/Art/Generated";
                string path = $"{dir}/{name}.physicsMaterial2D";
                var existing = AssetDatabase.LoadAssetAtPath<PhysicsMaterial2D>(path);
                if (existing != null) return existing;
                System.IO.Directory.CreateDirectory(dir);
                var created = new PhysicsMaterial2D(name) { friction = friction, bounciness = bounciness };
                AssetDatabase.CreateAsset(created, path);
                return created;
            }
#endif
            return new PhysicsMaterial2D(name) { friction = friction, bounciness = bounciness };
        }

        // Runtime-owned copy of the platform material. Created on first use and
        // reused, so repeated friction changes cost nothing and leak nothing.
        private PhysicsMaterial2D _platformMatInstance;

        /// Sets the dohyo top-surface friction: the training referee's per-round
        /// domain randomization, and the game's one-time apply from GameTuning.
        ///
        /// Mutates an INSTANCE, never the material it was given. In a baked scene
        /// col.sharedMaterial is the project asset Ground_Clay.physicsMaterial2D,
        /// and writing through to it edited the asset itself — during Play mode,
        /// where asset edits are not reverted on exit. That is not theoretical:
        /// Build() writes that asset at the component default 0.9, yet it is
        /// committed at 0.55, which is GameTuning.surfaceFriction, arrived at via
        /// this method.
        public void SetSurfaceFriction(float friction)
        {
            surfaceFriction = friction;
            if (_platform == null) return;
            var col = _platform.GetComponent<BoxCollider2D>();
            if (col == null) return;
#if UNITY_EDITOR
            // Edit mode keeps the shared asset on the collider: Build() bakes that
            // reference into the scene, and swapping in a scene-local instance
            // here would leave the saved scene pointing at nothing after reload.
            // Only Build() authors the material outside Play mode.
            if (!Application.isPlaying) return;
#endif

            if (_platformMatInstance == null)
            {
                _platformMatInstance = new PhysicsMaterial2D("Ground_Clay_Runtime")
                {
                    bounciness = col.sharedMaterial != null ? col.sharedMaterial.bounciness : 0f,
                };
            }
            _platformMatInstance.friction = friction;
            col.sharedMaterial = _platformMatInstance;
        }

        private void OnDestroy()
        {
            // Script-created, so nothing else will ever collect it.
            if (_platformMatInstance != null) Destroy(_platformMatInstance);
            if (_tawaraMatInstance != null) Destroy(_tawaraMatInstance);
        }

        [Tooltip("Width in metres of the slick tawara band at each rim. 0 disables it.")]
        public float tawaraBandWidth = 0.7f;
        [Tooltip("Friction inside the tawara band.")]
        public float tawaraFriction = 0.18f;

        private Transform _bandLeft, _bandRight;
        private PhysicsMaterial2D _tawaraMatInstance;

        /// The band material, resolved once. In edit mode this is the shared
        /// project asset (so a baked scene keeps referencing it); at runtime it is
        /// a single owned instance whose friction tracks tawaraFriction.
        private PhysicsMaterial2D TawaraMaterial()
        {
            if (_tawaraMatInstance != null)
            {
                _tawaraMatInstance.friction = tawaraFriction;
                return _tawaraMatInstance;
            }
            PhysicsMaterial2D material = SourcePhysMat("Ground_Tawara", tawaraFriction, 0f);
#if UNITY_EDITOR
            if (!Application.isPlaying) return material;   // the shared asset
#endif
            _tawaraMatInstance = material;
            return _tawaraMatInstance;
        }

        /// The bales, as physics rather than decoration.
        ///
        /// A fighter driven to the rim used to plant on the same 0.9-friction clay
        /// as the middle of the ring and simply stop, which is a large part of why
        /// ring-outs never happened. These two strips sit 5 mm proud of the clay at
        /// each edge, so a foot that reaches them contacts the slick material first
        /// and the fighter slides out instead of gripping.
        public void EnsureTawaraBands(float half)
        {
            if (tawaraBandWidth <= 0.001f)
            {
                if (_bandLeft != null) _bandLeft.gameObject.SetActive(false);
                if (_bandRight != null) _bandRight.gameObject.SetActive(false);
                return;
            }
            _bandLeft = Band(_bandLeft, "TawaraBandLeft", -1f, half);
            _bandRight = Band(_bandRight, "TawaraBandRight", 1f, half);
        }

        private Transform Band(Transform existing, string name, float side, float half)
        {
            Transform t = existing;
            if (t == null)
            {
                Transform found = transform.Find(name);
                if (found != null)
                {
                    t = found;
                }
                else
                {
                    var go = new GameObject(name);
                    go.transform.SetParent(transform, false);
                    var col = go.AddComponent<BoxCollider2D>();
                    col.size = Vector2.one;
                    t = go.transform;
                }
            }
            t.gameObject.SetActive(true);

            var box = t.GetComponent<BoxCollider2D>();
            if (box != null)
            {
                // Resolved ONCE and cached. At runtime SourcePhysMat takes the
                // `new PhysicsMaterial2D` branch, so calling it per band per call
                // allocated two native objects every time the referee randomized
                // the platform width — every round, per arena, per worker, none of
                // them ever collected in a headless env that never reloads a scene.
                box.sharedMaterial = TawaraMaterial();
            }
            // Centred on the outer band, top edge 5 mm above the clay so it wins the
            // contact against the platform collider it overlaps.
            float centre = side * (half - tawaraBandWidth * 0.5f);
            t.localScale = new Vector3(tawaraBandWidth, 0.08f, 1f);
            t.localPosition = new Vector3(centre, 0.005f - 0.04f, 0.01f);
            return t;
        }

        private static Sprite Box() => SourceSprite("Box", 4f, false, new Vector2(0.5f, 0.5f), () =>
        {
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            var px = new Color[16];
            for (int index = 0; index < 16; index++) px[index] = Color.white;
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        });

        private static Sprite Circle() => SourceSprite("Circle", 64f, false, new Vector2(0.5f, 0.5f), () =>
        {
            const int S = 64;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            float r = S / 2f - 1f;
            for (int rowIndex = 0; rowIndex < S; rowIndex++)
                for (int columnIndex = 0; columnIndex < S; columnIndex++)
                {
                    float d = Vector2.Distance(new Vector2(columnIndex + 0.5f, rowIndex + 0.5f), new Vector2(S / 2f, S / 2f));
                    tex.SetPixel(columnIndex, rowIndex, d <= r ? Color.white : Color.clear);
                }
            tex.Apply();
            return tex;
        });

        private Sprite NoiseSprite(Color baseColor, float amount) =>
            SourceSprite("Noise_Clay", 256f, true, new Vector2(0.5f, 0.5f), () =>
        {
            const int S = 256;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            for (int rowIndex = 0; rowIndex < S; rowIndex++)
                for (int columnIndex = 0; columnIndex < S; columnIndex++)
                {
                    float n = Mathf.PerlinNoise(columnIndex * 0.11f, rowIndex * 0.11f)
                            + 0.5f * Mathf.PerlinNoise(columnIndex * 0.31f, rowIndex * 0.31f);
                    float v = 1f + (n / 1.5f - 0.5f) * 2f * amount;
                    tex.SetPixel(columnIndex, rowIndex, new Color(baseColor.r * v, baseColor.g * v, baseColor.b * v, 1f));
                }
            tex.Apply();
            return tex;
        });

        private static Sprite GradientSprite(Color top, Color bottom) =>
            SourceSprite("WallGradient", 128f, false, new Vector2(0.5f, 0.5f), () =>
        {
            const int S = 128;
            var tex = new Texture2D(4, S, TextureFormat.RGBA32, false);
            for (int rowIndex = 0; rowIndex < S; rowIndex++)
            {
                var c = Color.Lerp(bottom, top, (float)rowIndex / (S - 1));
                for (int columnIndex = 0; columnIndex < 4; columnIndex++) tex.SetPixel(columnIndex, rowIndex, c);
            }
            tex.Apply();
            return tex;
        });

        private static Sprite ConeSprite() =>
            SourceSprite("SpotCone", 128f, false, new Vector2(0.5f, 0f), () =>
        {
            const int S = 128;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            for (int rowIndex = 0; rowIndex < S; rowIndex++)
                for (int columnIndex = 0; columnIndex < S; columnIndex++)
                {
                    // Triangle widening toward the bottom, soft edges, fades upward.
                    float ny = (float)rowIndex / (S - 1);              // 0 bottom, 1 top
                    float halfW = Mathf.Lerp(0.5f, 0.06f, ny);  // wide at bottom
                    float dx = Mathf.Abs((float)columnIndex / (S - 1) - 0.5f);
                    float a = dx < halfW ? (1f - dx / halfW) * (1f - ny) : 0f;
                    tex.SetPixel(columnIndex, rowIndex, new Color(1f, 0.95f, 0.8f, a * 0.16f));
                }
            tex.Apply();
            return tex;
        });

        private Sprite GridSprite() =>
            SourceSprite(deluxe ? "Grid_Deluxe" : "Grid_Plain", 100f, true, new Vector2(0.5f, 0.5f), () =>
        {
            const int PX = 500;
            var tex = new Texture2D(PX, PX, TextureFormat.RGBA32, false);
            var bg = deluxe ? new Color(0f, 0f, 0f, 0f) : new Color(0.17f, 0.13f, 0.12f, 1f);
            var thin = deluxe ? new Color(0.62f, 0.54f, 0.45f, 0.38f) : new Color(0.24f, 0.19f, 0.17f, 1f);
            var heavy = deluxe ? new Color(0.75f, 0.65f, 0.52f, 0.55f) : new Color(0.36f, 0.29f, 0.25f, 1f);
            for (int rowIndex = 0; rowIndex < PX; rowIndex++)
                for (int columnIndex = 0; columnIndex < PX; columnIndex++)
                {
                    bool heavyLine = columnIndex < 3 || rowIndex < 3 || columnIndex >= PX - 3 || rowIndex >= PX - 3;
                    bool thinLine = (columnIndex % 100) < 2 || (rowIndex % 100) < 2;
                    tex.SetPixel(columnIndex, rowIndex, heavyLine ? heavy : (thinLine ? thin : bg));
                }
            tex.Apply();
            return tex;
        });

        private void Awake()
        {
            // Baked scene: the children already exist in the editor — only
            // rebind the references SetVisualRing needs.
            if (transform.childCount > 0)
            {
                RebindBaked();
                ApplyLitMaterial();
                return;
            }
            Build();
            ApplyLitMaterial();
        }

        /// Put every renderer the ARENA owns onto the lit material so the 2D light
        /// rig reaches the dohyo, crowd and backdrop.
        ///
        /// Systems_ArenaLighting used to do this by sweeping every SpriteRenderer in
        /// the scene, which meant it also claimed renderers it did not own — the
        /// wrestlers' body materials and the light shafts both got overwritten, and
        /// each needed an exception carved out. Scoped to this subtree, the arena
        /// owns its own look and nothing else has to defend against it.
        private void ApplyLitMaterial()
        {
            Material lit = Systems_ArenaLighting.LitSpriteMaterial();
            if (lit == null)
            {
                return;
            }
            var renderers = GetComponentsInChildren<SpriteRenderer>(true);
            for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                renderers[rendererIndex].sharedMaterial = lit;
            }
        }

        private void RebindBaked()
        {
            _tawara.Clear();
            _currentHalf = groundWidth * 0.5f;
            foreach (Transform child in transform)
            {
                string n = child.name;
                if (n.StartsWith("TawaraLeft"))
                {
                    _tawara.Add(new TawaraRef { t = child, side = -1, index = n[n.Length - 1] - '0' });
                }
                else if (n.StartsWith("TawaraRight"))
                {
                    _tawara.Add(new TawaraRef { t = child, side = 1, index = n[n.Length - 1] - '0' });
                }
                else if (n == "DohyoPlatform") _platform = child;
                else if (n == "ClaySurface") _surface = child;
                else if (n == "DohyoBase") _baseLip = child;
            }
        }

        /// Creates the full arena as child objects. Called by Awake() when the
        /// scene isn't baked, and by the editor bake pass (which then saves
        /// the children into the scene).
        public void Build()
        {
            var clay = new Color(0.72f, 0.57f, 0.4f);
            var clayLight = new Color(0.8f, 0.66f, 0.47f);
            var clayDark = new Color(0.55f, 0.42f, 0.3f);

            // --- Dohyo platform: top surface at y=0, sides slightly stepped.
            float half = groundWidth * 0.5f;
            _currentHalf = half;
            var platform = new GameObject("DohyoPlatform");
            _platform = platform.transform;
            platform.transform.SetParent(transform, false);
            platform.transform.localPosition = new Vector3(0f, -platformDrop * 0.5f, 0f);
            platform.transform.localScale = new Vector3(groundWidth, platformDrop, 1f);
            var psr = platform.AddComponent<SpriteRenderer>();
            psr.sprite = deluxe ? NoiseSprite(clay, 0.06f) : Box();
            psr.color = deluxe ? Color.white : clay;
            psr.sortingOrder = -5;
            var pcol = platform.AddComponent<BoxCollider2D>();
            pcol.sharedMaterial = SourcePhysMat("Ground_Clay", surfaceFriction, 0f);

            // Slightly wider base lip to suggest the mound shape.
            var lip = new GameObject("DohyoBase");
            _baseLip = lip.transform;
            lip.transform.SetParent(transform, false);
            lip.transform.localPosition = new Vector3(0f, -platformDrop + 0.08f, 0f);
            lip.transform.localScale = new Vector3(groundWidth + 0.7f, 0.16f, 1f);
            var lsr0 = lip.AddComponent<SpriteRenderer>();
            lsr0.sprite = Box();
            lsr0.color = clayDark;
            lsr0.sortingOrder = -4;

            // Packed-clay surface highlight.
            var surface = new GameObject("ClaySurface");
            _surface = surface.transform;
            surface.transform.SetParent(transform, false);
            surface.transform.localPosition = new Vector3(0f, -0.04f, 0f);
            surface.transform.localScale = new Vector3(groundWidth, 0.08f, 1f);
            var ssr = surface.AddComponent<SpriteRenderer>();
            ssr.sprite = Box();
            ssr.color = clayLight;
            ssr.sortingOrder = -4;

            // --- Lower arena floor (crowd level) + collider so fallers land.
            var floor = new GameObject("ArenaFloor");
            floor.transform.SetParent(transform, false);
            floor.transform.localPosition = new Vector3(0f, -platformDrop - 0.5f, 0f);
            floor.transform.localScale = new Vector3(floorWidth, 1f, 1f);
            var fsr = floor.AddComponent<SpriteRenderer>();
            fsr.sprite = Box();
            fsr.color = new Color(0.24f, 0.18f, 0.15f);
            fsr.sortingOrder = -6;
            var fcol = floor.AddComponent<BoxCollider2D>();
            fcol.sharedMaterial = SourcePhysMat("Floor", 0.8f, 0f);

            // --- Shikiri-sen: the two white starting lines at ring center.
            for (int s = -1; s <= 1; s += 2)
            {
                var line = new GameObject(s < 0 ? "ShikiriLeft" : "ShikiriRight");
                line.transform.SetParent(transform, false);
                line.transform.localPosition = new Vector3(s * 0.35f, 0.015f, 0f);
                line.transform.localScale = new Vector3(0.25f, 0.035f, 1f);
                var lsr = line.AddComponent<SpriteRenderer>();
                lsr.sprite = Box();
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
            var grid = new GameObject("Grid");
            grid.transform.SetParent(transform, false);
            grid.transform.localPosition = new Vector3(0f, 4.4f, 0f);
            var gsr2 = grid.AddComponent<SpriteRenderer>();
            gsr2.sprite = GridSprite();
            gsr2.drawMode = SpriteDrawMode.Tiled;
            gsr2.size = new Vector2(floorWidth, 12f);
            gsr2.sortingOrder = -10;

            if (!showPosts) return;

            // --- Back gallery: a full-width row of darkened spectators behind
            // the fight so the portrait crop doesn't read as an empty wall.
            var galleryKimono = new[]
            {
                new Color(0.25f, 0.18f, 0.28f), new Color(0.17f, 0.24f, 0.3f),
                new Color(0.3f, 0.2f, 0.17f), new Color(0.2f, 0.27f, 0.21f),
                new Color(0.28f, 0.25f, 0.18f)
            };
            int gallerySeats = Mathf.FloorToInt(floorWidth / 0.6f);
            for (int gallerySeatIndex = 0; gallerySeatIndex < gallerySeats; gallerySeatIndex++)
            {
                float gx = -floorWidth * 0.5f + 0.4f + gallerySeatIndex * 0.6f;
                float jitter = ((gallerySeatIndex * 13) % 5) * 0.025f;
                var c = galleryKimono[(gallerySeatIndex * 7) % galleryKimono.Length];

                var gbody = new GameObject("GallerySpectator");
                gbody.transform.SetParent(transform, false);
                gbody.transform.localPosition = new Vector3(gx, 0.3f + jitter, 0f);
                gbody.transform.localScale = new Vector3(0.34f, 0.46f, 1f);
                var gsr = gbody.AddComponent<SpriteRenderer>();
                gsr.sprite = Box();
                gsr.color = new Color(c.r, c.g, c.b, 0.85f);
                gsr.sortingOrder = -9;

                var ghead = new GameObject("Head");
                ghead.transform.SetParent(gbody.transform, false);
                ghead.transform.localScale = new Vector3(0.2f / 0.34f, 0.2f / 0.46f, 1f);
                ghead.transform.localPosition = new Vector3(0f, 0.72f, 0f);
                var ghsr = ghead.AddComponent<SpriteRenderer>();
                ghsr.sprite = Circle();
                ghsr.color = new Color(0.5f, 0.42f, 0.36f, 0.85f);
                ghsr.sortingOrder = -9;
            }

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
                    bsr.sprite = Box();
                    bsr.color = b == 0 ? strawDark : straw;
                    bsr.sortingOrder = -3 + b;
                    _tawara.Add(new TawaraRef { t = bale.transform, side = s, index = b });
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
                        stsr.sprite = Box();
                        stsr.color = new Color(0.2f, 0.15f, 0.13f);
                        stsr.sortingOrder = -8;
                    }
                    for (int seatIndex = 0; seatIndex < seats; seatIndex++)
                    {
                        float px = s * (startX + seatIndex * 0.55f);
                        int idx = (s + 1) * 7 + row * 13 + seatIndex * 5;
                        var c = kimono[idx % kimono.Length];
                        if (row == 1) c *= 0.75f;

                        var body = new GameObject("Spectator");
                        body.transform.SetParent(transform, false);
                        body.transform.localPosition = new Vector3(px, rowY + 0.19f, 0f);
                        body.transform.localScale = new Vector3(0.34f, 0.38f, 1f);
                        var bsr2 = body.AddComponent<SpriteRenderer>();
                        bsr2.sprite = Box();
                        bsr2.color = new Color(c.r, c.g, c.b, 1f);
                        bsr2.sortingOrder = row == 0 ? -6 : -7;

                        var head = new GameObject("Head");
                        head.transform.SetParent(body.transform, false);
                        head.transform.localScale = new Vector3(0.2f / 0.34f, 0.2f / 0.38f, 1f);
                        head.transform.localPosition = new Vector3(0f, 0.75f, 0f);
                        var hsr = head.AddComponent<SpriteRenderer>();
                        hsr.sprite = Circle();
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
                for (int index = 0; index < 5; index++)
                {
                    float lx = Mathf.Lerp(-(half + 1.2f), half + 1.2f, index / 4f);
                    var halo = new GameObject("LanternHalo");
                    halo.transform.SetParent(transform, false);
                    halo.transform.localPosition = new Vector3(lx, 5.55f, 0f);
                    halo.transform.localScale = new Vector3(0.55f, 0.55f, 1f);
                    var hsr2 = halo.AddComponent<SpriteRenderer>();
                    hsr2.sprite = Circle();
                    hsr2.color = new Color(1f, 0.8f, 0.45f, 0.18f);
                    hsr2.sortingOrder = -6;
                    var lamp = new GameObject("Lantern");
                    lamp.transform.SetParent(transform, false);
                    lamp.transform.localPosition = new Vector3(lx, 5.55f, 0f);
                    lamp.transform.localScale = new Vector3(0.2f, 0.26f, 1f);
                    var lsr2 = lamp.AddComponent<SpriteRenderer>();
                    lsr2.sprite = Circle();
                    lsr2.color = new Color(1f, 0.85f, 0.55f, 0.95f);
                    lsr2.sortingOrder = -5;
                }

                // Wall banners.
                var bannerCols = new[]
                {
                    new Color(0.55f, 0.16f, 0.14f), new Color(0.16f, 0.3f, 0.45f),
                    new Color(0.5f, 0.38f, 0.12f), new Color(0.2f, 0.38f, 0.24f)
                };
                for (int index = 0; index < 4; index++)
                {
                    float bx = Mathf.Lerp(-(half + 4.2f), half + 4.2f, index / 3f);
                    var banner = new GameObject("Banner");
                    banner.transform.SetParent(transform, false);
                    banner.transform.localPosition = new Vector3(bx, 3.6f, 0f);
                    banner.transform.localScale = new Vector3(0.55f, 1.5f, 1f);
                    var bsr3 = banner.AddComponent<SpriteRenderer>();
                    bsr3.sprite = Box();
                    bsr3.color = bannerCols[index % bannerCols.Length];
                    bsr3.sortingOrder = -9;
                    var motif = new GameObject("Motif");
                    motif.transform.SetParent(banner.transform, false);
                    motif.transform.localScale = new Vector3(0.6f, 0.18f, 1f);
                    motif.transform.localPosition = new Vector3(0f, 0.22f, 0f);
                    var msr = motif.AddComponent<SpriteRenderer>();
                    msr.sprite = Box();
                    msr.color = new Color(0.94f, 0.9f, 0.82f, 0.9f);
                    msr.sortingOrder = -8;
                }

                // Distant back-row crowd silhouettes on both sides.
                for (int s = -1; s <= 1; s += 2)
                {
                    for (int index = 0; index < 8; index++)
                    {
                        float px = s * (half + 0.7f + index * 0.42f);
                        var sil = new GameObject("CrowdSilhouette");
                        sil.transform.SetParent(transform, false);
                        sil.transform.localPosition = new Vector3(px, -platformDrop + 0.62f, 0f);
                        sil.transform.localScale = new Vector3(0.3f, 0.34f, 1f);
                        var ssr3 = sil.AddComponent<SpriteRenderer>();
                        ssr3.sprite = Circle();
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
            rsr.sprite = Box();
            rsr.color = new Color(0.3f, 0.17f, 0.13f);
            rsr.sortingOrder = -6;
            var ridge = new GameObject("TsuriyaneRidge");
            ridge.transform.SetParent(transform, false);
            ridge.transform.localPosition = new Vector3(0f, 6.77f, 0f);
            ridge.transform.localScale = new Vector3(groundWidth + 4.3f, 0.16f, 1f);
            var rid = ridge.AddComponent<SpriteRenderer>();
            rid.sprite = Box();
            rid.color = new Color(0.2f, 0.11f, 0.09f);
            rid.sortingOrder = -6;
            var fusa = new[]
            {
                new Color(0.12f, 0.12f, 0.12f), new Color(0.2f, 0.5f, 0.28f),
                new Color(0.75f, 0.2f, 0.16f), new Color(0.92f, 0.9f, 0.85f)
            };
            for (int index = 0; index < 4; index++)
            {
                float fx = Mathf.Lerp(-(half + 1.4f), half + 1.4f, index / 3f);
                var tassel = new GameObject("Fusa" + index);
                tassel.transform.SetParent(transform, false);
                tassel.transform.localPosition = new Vector3(fx, 5.9f, 0f);
                tassel.transform.localScale = new Vector3(0.12f, 0.5f, 1f);
                var tsr = tassel.AddComponent<SpriteRenderer>();
                tsr.sprite = Box();
                tsr.color = fusa[index];
                tsr.sortingOrder = -5;
            }
        }
    }
}
