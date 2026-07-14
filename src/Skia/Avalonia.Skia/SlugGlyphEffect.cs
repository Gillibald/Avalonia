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
    /// loops use the serializer's 64-curve cap as a constant bound with an early break, the
    /// root-code table evaluates arithmetically, and fwidth becomes per-draw uniforms (constant
    /// under an affine transform). The result compiles under the base runtime-effect profile —
    /// no #version pragma — so every Skia backend accepts it.
    /// </summary>
    internal static class SlugGlyphEffect
    {
        internal const string ShaderSource = @"
uniform shader curveTex;
uniform shader bandTex;
uniform float2 pixelsPerEm;
uniform float2 glyphLoc;
uniform float2 bandCounts;
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

    float xcov = 0.0;
    float xwgt = 0.0;
    half4 hband = bandTex.eval(float2(glyphLoc.x + bandY + 0.5, glyphLoc.y + 0.5));
    float2 hloc = CalcBandLoc(float(hband.y));
    float hcount = float(hband.x);

    for (int i = 0; i < 64; ++i) {
        if (float(i) >= hcount) { break; }
        half4 entry = bandTex.eval(float2(hloc.x + float(i) + 0.5, hloc.y + 0.5));
        half4 c12 = curveTex.eval(float2(float(entry.x) + 0.5, float(entry.y) + 0.5));
        half4 c3 = curveTex.eval(float2(float(entry.x) + 1.5, float(entry.y) + 0.5));
        float2 p1 = float2(c12.xy) - coord;
        float2 p2 = float2(c12.zw) - coord;
        float2 p3 = float2(c3.xy) - coord;

        if (max(max(p1.x, p2.x), p3.x) * pixelsPerEm.x < -0.5) { break; }

        float2 code = RootCode(p1.y, p2.y, p3.y);

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
            xcov += code.x * clamp(r1 + 0.5, 0.0, 1.0);
            xcov -= code.y * clamp(r2 + 0.5, 0.0, 1.0);
            xwgt = max(xwgt, code.x * clamp(1.0 - abs(r1) * 2.0, 0.0, 1.0));
            xwgt = max(xwgt, code.y * clamp(1.0 - abs(r2) * 2.0, 0.0, 1.0));
        }
    }

    float ycov = 0.0;
    float ywgt = 0.0;
    half4 vband = bandTex.eval(float2(glyphLoc.x + bandCounts.x + bandX + 0.5, glyphLoc.y + 0.5));
    float2 vloc = CalcBandLoc(float(vband.y));
    float vcount = float(vband.x);

    for (int i = 0; i < 64; ++i) {
        if (float(i) >= vcount) { break; }
        half4 entry = bandTex.eval(float2(vloc.x + float(i) + 0.5, vloc.y + 0.5));
        half4 c12 = curveTex.eval(float2(float(entry.x) + 0.5, float(entry.y) + 0.5));
        half4 c3 = curveTex.eval(float2(float(entry.x) + 1.5, float(entry.y) + 0.5));
        float2 p1 = float2(c12.xy) - coord;
        float2 p2 = float2(c12.zw) - coord;
        float2 p3 = float2(c3.xy) - coord;

        if (max(max(p1.y, p2.y), p3.y) * pixelsPerEm.y < -0.5) { break; }

        float2 code = RootCode(p1.x, p2.x, p3.x);

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
            ycov -= code.x * clamp(r1 + 0.5, 0.0, 1.0);
            ycov += code.y * clamp(r2 + 0.5, 0.0, 1.0);
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
