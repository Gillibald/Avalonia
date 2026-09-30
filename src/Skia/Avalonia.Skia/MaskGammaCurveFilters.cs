using Avalonia.Logging;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// Per-luminance-bucket runtime colour filters that evaluate the <see cref="MaskGamma"/>
    /// coverage correction in closed form, for inputs that carry coverage as premultiplied white
    /// (the Slug shaders' output). The curve is the one <see cref="MaskGamma.GetTable(int)"/>
    /// tabulates, keyed by the same bucket, so both agree to within 8-bit rounding.
    /// </summary>
    /// <remarks>
    /// The Slug shader runs its band loops before the colour filter, and a table filter lowers to
    /// implicit-LOD texture reads; evaluating the curve arithmetically keeps the filter free of
    /// texture reads, so it cannot inherit undefined derivatives from the shader's control flow.
    /// When the effect fails to compile, <see cref="Get"/> hands out the
    /// <see cref="MaskGammaFilters"/> table filter instead. The effect, its builder and the
    /// filters are shared and never disposed: paints and composed filters take their own refs,
    /// and the builder owns the effect it wraps. Touched on the render thread only.
    /// </remarks>
    internal static class MaskGammaCurveFilters
    {
        // Evaluated in float, not half: the solve divides by (lumSrc - lumDst), which is 37/255
        // for the two middle buckets, so half-precision pow error would exceed one 8-bit level.
        private const string Source = @"
uniform float kContrast;
uniform float lumSrc;
uniform float lumDst;
uniform float linSrc;
uniform float linDst;
uniform float nearEqual;
uniform float invGamma;

half4 main(half4 color) {
    float coverage = float(color.a);
    float boosted = coverage + (1.0 - coverage) * kContrast * coverage;
    float corrected = boosted;
    if (nearEqual < 0.5) {
        float linOut = linSrc * boosted + (1.0 - boosted) * linDst;
        corrected = (pow(linOut, invGamma) - lumDst) / (lumSrc - lumDst);
    }
    return half4(half(clamp(corrected, 0.0, 1.0)));
}";

        private static readonly SKColorFilter?[] s_filters = new SKColorFilter?[MaskGamma.BucketCount];
        private static SKRuntimeColorFilterBuilder? s_builder;
        private static bool s_effectFailed;

        /// <summary>
        /// The closed-form filter for a straight RGB text colour, or the table filter for the
        /// same bucket when the runtime effect is unavailable. Expects the input's colour
        /// channels to equal its alpha, and outputs the corrected coverage in all four channels.
        /// </summary>
        public static SKColorFilter Get(byte r, byte g, byte b)
        {
            var bucket = MaskGamma.GetBucket(r, g, b);

            if (s_filters[bucket] is { } cached)
            {
                return cached;
            }

            if (GetBuilder() is not { } builder)
            {
                return MaskGammaFilters.Get(r, g, b);
            }

            var parameters = MaskGamma.GetShaderParameters(r, g, b);

            builder.Uniforms["kContrast"] = parameters.Contrast;
            builder.Uniforms["lumSrc"] = parameters.LumSrc;
            builder.Uniforms["lumDst"] = parameters.LumDst;
            builder.Uniforms["linSrc"] = parameters.LinSrc;
            builder.Uniforms["linDst"] = parameters.LinDst;
            builder.Uniforms["nearEqual"] = parameters.NearEqual ? 1f : 0f;
            builder.Uniforms["invGamma"] = parameters.InverseGamma;

            return s_filters[bucket] = builder.Build();
        }

        /// <summary>Whether the closed-form effect compiled; false means <see cref="Get"/> returns table filters.</summary>
        public static bool IsSupported => GetBuilder() is not null;

        private static SKRuntimeColorFilterBuilder? GetBuilder()
        {
            if (s_builder is null && !s_effectFailed)
            {
                var effect = SKRuntimeEffect.CreateColorFilter(Source, out var errors);

                if (effect is null)
                {
                    s_effectFailed = true;
                    Logger.TryGet(LogEventLevel.Warning, LogArea.Visual)?.Log(null,
                        "Mask gamma colour filter failed to compile; Slug text keeps the table filter: {Errors}",
                        errors);
                }
                else
                {
                    s_builder = new SKRuntimeColorFilterBuilder(effect);
                }
            }

            return s_builder;
        }
    }
}
