// Lit sprite shader for wrestler body parts.
//
// Structurally a copy of "Universal Render Pipeline/2D/Sprite-Lit-Default"
// (same three passes, same CBUFFER discipline) with four additions that turn a
// flat tinted capsule into something that reads as a body:
//
//   rim        - edge light from the silhouette, separates limbs from the clay
//   wrap       - warm subsurface-ish bounce through the middle of the tube
//   sweat      - specular band + sparkle down the lit centreline, driven by exertion
//   dirt       - clay staining that accumulates from the bottom of the part up
//   wet        - sheet-water darkening plus a tighter spec than sweat's
//   flash      - white-hot pulse on the frame a limb is struck
//   backlight  - broad warm silhouette wrap that separates two clinched bodies
//   detail     - procedural muscle banding along the limb's long axis
//
// The last four were added 2026-09-05. `detail` is procedural rather than a
// texture on purpose: it costs no extra fetch, and body detail here can ONLY be
// shading — the drawn edge is the colliding edge, and touching a collider
// invalidates all four brains with no trunk on disk to recover from.
//
// All four read the SAME cylindrical normal map Systems_ArenaLighting builds, so
// they cost one extra texture fetch: none. _Sweat and _Dirt are written per
// fighter by Systems_BodySurface (one material instance per wrestler, so SRP
// batching survives within a body).
//
// NOTE: every pass must declare an IDENTICAL UnityPerMaterial CBUFFER layout or
// the SRP Batcher silently drops this shader. Add properties to all three.

