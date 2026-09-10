using ComputeSharp;
using ComputeSharp.D2D1;

namespace PoChopAudio.WinUI.Shaders;

/// <summary>
/// A slow mesh gradient: four coloured lobes drifting on independent orbits, plus a faint grain.
///
/// <para>
/// This is HLSL. ComputeSharp's source generator compiles the <see cref="Execute"/> body to a
/// Direct2D pixel shader at build time and Win2D runs it per pixel on the GPU — which is what makes
/// a full-window animated gradient cost nothing measurable, where the same effect built from XAML
/// gradient stops and storyboards would repaint on the UI thread.
/// </para>
/// <para>
/// The grain matters more than it looks. A smooth wide gradient in 8-bit colour bands visibly on
/// almost every panel; a little per-pixel noise dithers the transition and the banding disappears.
/// </para>
/// </summary>
/// <param name="time">Seconds since the effect started. Drives the drift.</param>
/// <param name="size">Surface size in pixels, for normalising the coordinate.</param>
/// <param name="tintA">First lobe colour, linear RGB 0..1.</param>
/// <param name="tintB">Second lobe colour, linear RGB 0..1.</param>
/// <param name="baseColor">The colour everything is composited over.</param>
/// <param name="intensity">0 hides the effect entirely; 1 is full strength.</param>
/// <param name="audioPower">0 for silence, 1 for peak audio energy.</param>
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct AuroraShader(
    float time,
    float2 size,
    float3 tintA,
    float3 tintB,
    float3 baseColor,
    float intensity,
    float audioPower = 0f) : ID2D1PixelShader
{
    public float4 Execute()
    {
        float2 position = D2D.GetScenePosition().XY;
        float2 uv = position / Hlsl.Max(size, new float2(1f, 1f));

        // Keep the lobes circular on a wide surface rather than stretched into ellipses.
        float aspect = size.X / Hlsl.Max(size.Y, 1f);
        float2 p = new float2(uv.X * aspect, uv.Y);

        float3 colour = baseColor;
        float lobeBoost = 1.0f + (Hlsl.Saturate(audioPower) * 0.45f);

        colour += tintA * Lobe(p, new float2(0.25f * aspect, 0.30f), 0.55f * lobeBoost, time, 0.11f, 0.9f);
        colour += tintB * Lobe(p, new float2(0.80f * aspect, 0.22f), 0.48f * lobeBoost, time, -0.08f, 1.3f);
        colour += tintB * Lobe(p, new float2(0.60f * aspect, 0.85f), 0.60f * lobeBoost, time, 0.06f, 0.5f);
        colour += tintA * Lobe(p, new float2(0.10f * aspect, 0.90f), 0.45f * lobeBoost, time, -0.13f, 1.7f);

        // Hash-based dither. Cheap, stable per pixel, and enough to break up 8-bit banding.
        float grain = Hlsl.Frac(Hlsl.Sin(Hlsl.Dot(position, new float2(12.9898f, 78.233f))) * 43758.5453f);
        colour += (grain - 0.5f) * 0.012f;

        float effectiveIntensity = Hlsl.Saturate(intensity + (Hlsl.Saturate(audioPower) * 0.25f));
        colour = Hlsl.Lerp(baseColor, colour, effectiveIntensity);

        return new float4(Hlsl.Saturate(colour), 1f);
    }

    /// <summary>
    /// One soft radial lobe orbiting <paramref name="centre"/>. Falloff is a smoothstep rather than
    /// an inverse-square so the edges stay soft instead of pinching to a hard point.
    /// </summary>
    private static float Lobe(float2 p, float2 centre, float radius, float time, float speed, float phase)
    {
        float2 drift = new float2(
            Hlsl.Sin((time * speed) + phase) * 0.14f,
            Hlsl.Cos((time * speed * 0.8f) + phase) * 0.10f);

        float distance = Hlsl.Length(p - (centre + drift));

        return Hlsl.SmoothStep(radius, 0f, distance) * 0.55f;
    }
}
