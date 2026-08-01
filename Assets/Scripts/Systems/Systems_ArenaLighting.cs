using System.Reflection;
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
        // SHAPED LIGHTING. The global fill alone lights every sprite on its
        // target layers identically — no position, no falloff, and therefore no
        // shading anywhere. It is here to set the floor of the exposure, not to
        // carry the scene: the key point-light over the dohyo does the shaping,
        // the two rim lights separate the wrestlers from the crowd behind them,
        // and the key's light volume draws the visible cone under the tsuriyane.
        //
        // The global is deliberately BELOW 1 now. A normal-mapped tube lit by a
        // 1.35 global plus a key blows its centreline straight to white and the
        // bodies read as chrome — see the headroom term in PoSumo/BodyLit, which
        // shapes the additive highlights but cannot rescue an over-bright base.
        // SHADOW BUDGET — the ratio below is the whole trick, and getting it wrong
        // is why the first pass rendered a shadow pass that produced no visible
        // shadow. A Light2D shadow occludes ONLY the light that casts it. The key
        // is the sole caster; the global cannot be shadowed at all (Global type,
        // and DisableShadows is called on it), and the rims are explicitly
        // unshadowed. So the darkest a shadowed patch of clay can ever get is
        //     global + rims  /  global + rims + key
        // At the old 0.78 / 0.7 that floor was ~53% of full brightness — a shadow
        // that faint against warm clay is invisible. The key must DOMINATE the
        // unshadowed fill, not merely match it. Total exposure is held roughly
        // constant; what changes is which light carries it.
        [Header("Global fill")]
        [Tooltip("Base illumination everything receives, with no falloff anywhere. Deliberately low: this light can never be shadowed, so every unit of it is a unit that fills in the key's shadows.")]
        public float globalIntensity = 0.42f;
        public Color globalColor = new Color(1f, 0.97f, 0.93f);

        [Header("Key light (above the dohyo) — 0 disables the spotlight look")]
        [Tooltip("The only shadow-casting light. Carries the exposure so its shadows have something to subtract from.")]
        public float keyIntensity = 1.25f;
        public Color keyColor = new Color(1f, 0.93f, 0.78f);
        public float keyHeight = 5.2f;
        public float keyOuterRadius = 7.5f;

        [Header("Rim lights (crowd side) — 0 keeps the edges as bright as the middle")]
        [Tooltip("Also unshadowed, and they sit at fighter height pointing inward, so they fill the key's shadows more per unit than the global does. Kept low for that reason.")]
        public float rimIntensity = 0.2f;
        public Color rimColor = new Color(0.45f, 0.62f, 1f);
        public float rimSpread = 4.2f;

        [Header("Shadows")]
        // OFF, and this time the reason is measured rather than assumed. Cast
        // shadows were implemented properly (ShadowCaster2D per part, one
        // CompositeShadowCaster2D per fighter so they do not self-shadow) and the
        // light ratio below was rebalanced specifically so a shadow would have
        // something to subtract from. Three 1080x1920 live captures across
        // different match states then showed NO body-shaped shadow anywhere.
        //
        // The reason is the arena's geometry, not the shadow setup: at gameplay
        // framing the only clay in frame is the dohyo's FRONT FACE seen edge-on.
        // The fighters stand on its top edge, so there is no horizontal surface
        // for a shadow to fall across. This is almost certainly why shadows were
        // removed the first time too.
        //
        // Everything needed is still here and correct — flip this and
        // Agent_BipedBody.castShadows together if the camera ever frames the mat
        // from above, or if a floor plane becomes visible. Until then it is a
        // shadow render texture per frame, plus a second volumetric pass, for
        // nothing — and this ships to Android. Systems_BlobShadow already does
        // the contact grounding this geometry can actually show.
        [Tooltip("Key-light shadows. Needs Agent_BipedBody.castShadows on as well. Currently ineffective at gameplay framing — see the note above before enabling.")]
        public bool keyCastsShadows = false;
        [Range(0f, 1f)] public float shadowIntensity = 0.78f;
        public float shadowSoftness = 0.55f;

        [Header("Light volumes (god rays)")]
        [Tooltip("Volumetric glow on the key light — the visible cone under the tsuriyane. This IS the spotlight effect; it reads against the arena haze Systems_ArenaAtmosphere emits.")]
        [Range(0f, 1f)] public float keyVolumeIntensity = 0.35f;
        [Tooltip("Wrestlers carve dark shafts out of the cone as they move through it. Costs a second volumetric pass, so it stays well below keyVolumeIntensity.")]
        [Range(0f, 1f)] public float keyShadowVolumeIntensity = 0.18f;

        [Header("Post-processing")]
        public bool enablePost = true;
        public float bloomIntensity = 0.85f;
        public float bloomThreshold = 0.82f;
        // Screen-space darkening at the edge of frame. It doubles the world-space
        // drop-off the point lights produce rather than replacing it, so it stays
        // gentle — this is framing, not the lighting.
        public float vignette = 0.22f;
        public float contrast = 12f;
        public float saturation = 10f;
        public float grain = 0.18f;

        private static Material _litSprite;
        private static Shader _bodyShader;
        private static bool _bodyShaderMissingLogged;

        /// The most recently built rig. Systems_ArenaAtmosphere
        /// read the lights and the volume from here rather than hunting for them.
        public static Systems_ArenaLighting Instance { get; private set; }

        public Light2D GlobalLight { get; private set; }
        public Light2D KeyLight { get; private set; }
        public Volume PostVolume { get; private set; }
        public VolumeProfile PostProfile { get; private set; }

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

        /// A fresh PoSumo/BodyLit material for one wrestler. Per-fighter rather
        /// than shared, because Systems_BodySurface writes sweat and clay into it
        /// and the two wrestlers get dirty at different rates — one material per
        /// body still batches all 14 parts of that body together.
        public static Material CreateBodyMaterial(string materialName)
        {
            if (_bodyShader == null)
            {
                // Resources.Load, not Shader.Find. A shader that is only ever
                // reached through Shader.Find is referenced by nothing at build
                // time and gets stripped out of the player — it would work in the
                // editor and silently fall back to flat sprites on device. Living
                // under Resources/ is what guarantees it ships.
                _bodyShader = Resources.Load<Shader>("Shaders/PoSumo_BodyLit");
                if (_bodyShader == null)
                {
                    _bodyShader = Shader.Find("PoSumo/BodyLit");
                }
            }
            if (_bodyShader == null)
            {
                // Shader missing (not yet imported, or stripped): fall back to the
                // shared lit material so bodies still render, just without sweat,
                // clay or rim light.
                if (!_bodyShaderMissingLogged)
                {
                    _bodyShaderMissingLogged = true;
                    Debug.LogWarning("Systems_ArenaLighting: shader 'PoSumo/BodyLit' not found; " +
                                     "wrestlers fall back to the flat lit sprite material.");
                }
                return LitSpriteMaterial();
            }

            var material = new Material(_bodyShader) { name = materialName };

            if (FlatBodyShading)
            {
                // FLAT: no cylinder normal map, so the 2D lights see an even
                // surface and each part takes one solid tinted colour — the look
                // this project shipped with. The rim, subsurface and sweat terms
                // are zeroed rather than compiled out so the shader stays a single
                // variant and the look can be switched back at runtime.
                SetIfPresent(material, RimStrengthId, 0f);
                SetIfPresent(material, WrapWarmId, 0f);
                SetIfPresent(material, SweatId, 0f);
                return material;
            }

            if (material.HasProperty(NormalMapId))
            {
                material.SetTexture(NormalMapId, CylinderNormalMap());
                material.EnableKeyword("_NORMALMAP");
                material.EnableKeyword("USE_NORMAL_MAP");
            }
            return material;
        }

        /// True for any material this class hands out. Used to keep
        /// ConvertSceneSpritesToLit from overwriting a per-fighter body material
        /// with the shared scenery one.
        private static bool IsPoSumoLitMaterial(Material material)
        {
            if (material == null)
            {
                return false;
            }
            if (material == _litSprite)
            {
                return true;
            }
            return _bodyShader != null && material.shader == _bodyShader;
        }

        private static readonly int NormalMapId = Shader.PropertyToID("_NormalMap");
        private static readonly int RimStrengthId = Shader.PropertyToID("_RimStrength");
        private static readonly int WrapWarmId = Shader.PropertyToID("_WrapWarm");
        private static readonly int SweatId = Shader.PropertyToID("_Sweat");
        private static Texture2D _normalMap;

        /// True renders wrestlers as FLAT tinted shapes — no cylindrical shading,
        /// no rim light, no subsurface warmth, no sweat highlight. False is the
        /// rounded, shaded treatment, and is what the exposure above is balanced
        /// for (globalIntensity 0.78 / keyIntensity 0.7); raising those back
        /// toward 1.35 / 0 while this is false blows the lit centreline out to
        /// white and the bodies read as chrome.
        public static bool FlatBodyShading = false;

        private static void SetIfPresent(Material material, int propertyId, float value)
        {
            if (material.HasProperty(propertyId))
            {
                material.SetFloat(propertyId, value);
            }
        }

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
            for (int columnIndex = 0; columnIndex < S; columnIndex++)
            {
                // -1..1 across the sprite, then the unit-circle height gives z.
                float u = (columnIndex + 0.5f) / S * 2f - 1f;
                float nx = u * 0.85f;                      // 0.85: soften the extremes
                float nz = Mathf.Sqrt(Mathf.Max(0.0001f, 1f - nx * nx));
                var encoded = new Color(nx * 0.5f + 0.5f, 0.5f, nz * 0.5f + 0.5f, 1f);
                for (int rowIndex = 0; rowIndex < S; rowIndex++)
                {
                    _normalMap.SetPixel(columnIndex, rowIndex, encoded);
                }
            }
            _normalMap.Apply();
            return _normalMap;
        }

        private void Awake()
        {
            Instance = this;
            BuildLights();
            if (enablePost)
            {
                BuildPostProcessing();
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        // The scene-wide material sweep that used to live here is gone.
        //
        // It ran FindObjectsByType<SpriteRenderer> across the whole scene in Start
        // and reassigned sharedMaterial on everything it did not recognise. That is
        // an opt-OUT design: every new renderer had to be defended against it. It
        // already ate the per-fighter body materials once (hence an IsPoSumoLitMaterial
        // check bolted on) and it silently replaced the light shafts' material too.
        //
        // Arena scenery now takes the lit material from Systems_SumoArena as it is
        // built, and runtime-built objects (bodies, cloth, shafts) set their own.
        // Ownership sits with whoever creates the renderer, which is the only rule
        // that does not need a list of exceptions.

        private void BuildLights()
        {
            float centerX = transform.position.x;

            GlobalLight = CreateLight("Light_Global", Light2D.LightType.Global, globalColor, globalIntensity,
                                      Vector3.zero, 0f, 0f);

            // Key: hangs where the tsuriyane roof would be, so the fighters are lit
            // from the same place the arena's own geometry implies.
            KeyLight = CreateLight("Light_Key", Light2D.LightType.Point, keyColor, keyIntensity,
                                   new Vector3(centerX, keyHeight, 0f), 1.5f, keyOuterRadius);

            // Shadows come off the key light only. Two shadow-casting point lights
            // double the shadow pass for a second set of shadows nobody reads at
            // this camera distance.
            if (keyCastsShadows && KeyLight != null)
            {
                KeyLight.shadowsEnabled = true;
                KeyLight.shadowIntensity = shadowIntensity;
                KeyLight.shadowSoftness = shadowSoftness;
                KeyLight.shadowSoftnessFalloffIntensity = 0.5f;
            }

            // Volumetric glow: the visible cone of light under the roof. This is
            // the cheap version of god rays — no extra geometry, the light renders
            // its own falloff into the volume pass.
            if (keyVolumeIntensity > 0f && KeyLight != null)
            {
                KeyLight.volumetricEnabled = true;
                KeyLight.volumeIntensity = keyVolumeIntensity;
                if (keyCastsShadows && keyShadowVolumeIntensity > 0f)
                {
                    // Bodies carve dark wedges out of the light cone.
                    KeyLight.volumetricShadowsEnabled = true;
                    KeyLight.shadowVolumeIntensity = keyShadowVolumeIntensity;
                }
            }

            // Cool rims from the crowd, which separate the wrestlers from the
            // warm clay behind them.
            Light2D rimLeft = CreateLight("Light_RimL", Light2D.LightType.Point, rimColor, rimIntensity,
                        new Vector3(centerX - rimSpread, 1.6f, 0f), 0.5f, rimSpread * 1.6f);
            Light2D rimRight = CreateLight("Light_RimR", Light2D.LightType.Point, rimColor, rimIntensity,
                        new Vector3(centerX + rimSpread, 1.6f, 0f), 0.5f, rimSpread * 1.6f);

            // Light2D.m_ShadowsEnabled defaults to TRUE, so every light created in
            // code casts unless told not to. Three extra shadow passes for shadows
            // that fight the key's and read as mud.
            DisableShadows(GlobalLight);
            DisableShadows(rimLeft);
            DisableShadows(rimRight);
        }

        private static void DisableShadows(Light2D light)
        {
            if (light != null)
            {
                light.shadowsEnabled = false;
                light.volumetricShadowsEnabled = false;
            }
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
            EnableNormalMapping(light);
            return light;
        }

        private static FieldInfo _normalQualityField;
        private static FieldInfo _useNormalMapField;
        private static bool _normalReflectionResolved;
        private static bool _normalReflectionWarned;

        /// Light2D.m_NormalMapQuality defaults to Disabled and the public property
        /// is get-only — there is no supported way to switch normal mapping on for
        /// a light created in code. Without this the cylinder normal map above is
        /// sampled by nothing and every limb lights perfectly flat, which is
        /// exactly how the rig shipped.
        private static void EnableNormalMapping(Light2D light)
        {
            if (light == null)
            {
                return;
            }
            if (!_normalReflectionResolved)
            {
                _normalReflectionResolved = true;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                _normalQualityField = typeof(Light2D).GetField("m_NormalMapQuality", flags);
                _useNormalMapField = typeof(Light2D).GetField("m_UseNormalMap", flags);
            }
            if (_normalQualityField == null)
            {
                if (!_normalReflectionWarned)
                {
                    _normalReflectionWarned = true;
                    Debug.LogWarning("Systems_ArenaLighting: could not enable Light2D normal mapping " +
                                     "(m_NormalMapQuality not found on this URP version). Limbs will light flat.");
                }
                return;
            }
            // Accurate rather than Fast: the wrestlers fill a large part of a
            // portrait frame, which is the case Fast is explicitly not for.
            _normalQualityField.SetValue(light, Light2D.NormalMapQuality.Accurate);
            if (_useNormalMapField != null && _useNormalMapField.FieldType == typeof(bool))
            {
                // Legacy companion flag on older serialized layouts; a version
                // upgrade path resets quality to Disabled when this is false.
                _useNormalMapField.SetValue(light, true);
            }
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

            // Impact effects, parked at zero. Nothing drives these since Systems_PostFx
            // was removed; kept because the volume profile still declares them.
            // hits and finishes; they are declared here so the profile layout never
            // changes at runtime.
            //
            // NOTE: no DepthOfField. The 2D renderer does not write sprite depth,
            // so URP's DoF has nothing to work from — background separation is done
            // by tinting the backdrop layers in Systems_ArenaAtmosphere instead.
            var aberration = profile.Add<ChromaticAberration>();
            aberration.intensity.Override(0f);

            var distortion = profile.Add<LensDistortion>();
            distortion.intensity.Override(0f);
            distortion.scale.Override(1f);

            var go = new GameObject("ArenaVolume");
            go.transform.SetParent(transform, false);
            Volume volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 1f;
            volume.sharedProfile = profile;

            PostVolume = volume;
            PostProfile = profile;
        }
    }
}
