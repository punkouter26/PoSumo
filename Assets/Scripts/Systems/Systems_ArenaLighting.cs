using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace PoSumo
{
    /// Builds the arena's 2D lighting rig and post-processing stack at runtime,
    /// the same way Systems_SumoArena builds the dohyo and Agent_BipedBody builds
    /// the ragdoll — so no scene has to store any of it and every arena, old or
    /// new, picks it up automatically.
    ///
    /// The project already shipped a Renderer2D with four light blend styles and a
    /// volume profile full of effects; neither was ever used, so everything
    /// rendered flat and unlit. This turns both on.
    ///
    /// Lighting is deliberately conservative: a bright warm global fill means the
    /// scene never goes dark if a light fails to spawn, and the key/rim lights
    /// shape on top of it rather than being the only illumination.
    public sealed class Systems_ArenaLighting : MonoBehaviour
    {
        [Header("Global fill")]
        [Tooltip("Base illumination everything receives. 1 = as bright as the old unlit look.")]
        public float globalIntensity = 0.95f;
        public Color globalColor = new Color(1f, 0.95f, 0.88f);

        [Header("Key light (above the dohyo)")]
        public float keyIntensity = 1.15f;
        public Color keyColor = new Color(1f, 0.93f, 0.78f);
        public float keyHeight = 5.2f;
        public float keyOuterRadius = 7.5f;

        [Header("Rim lights (crowd side)")]
        public float rimIntensity = 0.85f;
        public Color rimColor = new Color(0.45f, 0.62f, 1f);
        public float rimSpread = 4.2f;

        [Header("Post-processing")]
        public bool enablePost = true;
        public float bloomIntensity = 0.85f;
        public float bloomThreshold = 0.82f;
        public float vignette = 0.26f;
        public float contrast = 12f;
        public float saturation = 10f;
        public float grain = 0.18f;

        private static Material _litSprite;

        /// Shared lit-sprite material. Everything drawn in the arena uses this one
        /// instance so the 2D lights apply and the sprites still batch together.
        public static Material LitSpriteMaterial()
        {
            if (_litSprite == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Lit-Default");
                // Falling back to the unlit default keeps the game rendering (just
                // flat, as before) on any pipeline where the 2D lit shader is absent.
                if (shader == null)
                {
                    shader = Shader.Find("Sprites/Default");
                }
                _litSprite = new Material(shader) { name = "PoSumo_LitSprite" };

                // A cylindrical normal map turns every flat quad into a rounded
                // tube under the 2D lights: limbs and torso pick up a highlight
                // down the middle and fall off at the edges, which is what makes
                // primitives read as bodies instead of coloured paper.
                Texture2D normals = CylinderNormalMap();
                if (_litSprite.HasProperty(NormalMapId))
                {
                    _litSprite.SetTexture(NormalMapId, normals);
                    _litSprite.EnableKeyword("_NORMALMAP");
                    _litSprite.EnableKeyword("USE_NORMAL_MAP");
                }
            }
            return _litSprite;
        }

        private static readonly int NormalMapId = Shader.PropertyToID("_NormalMap");
        private static Texture2D _normalMap;

        /// Normal map of a horizontal cylinder: normals sweep from pointing left at
        /// the sprite's left edge, through straight out at the middle, to right at
        /// the right edge. Vertically flat, so a limb lights like a tube whichever
        /// way it is rotated.
        private static Texture2D CylinderNormalMap()
        {
            if (_normalMap != null)
            {
                return _normalMap;
            }
            const int S = 64;
            _normalMap = new Texture2D(S, S, TextureFormat.RGBA32, false, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "PoSumo_CylinderNormal",
            };
            for (int x = 0; x < S; x++)
            {
                // -1..1 across the sprite, then the unit-circle height gives z.
                float u = (x + 0.5f) / S * 2f - 1f;
                float nx = u * 0.85f;                      // 0.85: soften the extremes
                float nz = Mathf.Sqrt(Mathf.Max(0.0001f, 1f - nx * nx));
                var encoded = new Color(nx * 0.5f + 0.5f, 0.5f, nz * 0.5f + 0.5f, 1f);
                for (int y = 0; y < S; y++)
                {
                    _normalMap.SetPixel(x, y, encoded);
                }
            }
            _normalMap.Apply();
            return _normalMap;
        }

        private void Awake()
        {
            BuildLights();
            if (enablePost)
            {
                BuildPostProcessing();
            }
        }

        private void Start()
        {
            // The dohyo, crowd and backdrop are baked into the scene with the
            // default unlit sprite material, so they would ignore the rig entirely.
            // Convert them once, after the arena has finished building itself.
            ConvertSceneSpritesToLit();
        }

        private static void ConvertSceneSpritesToLit()
        {
            Material lit = LitSpriteMaterial();
            SpriteRenderer[] renderers =
                FindObjectsByType<SpriteRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            int converted = 0;
            for (int i = 0; i < renderers.Length; i++)
            {
                SpriteRenderer sr = renderers[i];
                if (sr.sharedMaterial == lit)
                {
                    continue;
                }
                sr.sharedMaterial = lit;
                converted++;
            }
            Debug.Log($"Systems_ArenaLighting: lit {converted} sprite renderers.");
        }

        private void BuildLights()
        {
            float centerX = transform.position.x;

            CreateLight("Light_Global", Light2D.LightType.Global, globalColor, globalIntensity,
                        Vector3.zero, 0f, 0f);

            // Key: hangs where the tsuriyane roof would be, so the fighters are lit
            // from the same place the arena's own geometry implies.
            CreateLight("Light_Key", Light2D.LightType.Point, keyColor, keyIntensity,
                        new Vector3(centerX, keyHeight, 0f), 1.5f, keyOuterRadius);

            // Cool rims from the crowd, which separate the wrestlers from the
            // warm clay behind them.
            CreateLight("Light_RimL", Light2D.LightType.Point, rimColor, rimIntensity,
                        new Vector3(centerX - rimSpread, 1.6f, 0f), 0.5f, rimSpread * 1.6f);
            CreateLight("Light_RimR", Light2D.LightType.Point, rimColor, rimIntensity,
                        new Vector3(centerX + rimSpread, 1.6f, 0f), 0.5f, rimSpread * 1.6f);
        }

        private Light2D CreateLight(string name, Light2D.LightType type, Color colour, float intensity,
                                    Vector3 localPosition, float innerRadius, float outerRadius)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.position = localPosition;

            Light2D light = go.AddComponent<Light2D>();
            light.lightType = type;
            light.color = colour;
            light.intensity = intensity;
            if (type == Light2D.LightType.Point)
            {
                light.pointLightInnerRadius = innerRadius;
                light.pointLightOuterRadius = outerRadius;
                light.falloffIntensity = 0.65f;
            }
            return light;
        }

        private void BuildPostProcessing()
        {
            // The camera opts into post per-camera; without this the volume renders
            // nothing at all, which is why the shipped profile never showed up.
            Camera cam = Camera.main;
            if (cam != null)
            {
                UniversalAdditionalCameraData data = cam.GetUniversalAdditionalCameraData();
                if (data != null)
                {
                    data.renderPostProcessing = true;
                }
            }

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "ArenaVolumeProfile (runtime)";

            var tonemap = profile.Add<Tonemapping>();
            tonemap.mode.Override(TonemappingMode.Neutral);

            var bloomEffect = profile.Add<Bloom>();
            bloomEffect.intensity.Override(bloomIntensity);
            bloomEffect.threshold.Override(bloomThreshold);
            bloomEffect.scatter.Override(0.62f);
            bloomEffect.tint.Override(new Color(1f, 0.92f, 0.78f));

            var vignetteEffect = profile.Add<Vignette>();
            vignetteEffect.intensity.Override(vignette);
            vignetteEffect.smoothness.Override(0.45f);

            var colour = profile.Add<ColorAdjustments>();
            colour.contrast.Override(contrast);
            colour.saturation.Override(saturation);
            colour.postExposure.Override(0.12f);

            var split = profile.Add<SplitToning>();
            split.shadows.Override(new Color(0.22f, 0.3f, 0.5f));
            split.highlights.Override(new Color(1f, 0.86f, 0.62f));
            split.balance.Override(-8f);

            var grainEffect = profile.Add<FilmGrain>();
            grainEffect.type.Override(FilmGrainLookup.Medium1);
            grainEffect.intensity.Override(grain);
            grainEffect.response.Override(0.7f);

            var go = new GameObject("ArenaVolume");
            go.transform.SetParent(transform, false);
            Volume volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 1f;
            volume.sharedProfile = profile;
        }
    }
}
