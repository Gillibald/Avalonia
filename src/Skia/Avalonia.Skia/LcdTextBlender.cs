using System;
using System.Collections.Concurrent;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// The per-channel blend for subpixel text: source pixels carry stripe coverage (already in
    /// the destination's channel positions), the blender applies the analytic
    /// <see cref="MaskGamma"/> correction per channel and lerps each destination channel by its
    /// own corrected coverage — the operation ordinary SourceOver cannot express with a single
    /// alpha. Destination alpha advances by the strongest channel, the platform-typical
    /// approximation (exact on the opaque main target, which eligibility guarantees in
    /// practice).
    /// </summary>
    /// <remarks>
    /// The compiled effect is shared and never disposed; built blenders are cached per tint and
    /// also never disposed (text colors are few in practice — past the cap, draws build a
    /// transient blender instead of evicting).
    /// </remarks>
    internal static class LcdTextBlender
    {
        private const int CacheCap = 64;

        private const string Source = @"
uniform half4 tint;        // straight rgb + alpha, 0..1
uniform half  kContrast;
uniform half  lumSrc;
uniform half  lumDst;
uniform half  linSrc;
uniform half  linDst;
uniform half  nearEqual;
uniform half  invGamma;

half3 correct(half3 coverage) {
    half3 boosted = coverage + (half3(1.0) - coverage) * kContrast * coverage;
    if (nearEqual > 0.5) {
        return boosted;
    }
    half3 linOut = linSrc * boosted + (half3(1.0) - boosted) * linDst;
    half3 outv = pow(linOut, half3(invGamma));
    return clamp((outv - half3(lumDst)) / half3(lumSrc - lumDst), half3(0.0), half3(1.0));
}

half4 main(half4 src, half4 dst) {
    half3 cov = correct(src.rgb);
    half3 rgb = tint.rgb * tint.a * cov + dst.rgb * (half3(1.0) - tint.a * cov);
    half maxCov = max(cov.r, max(cov.g, cov.b));
    half a = tint.a * maxCov + dst.a * (1.0 - tint.a * maxCov);
    return half4(rgb, a);
}";

        private static readonly SKRuntimeEffect? s_effect = Compile();
        private static readonly ConcurrentDictionary<uint, SKBlender> s_blenders = new();

        /// <summary>Whether the blend effect compiled; eligibility gates GPU LCD on this.</summary>
        public static bool IsSupported => s_effect is not null;

        /// <summary>
        /// The blender for a straight ARGB tint (opacity folded into its alpha), cached per
        /// tint. Null only when the effect failed to compile.
        /// </summary>
        public static SKBlender? Get(uint tintArgb)
        {
            if (s_effect is null)
            {
                return null;
            }

            if (s_blenders.TryGetValue(tintArgb, out var cached))
            {
                return cached;
            }

            var blender = Build(tintArgb);

            if (blender is not null && s_blenders.Count < CacheCap)
            {
                blender = s_blenders.GetOrAdd(tintArgb, blender);
            }

            return blender;
        }

        private static SKBlender? Build(uint tintArgb)
        {
            var r = (byte)(tintArgb >> 16);
            var g = (byte)(tintArgb >> 8);
            var b = (byte)tintArgb;
            var parameters = MaskGamma.GetLcdShaderParameters(r, g, b);

            var builder = new SKRuntimeBlenderBuilder(s_effect!);

            builder.Uniforms["tint"] = new[] { r / 255f, g / 255f, b / 255f, (byte)(tintArgb >> 24) / 255f };
            builder.Uniforms["kContrast"] = parameters.Contrast;
            builder.Uniforms["lumSrc"] = parameters.LumSrc;
            builder.Uniforms["lumDst"] = parameters.LumDst;
            builder.Uniforms["linSrc"] = parameters.LinSrc;
            builder.Uniforms["linDst"] = parameters.LinDst;
            builder.Uniforms["nearEqual"] = parameters.NearEqual ? 1f : 0f;
            builder.Uniforms["invGamma"] = parameters.InverseGamma;

            return builder.Build();
        }

        private static SKRuntimeEffect? Compile()
        {
            var effect = SKRuntimeEffect.CreateBlender(Source, out var errors);

            if (effect is null)
            {
                Logging.Logger.TryGet(Logging.LogEventLevel.Warning, Logging.LogArea.Visual)?.Log(
                    typeof(LcdTextBlender), "LCD text blender failed to compile: {Errors}", errors);
            }

            return effect;
        }
    }
}
