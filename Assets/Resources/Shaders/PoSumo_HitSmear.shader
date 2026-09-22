// Hit smear: an expanding radial STREAK burst drawn at a contact point for a
// fraction of a second — the fight-game "that landed" flash that particles
// alone do not give, because particles are many small dots and a smear is one
// directed shape.
//
// Procedural like PoSumo/Shockwave: no texture, no atlas slot, and every
// instance differs only through its MaterialPropertyBlock, so the pooled quads
// stay batchable. Additive, so it can only ever brighten the frame — a smear
// that darkened would read as a hole.
//
// _Progress 0..1 drives both the expansion and the fade; _Strength scales the
// streak length and brightness together; the quad's own transform rotation
// aims the streak axis along the impact velocity (done in Systems_HitSmear, so
// the shader stays rotation-free).

Shader "PoSumo/HitSmear"
{
    Properties
    {
        _Color("Tint", Color) = (1, 0.92, 0.78, 1)
        _Progress("Progress", Range(0, 1)) = 0
        _Strength("Strength", Range(0, 2)) = 1
        _StreakCount("Streak Count", Float) = 7
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Blend SrcAlpha One
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                half4  color      : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                half4  color       : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _Progress;
                half _Strength;
                half _StreakCount;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                o.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                o.uv = input.uv;
                o.color = input.color;
                return o;
            }

            // Small deterministic hash so the streaks differ in length without a
            // texture or a random buffer.
            half Hash11(half n)
            {
                return frac(sin(n * 127.1h) * 43758.5453h);
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half2 p = input.uv - 0.5h;
                half r = length(p) * 2.0h;                      // 0 centre -> 1 edge
                half ang = atan2(p.y, p.x) / 6.28318h + 0.5h;   // 0..1 around

                half count = max(_StreakCount, 1.0h);
                half slot = floor(ang * count);
                half f = frac(ang * count);                     // 0..1 within a slot

                // A triangular spike per slot, sharpened per streak by its own
                // hash, so seven rays of differing weight radiate from the point.
                half spike = pow(saturate(1.0h - abs(f * 2.0h - 1.0h)), 2.0h + Hash11(slot) * 3.0h);

                // The band sweeps outward as _Progress advances and fades behind.
                half head = 0.18h + _Progress * 0.9h;
                half band = smoothstep(head - 0.45h, head, r) * smoothstep(head + 0.18h, head - 0.1h, r);

                // Whole-burst fade: bright for the first frames, gone by the end.
                half fade = saturate(1.0h - _Progress);
                fade *= fade;

                half a = spike * band * fade * saturate(_Strength) * input.color.a;
                // A hot white core in the first 40 ms sells "impact", not "light".
                half3 rgb = _Color.rgb + half3(1, 1, 1) * smoothstep(0.25h, 0.0h, r) * fade * 0.6h;
                return half4(rgb * a, a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