Shader "PoSumo/BodyLit"
{
    Properties
    {
        _MainTex("Diffuse", 2D) = "white" {}
        _MaskTex("Mask", 2D) = "white" {}
        _NormalMap("Normal Map", 2D) = "bump" {}
        [MaterialToggle] _ZWrite("ZWrite", Float) = 0

        [Header(Rim)]
        _RimColor("Rim Colour", Color) = (0.55, 0.7, 1.0, 1)
        _RimStrength("Rim Strength", Range(0, 3)) = 0.22
        _RimPower("Rim Power", Range(0.5, 8)) = 4.5

        [Header(Subsurface)]
        _WarmColor("Subsurface Tint", Color) = (1.0, 0.42, 0.3, 1)
        _WrapWarm("Subsurface Warmth", Range(0, 1)) = 0.25

        [Header(Sweat)]
        _SweatColor("Sweat Highlight", Color) = (1, 1, 1, 1)
        _Sweat("Sweat", Range(0, 1)) = 0

        [Header(Clay)]
        _DirtColor("Clay Colour", Color) = (0.58, 0.46, 0.32, 1)
        _Dirt("Clay Dirt", Range(0, 1)) = 0

        [Header(Wetness)]
        _Wet("Wetness", Range(0, 1)) = 0

        [Header(Hit Flash)]
        _FlashColor("Flash Colour", Color) = (1, 0.93, 0.82, 1)
        _Flash("Flash", Range(0, 1)) = 0

        [Header(Back Light)]
        _BackLightColor("Back Light Colour", Color) = (1.0, 0.72, 0.42, 1)
        _BackLight("Back Light", Range(0, 2)) = 0.35

        [Header(Muscle Detail)]
        _Detail("Detail Strength", Range(0, 1)) = 0.35
        _DetailScale("Detail Scale", Range(0.5, 8)) = 1.6

        // Legacy properties, kept so materials can fall back to the sprite shader.
        [HideInInspector] _Color("Tint", Color) = (1,1,1,1)
        [HideInInspector] _RendererColor("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _AlphaTex("External Alpha", 2D) = "white" {}
        [HideInInspector] _EnableExternalAlpha("Enable External Alpha", Float) = 0
    }

    SubShader
    {
        Tags {"Queue" = "Transparent" "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
        Cull Off
        ZWrite [_ZWrite]

        Pass
        {
            Tags { "LightMode" = "Universal2D" }

            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/Core2D.hlsl"

            #pragma vertex BodyVertex
            #pragma fragment BodyFragment

            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/ShapeLightShared.hlsl"

            #pragma multi_compile_instancing
            #pragma multi_compile _ DEBUG_DISPLAY
            #pragma multi_compile _ SKINNED_SPRITE

            struct Attributes
            {
                COMMON_2D_INPUTS
                half4 color        : COLOR;
                UNITY_SKINNED_VERTEX_INPUTS
            };

            struct Varyings
            {
                COMMON_2D_LIT_OUTPUTS
                half4 color        : COLOR;
            };

            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/Lit2DCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half4 _RimColor;
                half4 _WarmColor;
                half4 _SweatColor;
                half4 _DirtColor;
                half4 _FlashColor;
                half4 _BackLightColor;
                half _RimStrength;
                half _RimPower;
                half _WrapWarm;
                half _Sweat;
                half _Dirt;
                half _Wet;
                half _Flash;
                half _BackLight;
                half _Detail;
                half _DetailScale;
            CBUFFER_END

            // Cheap hash for grit and sweat sparkle. Deterministic per texel, so
            // the speckle sits still on the limb instead of crawling.
            half Hash21(half2 p)
            {
                return frac(sin(dot(p, half2(41.7h, 289.3h))) * 43758.5453h);
            }

            Varyings BodyVertex(Attributes input)
            {
                UNITY_SKINNED_VERTEX_COMPUTE(input);
                SetUpSpriteInstanceProperties();
                input.positionOS = UnityFlipSprite(input.positionOS, unity_SpriteProps.xy);

                Varyings o = CommonLitVertex(input);
                o.color = input.color * _Color * unity_SpriteColor;
                return o;
            }

            half4 BodyFragment(Varyings input) : SV_Target
            {
                half4 main = input.color * SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                const half4 mask = SAMPLE_TEXTURE2D(_MaskTex, sampler_MaskTex, input.uv);
                const half3 normalTS = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, input.uv));

                half grit = Hash21(input.uv * 128.0h);

                // Clay stains the underside first and climbs. The grit term keeps
                // the boundary from reading as a clean gradient wipe.
                half dirtMask = saturate(1.0h - input.uv.y * 1.7h) * _Dirt;
                dirtMask = saturate(dirtMask * (0.6h + 0.8h * grit));
                main.rgb = lerp(main.rgb, _DirtColor.rgb, dirtMask * 0.8h);

                // Muscle definition. Every part is a plain ellipse sprite over a
                // unit capsule, and the drawn edge MUST stay the colliding edge —
                // changing a collider invalidates all four brains and there is no
                // trunk on disk to warm-start a correction from. So body detail
                // can only ever be shading, never geometry, and this is where it
                // lives. Bands run ALONG the limb (uv.y is the long axis) and are
                // gated by the facing term so they fade out at the silhouette,
                // where the cylinder is edge-on and a band would read as a hard
                // painted line rather than as form.
                // NOTE the frequency DOUBLING: abs() folds the negative lobe, so
                // this produces 2 * _DetailScale bellies along the limb, not
                // _DetailScale of them. The first pass shipped a scale of 7 and
                // therefore drew FOURTEEN bands per part, which read as caterpillar
                // segmentation rather than as muscle — visible on a live close-up.
                // A limb has two or three bellies, so the useful range is ~1-2.
                half band = 1.0h - abs(sin(input.uv.y * _DetailScale * 6.2831h));
                half facingDetail = 1.0h - saturate(abs(normalTS.x));
                half muscle = (band - 0.5h) * facingDetail * facingDetail * _Detail * 0.16h;
                main.rgb = saturate(main.rgb * (1.0h + muscle));

                // Wetness darkens and deepens the albedo the way wet skin does.
                // The specular half of the effect is added after lighting, below.
                main.rgb *= lerp(1.0h, 0.78h, _Wet);

                SurfaceData2D surfaceData;
                InputData2D inputData;
                InitializeSurfaceData(main.rgb, main.a, mask, normalTS, surfaceData);
                InitializeInputData(input.uv, input.lightingUV, inputData);

            #if defined(DEBUG_DISPLAY)
                SETUP_DEBUG_TEXTURE_DATA_2D_NO_TS(inputData, input.positionWS, input.positionCS, _MainTex);
                surfaceData.normalWS = input.normalWS;
            #endif

                half4 lit = CombinedShapeLightShared(surfaceData, inputData);

                // The cylinder normal sweeps x from -1 at the left edge to +1 at the
                // right, so |x| IS the silhouette term and 1-|x| is the facing term.
                half edge = saturate(abs(normalTS.x));
                half facing = 1.0h - edge;

                // Headroom: how far this pixel is from clipping. Every additive
                // term below is scaled by it, so highlights get shaped instead of
                // blown out. Without this the rim alone drives already-lit limbs
                // past white and the body reads as chrome.
                half luma = dot(lit.rgb, half3(0.299h, 0.587h, 0.114h));
                half headroom = saturate(1.0h - luma);

                half rim = pow(edge, _RimPower) * _RimStrength;
                lit.rgb += _RimColor.rgb * rim * main.a * headroom;

                // Warm bounce through the belly of the tube. Deliberately subtle:
                // this is the term that stops shaded skin going grey.
                lit.rgb += _WarmColor.rgb * (_WrapWarm * facing * facing * 0.22h) * main.a * headroom;

                // Sweat: a tight wet band down the centreline, plus a few glints.
                // Sweat is allowed more of the remaining headroom than the rim —
                // a wet highlight SHOULD approach white, an edge light should not.
                half spec = pow(saturate(normalTS.z), 20.0h);
                half sparkle = step(0.94h, grit) * facing;
                lit.rgb += _SweatColor.rgb * (spec * 0.85h + sparkle * 0.3h) * _Sweat * main.a
                         * (0.35h + 0.65h * headroom);

                // Back light: a broad warm wrap on the silhouette whose job is
                // SEPARATION, not shaping. In a clinch the two fighters overlap at
                // similar luminance and merge into one unreadable shape; this is
                // what pulls the near body off the far one. Broader than the rim
                // (edge squared rather than edge^_RimPower) and given less
                // headroom suppression, because it has to survive on an already
                // brightly lit limb, which is exactly where the merge happens.
                lit.rgb += _BackLightColor.rgb * (edge * edge * _BackLight * 0.28h) * main.a
                         * (0.5h + 0.5h * headroom);

                // Wet specular: a tighter, brighter band than sweat's. Sweat is
                // beads; this is a sheet of water, so it is narrower and it does
                // not sparkle.
                half wetSpec = pow(saturate(normalTS.z), 34.0h) * _Wet;
                lit.rgb += _SweatColor.rgb * wetSpec * 0.6h * main.a * (0.3h + 0.7h * headroom);

                // Hit flash: a whole-part pulse biased toward the edge, so it reads
                // as the limb being struck rather than as its material changing.
                // Deliberately NOT headroom-scaled — a flash that politely refuses
                // to clip is not a flash. Driven per fighter by Systems_ImpactFx.
                lit.rgb += _FlashColor.rgb * (_Flash * (0.55h + 0.45h * edge)) * main.a;

                return lit;
            }
            ENDHLSL
        }

        Pass
        {
            Tags { "LightMode" = "NormalsRendering"}

            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/Core2D.hlsl"

            #pragma vertex NormalsRenderingVertex
            #pragma fragment NormalsRenderingFragment

            #pragma multi_compile_instancing
            #pragma multi_compile _ SKINNED_SPRITE

            struct Attributes
            {
                COMMON_2D_NORMALS_INPUTS
                float4 color        : COLOR;
                UNITY_SKINNED_VERTEX_INPUTS
            };

            struct Varyings
            {
                COMMON_2D_NORMALS_OUTPUTS
                half4   color           : COLOR;
            };

            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/Normals2DCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half4 _RimColor;
                half4 _WarmColor;
                half4 _SweatColor;
                half4 _DirtColor;
                half4 _FlashColor;
                half4 _BackLightColor;
                half _RimStrength;
                half _RimPower;
                half _WrapWarm;
                half _Sweat;
                half _Dirt;
                half _Wet;
                half _Flash;
                half _BackLight;
                half _Detail;
                half _DetailScale;
            CBUFFER_END

            Varyings NormalsRenderingVertex(Attributes input)
            {
                UNITY_SKINNED_VERTEX_COMPUTE(input);
                SetUpSpriteInstanceProperties();
                input.positionOS = UnityFlipSprite(input.positionOS, unity_SpriteProps.xy);

                Varyings o = CommonNormalsVertex(input);
                o.color = input.color * _Color * unity_SpriteColor;
                return o;
            }

            half4 NormalsRenderingFragment(Varyings input) : SV_Target
            {
                SetUpSpriteInstanceProperties();
                return CommonNormalsFragment(input, input.color);
            }
            ENDHLSL
        }

        Pass
        {
            Tags { "LightMode" = "UniversalForward" "Queue"="Transparent" "RenderType"="Transparent"}

            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/Core2D.hlsl"

            #pragma vertex UnlitVertex
            #pragma fragment UnlitFragment

            struct Attributes
            {
                COMMON_2D_INPUTS
                half4 color : COLOR;
                UNITY_SKINNED_VERTEX_INPUTS
            };

            struct Varyings
            {
                COMMON_2D_OUTPUTS
                half4 color : COLOR;
            };

            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/2DCommon.hlsl"

            #pragma multi_compile_instancing
            #pragma multi_compile _ DEBUG_DISPLAY SKINNED_SPRITE

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half4 _RimColor;
                half4 _WarmColor;
                half4 _SweatColor;
                half4 _DirtColor;
                half4 _FlashColor;
                half4 _BackLightColor;
                half _RimStrength;
                half _RimPower;
                half _WrapWarm;
                half _Sweat;
                half _Dirt;
                half _Wet;
                half _Flash;
                half _BackLight;
                half _Detail;
                half _DetailScale;
            CBUFFER_END

            Varyings UnlitVertex(Attributes input)
            {
                UNITY_SKINNED_VERTEX_COMPUTE(input);
                SetUpSpriteInstanceProperties();
                input.positionOS = UnityFlipSprite(input.positionOS, unity_SpriteProps.xy);

                Varyings o = CommonUnlitVertex(input);
                o.color = input.color * _Color * unity_SpriteColor;
                return o;
            }

            half4 UnlitFragment(Varyings input) : SV_Target
            {
                return CommonUnlitFragment(input, input.color);
            }
            ENDHLSL
        }
    }

    Fallback "Universal Render Pipeline/2D/Sprite-Lit-Default"
}
