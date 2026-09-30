using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Logging;
using Avalonia.Media.Fonts.Rasterization.Slug;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// The Slug vector-glyph runtime effect and the per-store texture mirrors it samples. The
    /// shader is a port of the reference pixel shader for the Slug font rendering algorithm by
    /// Eric Lengyel (github.com/EricLengyel/Slug, licensed MIT OR Apache-2.0, patent dedicated
    /// to the public domain — credit is required on distribution and given here), adapted to
    /// SkSL runtime-effect constraints: both textures are RGBA half-float at width 2048 read
    /// through raw nearest-sampled children (no integer samplers or texelFetch), the band-list
    /// loops use the serializer's 64-curve cap as a constant bound and exit only at a per-draw
    /// uniform loop bound, the root-code table evaluates arithmetically, and fwidth becomes
    /// per-draw uniforms (constant under an affine transform). The result compiles under the
    /// base runtime-effect profile — no #version pragma — so every Skia backend accepts it.
    /// <para>
    /// One addition to the reference: at 32 or more pixels per em along a ray axis
    /// (<see cref="SlugGlyphPlacement.IsSplitEnabled"/>), each pixel walks only the half of its
    /// band list on its side of the glyph's midpoint, with a forward ray at or past the
    /// midpoint and a backward ray before it (<see cref="SlugBandEncoder"/> lays the lists
    /// out for this). The loop bound is then the glyph's longest such run instead of its
    /// longest whole list.
    /// </para>
    /// </summary>
    internal static class SlugGlyphEffect
    {
        internal const string ShaderSource = @"
uniform shader curveTex;
uniform shader bandTex;
uniform float2 pixelsPerEm;
uniform float2 glyphLoc;
uniform float2 bandCounts;
uniform float2 loopBounds;
uniform float2 splitPoints;
uniform float2 splitEnabled;
uniform float4 bandTransform;
uniform float evenOdd;
uniform half4 tint;

float2 CalcBandLoc(float offset) {
    float x = glyphLoc.x + offset;
    float row = floor(x / 2048.0);
    return float2(x - row * 2048.0, glyphLoc.y + row);
}

float2 RootCode(float p1, float p2, float p3) {
    float shift = (p1 < 0.0 ? 1.0 : 0.0) + (p2 < 0.0 ? 2.0 : 0.0) + (p3 < 0.0 ? 4.0 : 0.0);
    float scale = exp2(-shift);
    return float2(mod(floor(116.0 * scale), 2.0), mod(floor(46.0 * scale), 2.0));
}

half4 main(float2 coord) {
    float bandY = clamp(floor(coord.y * bandTransform.y + bandTransform.w), 0.0, bandCounts.x - 1.0);
    float bandX = clamp(floor(coord.x * bandTransform.x + bandTransform.z), 0.0, bandCounts.y - 1.0);

    // A band header holds (forward-only count, list offset, shared count, backward-only
    // count). Split, a pixel at or past the split point walks the forward-only and shared
    // segments with a +axis ray, any other pixel the shared and backward-only segments with a
    // -axis ray; unsplit, every pixel walks the whole list with a +axis ray.
    //
    // Every texture read stays in uniform control flow: the loops exit only on the per-draw
    // loop bound, and entries past this pixel's run or curves more than half a pixel behind
    // the pixel along its ray are masked out instead of ending the loop. Only the run's start
    // and the ray direction vary per pixel. Skia m119's runtime effects offer no
    // derivative-free texture read: every child eval lowers to an implicit-LOD texture(), so a
    // per-pixel exit would leave the reads inside the loops in non-uniform control flow, where
    // their results are undefined.
    //
    // The backward ray gives the same winding: around closed contours the crossings of a whole
    // line sum to zero, so the crossings ahead of the pixel are minus those behind it, and
    // clamp(r + 0.5) + clamp(0.5 - r) = 1 carries that over to the filtered sums. Hence each
    // contribution becomes dir * clamp(dir * r + 0.5) with the weights unchanged.
    float xcov = 0.0;
    float xwgt = 0.0;
    half4 hband = bandTex.eval(float2(glyphLoc.x + bandY + 0.5, glyphLoc.y + 0.5));
    float hback = splitEnabled.x > 0.5 && coord.x < splitPoints.x ? 1.0 : 0.0;
    float hdir = 1.0 - 2.0 * hback;
    float hcount = splitEnabled.x > 0.5
        ? float(hband.z) + (hback > 0.5 ? float(hband.w) : float(hband.x))
        : float(hband.x) + float(hband.z) + float(hband.w);
    float2 hloc = CalcBandLoc(float(hband.y) + hback * float(hband.x));

    for (int i = 0; i < 64; ++i) {
        if (float(i) >= loopBounds.x) { break; }
        half4 entry = bandTex.eval(float2(hloc.x + float(i) + 0.5, hloc.y + 0.5));
        half4 c12 = curveTex.eval(float2(float(entry.x) + 0.5, float(entry.y) + 0.5));
        half4 c3 = curveTex.eval(float2(float(entry.x) + 1.5, float(entry.y) + 0.5));
        float2 p1 = float2(c12.xy) - coord;
        float2 p2 = float2(c12.zw) - coord;
        float2 p3 = float2(c3.xy) - coord;

        float reach = max(max(hdir * p1.x, hdir * p2.x), hdir * p3.x) * pixelsPerEm.x;
        float live = float(i) < hcount && reach >= -0.5 ? 1.0 : 0.0;
        float2 code = RootCode(p1.y, p2.y, p3.y) * live;

        if (code.x + code.y > 0.0) {
            float a = p1.y - 2.0 * p2.y + p3.y;
            float b = p1.y - p2.y;
            float aPar = p1.x - 2.0 * p2.x + p3.x;
            float bPar = p1.x - p2.x;
            float ra = 1.0 / a;
            float d = sqrt(max(b * b - a * p1.y, 0.0));
            float t1 = (b - d) * ra;
            float t2 = (b + d) * ra;
            if (abs(a) < 1.52587890625e-5) { t1 = p1.y * (0.5 / b); t2 = t1; }
            float r1 = ((aPar * t1 - bPar * 2.0) * t1 + p1.x) * pixelsPerEm.x;
            float r2 = ((aPar * t2 - bPar * 2.0) * t2 + p1.x) * pixelsPerEm.x;
            xcov += hdir * code.x * clamp(hdir * r1 + 0.5, 0.0, 1.0);
            xcov -= hdir * code.y * clamp(hdir * r2 + 0.5, 0.0, 1.0);
            xwgt = max(xwgt, code.x * clamp(1.0 - abs(r1) * 2.0, 0.0, 1.0));
            xwgt = max(xwgt, code.y * clamp(1.0 - abs(r2) * 2.0, 0.0, 1.0));
        }
    }

    float ycov = 0.0;
    float ywgt = 0.0;
    half4 vband = bandTex.eval(float2(glyphLoc.x + bandCounts.x + bandX + 0.5, glyphLoc.y + 0.5));
    float vback = splitEnabled.y > 0.5 && coord.y < splitPoints.y ? 1.0 : 0.0;
    float vdir = 1.0 - 2.0 * vback;
    float vcount = splitEnabled.y > 0.5
        ? float(vband.z) + (vback > 0.5 ? float(vband.w) : float(vband.x))
        : float(vband.x) + float(vband.z) + float(vband.w);
    float2 vloc = CalcBandLoc(float(vband.y) + vback * float(vband.x));

    for (int i = 0; i < 64; ++i) {
        if (float(i) >= loopBounds.y) { break; }
        half4 entry = bandTex.eval(float2(vloc.x + float(i) + 0.5, vloc.y + 0.5));
        half4 c12 = curveTex.eval(float2(float(entry.x) + 0.5, float(entry.y) + 0.5));
        half4 c3 = curveTex.eval(float2(float(entry.x) + 1.5, float(entry.y) + 0.5));
        float2 p1 = float2(c12.xy) - coord;
        float2 p2 = float2(c12.zw) - coord;
        float2 p3 = float2(c3.xy) - coord;

        float reach = max(max(vdir * p1.y, vdir * p2.y), vdir * p3.y) * pixelsPerEm.y;
        float live = float(i) < vcount && reach >= -0.5 ? 1.0 : 0.0;
        float2 code = RootCode(p1.x, p2.x, p3.x) * live;

        if (code.x + code.y > 0.0) {
            float a = p1.x - 2.0 * p2.x + p3.x;
            float b = p1.x - p2.x;
            float aPar = p1.y - 2.0 * p2.y + p3.y;
            float bPar = p1.y - p2.y;
            float ra = 1.0 / a;
            float d = sqrt(max(b * b - a * p1.x, 0.0));
            float t1 = (b - d) * ra;
            float t2 = (b + d) * ra;
            if (abs(a) < 1.52587890625e-5) { t1 = p1.x * (0.5 / b); t2 = t1; }
            float r1 = ((aPar * t1 - bPar * 2.0) * t1 + p1.y) * pixelsPerEm.y;
            float r2 = ((aPar * t2 - bPar * 2.0) * t2 + p1.y) * pixelsPerEm.y;
            ycov -= vdir * code.x * clamp(vdir * r1 + 0.5, 0.0, 1.0);
            ycov += vdir * code.y * clamp(vdir * r2 + 0.5, 0.0, 1.0);
            ywgt = max(ywgt, code.x * clamp(1.0 - abs(r1) * 2.0, 0.0, 1.0));
            ywgt = max(ywgt, code.y * clamp(1.0 - abs(r2) * 2.0, 0.0, 1.0));
        }
    }

    float coverage = max(abs(xcov * xwgt + ycov * ywgt) / max(xwgt + ywgt, 1.52587890625e-5),
        min(abs(xcov), abs(ycov)));

    if (evenOdd > 0.5) {
        float w = coverage * 0.5;
        coverage = 1.0 - abs(1.0 - fract(w) * 2.0);
    } else {
        coverage = clamp(coverage, 0.0, 1.0);
    }

    return tint * half(coverage);
}
";

        private static SKRuntimeEffect? s_effect;
        private static SKRuntimeShaderBuilder? s_builder;
        private static bool s_effectFailed;

        /// <summary>
        /// The compiled effect, or null when the runtime rejected the source (logged once, never
        /// retried — the caller's support gate then keeps every draw on the native fallback).
        /// </summary>
        public static SKRuntimeEffect? Effect
        {
            get
            {
                if (s_effect is null && !s_effectFailed)
                {
                    s_effect = SKRuntimeEffect.CreateShader(ShaderSource, out var errors);

                    if (s_effect is null)
                    {
                        s_effectFailed = true;
                        Logger.TryGet(LogEventLevel.Warning, LogArea.Visual)?.Log(null,
                            "Slug glyph effect failed to compile; vector-tier text keeps the native fallback: {Errors}",
                            errors);
                    }
                }

                return s_effect;
            }
        }

        /// <summary>
        /// The shared runtime-shader builder over <see cref="Effect"/>. Never disposed: the
        /// builder owns the effect it wraps, so tearing it down would free the process-wide
        /// effect under later callers. Rebuilds overwrite every uniform and child before each
        /// Build call, and it is only touched on the render thread like everything else here.
        /// </summary>
        public static SKRuntimeShaderBuilder? Builder
            => Effect is { } effect ? s_builder ??= new SKRuntimeShaderBuilder(effect) : null;

        private sealed class TextureSet
        {
            public int Version = -1;
            public SKImage? CurveImage;
            public SKImage? BandImage;
            public SKShader? CurveShader;
            public SKShader? BandShader;
        }

        // Keyed by store identity: CPU-backed immutable images, so Skia's per-context texture
        // cache uploads each version once wherever it is drawn; the table lets the mirrors die
        // with their typeface instance. Accessed on the render thread only, like the store.
        private static readonly ConditionalWeakTable<SlugTexelStore, TextureSet> s_textures = new();

        /// <summary>
        /// Returns the raw nearest-sampled child shaders mirroring <paramref name="store"/>'s
        /// current texels, rebuilding when the store's version moved. Append-only texels mean a
        /// rebuild is an extension of the previous content, never a contradiction of it.
        /// </summary>
        public static bool TryGetShaders(SlugTexelStore store, out SKShader? curveShader, out SKShader? bandShader)
        {
            var set = s_textures.GetOrCreateValue(store);

            if (set.Version != store.Version)
            {
                set.CurveShader?.Dispose();
                set.BandShader?.Dispose();
                set.CurveImage?.Dispose();
                set.BandImage?.Dispose();
                set.CurveShader = null;
                set.BandShader = null;

                set.CurveImage = CreateTexture(store.CurveTexels, store.CurveRowCount);
                set.BandImage = CreateTexture(store.BandTexels, store.BandRowCount);

                if (set.CurveImage is not null && set.BandImage is not null)
                {
                    var sampling = new SKSamplingOptions(SKFilterMode.Nearest);

                    set.CurveShader = set.CurveImage.ToRawShader(
                        SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling);
                    set.BandShader = set.BandImage.ToRawShader(
                        SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling);
                }

                set.Version = store.Version;
            }

            curveShader = set.CurveShader;
            bandShader = set.BandShader;

            return curveShader is not null && bandShader is not null;
        }

        private static SKImage? CreateTexture(ReadOnlySpan<Half> texels, int rows)
        {
            if (rows == 0)
            {
                return null;
            }

            var info = new SKImageInfo(SlugTexelSerializer.TextureWidth, rows,
                SKColorType.RgbaF16, SKAlphaType.Unpremul);

            return SKImage.FromPixelCopy(info, MemoryMarshal.AsBytes(texels).ToArray(), info.RowBytes);
        }
    }
}
