// Expanding shock ring for the heaviest moments — a head KO, a dismemberment,
// a genuine slam.
//
// Deliberately an ADDITIVE ANNULUS rather than the usual screen-space refraction
// shockwave. Refraction needs the opaque camera texture, and the URP 2D Renderer
// does not reliably provide one — a distortion ring here would either sample
// garbage or silently render nothing, which is the worst of the two outcomes
// because it looks like a missing feature rather than an error.
//
// The ring is generated from the quad's own UVs, so it needs no texture, no
// atlas entry and no import settings. One material, one quad, one draw call per
// live ring, and `Systems_ShockwaveFx` pools a small fixed number of them.
//
// `_Progress` runs 0 -> 1 over the ring's life and is the ONLY thing animated.
// Radius, thickness and alpha are all derived from it, so a ring is one float
// upload per frame rather than a rebuilt mesh.
Shader "PoSumo/Shockwave"
{
    Properties
    {
        _Color("Colour", Color) = (1, 0.86, 0.66, 1)
        _Progress("Progress", Range(0, 1)) = 0
        _Thickness("Thickness", Range(0.005, 0.5)) = 0.09
        _Strength("Strength", Range(0, 4)) = 1
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        // Additive premultiplied: the ring only ever brightens what is behind it,
        // so it can never darken the fighters it is drawn over.
        Blend SrcAlpha One
        Cull Off
        ZWrite Off
        Lighting Off

        Pass
        {
            Tags { "LightMode" = "Universal2D" }

            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            #pragma vertex ShockVertex
            #pragma fragment ShockFragment
            #pragma multi_compile_instancing

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _Progress;
                half _Thickness;
                half _Strength;
            CBUFFER_END

            Varyings ShockVertex(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                o.positionCS = TransformObjectToHClip(input.positionOS);
                o.uv = input.uv;
                return o;
            }

            half4 ShockFragment(Varyings input) : SV_Target
            {
                // Distance from the quad's centre, 0 at the middle and 1 at the
                // edge midpoints. The quad is square, so the ring is round.
                half2 centred = input.uv * 2.0h - 1.0h;
                half dist = length(centred);

                // The ring races outward and thins as it goes, the way a real
                // pressure front does. Easing the radius on a square root front-
                // loads the expansion: fast at the instant of the hit, coasting
                // afterwards, which is what sells it as a release of energy
                // rather than a circle being animated.
                half radius = sqrt(saturate(_Progress));
                half thickness = _Thickness * (1.0h - 0.65h * _Progress);

                // A soft band at `radius`, falling off over `thickness`.
                half band = 1.0h - saturate(abs(dist - radius) / max(0.001h, thickness));
                band = band * band;   // tighten the core, keep the shoulders soft

                // Fade out over the life, and kill anything past the quad's
                // inscribed circle so the ring never shows the quad's corners.
                half life = 1.0h - _Progress;
                half clip = 1.0h - smoothstep(0.98h, 1.0h, dist);

                half alpha = band * life * life * clip * _Strength;
                return half4(_Color.rgb * alpha, alpha * _Color.a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
