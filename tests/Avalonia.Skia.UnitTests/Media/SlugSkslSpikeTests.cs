using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization.Slug;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// The SkSL spike for the Slug vector tier: the reference pixel shader ported to an
    /// <see cref="SKRuntimeEffect"/> reading the serialized half-float textures through raw
    /// nearest-sampled child shaders, with the per-draw uniforms standing in for fwidth and the
    /// vertex stage. The pass criterion is decision-level agreement with
    /// <see cref="SlugReferenceEvaluator"/> on identical texels — the evaluator is already
    /// proven against the analytic rasterizer, so the shader only has to match it. Findings and
    /// measured numbers live in planning/slug-sksl-spike.md.
    /// </summary>
    public class SlugSkslSpikeTests
    {
        /// <summary>
        /// The ported shader. Deliberately version-agnostic SkSL: the band-list loops use the
        /// constant bound of 64 (the serializer's decline threshold guarantees no list is
        /// longer) with an early break, and the root-code table is evaluated arithmetically
        /// (bit s of 0x74 / 0x2E via mod(floor(n * exp2(-s)), 2)) instead of integer bit ops.
        /// The compile test reports whether the base profile accepts it or #version 300 is
        /// required. One deliberate deviation from the HLSL: sign tests use (v &lt; 0) rather
        /// than the float sign bit, so negative zero classifies as positive — the serializer
        /// never emits negative zero, and the evaluator comparison would catch any divergence.
        /// </summary>
        private const string ShaderSource = @"
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

        private static readonly List<string> s_report = new();

        private readonly ITestOutputHelper _output;

        public SlugSkslSpikeTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// Passing-run output is not shown by the terminal reporter; set SLUG_SPIKE_REPORT to a
        /// file path to capture the measurements for the planning write-up. Every test flushes
        /// (and clears) the shared list, so the file is complete regardless of test order.
        /// </summary>
        private static void FlushReport()
        {
            if (Environment.GetEnvironmentVariable("SLUG_SPIKE_REPORT") is { Length: > 0 } reportPath)
            {
                lock (s_report)
                {
                    File.AppendAllLines(reportPath, s_report);
                    s_report.Clear();
                }
            }
        }

        private static SKRuntimeEffect Compile(out string variant)
        {
            var effect = SKRuntimeEffect.CreateShader(ShaderSource, out var baseErrors);

            if (effect is not null)
            {
                variant = "base profile (no #version pragma)";
                return effect;
            }

            effect = SKRuntimeEffect.CreateShader("#version 300\n" + ShaderSource, out var es3Errors);

            Assert.True(effect is not null,
                $"SkSL rejected both variants.\nbase: {baseErrors}\n#version 300: {es3Errors}");

            variant = "#version 300";
            return effect!;
        }

        private static SKImage CreateTexture(ReadOnlySpan<Half> texels, int rows)
        {
            var info = new SKImageInfo(SlugTexelSerializer.TextureWidth, Math.Max(rows, 1),
                SKColorType.RgbaF16, SKAlphaType.Unpremul);
            var bytes = MemoryMarshal.AsBytes(texels).ToArray();

            Array.Resize(ref bytes, info.BytesSize);

            return SKImage.FromPixelCopy(info, bytes, info.RowBytes);
        }

        private static SKShader CreateGlyphShader(
            SKRuntimeEffect effect, SKShader curves, SKShader bands,
            in SlugGlyphPlacement placement, float pixelsPerEmX, float pixelsPerEmY,
            SKMatrix emToDevice)
        {
            var uniforms = new SKRuntimeEffectUniforms(effect)
            {
                ["pixelsPerEm"] = new[] { pixelsPerEmX, pixelsPerEmY },
                ["glyphLoc"] = new[] { (float)placement.GlyphLocX, (float)placement.GlyphLocY },
                ["bandCounts"] = new[] { (float)placement.HorizontalBandCount, (float)placement.VerticalBandCount },
                ["bandTransform"] = new[]
                {
                    placement.BandScaleX, placement.BandScaleY, placement.BandOffsetX, placement.BandOffsetY,
                },
                ["evenOdd"] = placement.EvenOdd ? 1f : 0f,
                ["tint"] = new[] { 1f, 1f, 1f, 1f },
            };

            var children = new SKRuntimeEffectChildren(effect)
            {
                ["curveTex"] = curves,
                ["bandTex"] = bands,
            };

            return effect.ToShader(uniforms, children, emToDevice);
        }

        /// <summary>
        /// Renders the placed glyph through the runtime effect and compares every pixel against
        /// the reference evaluator at the same em-space sample — the shader must reproduce the
        /// evaluator's decisions, so the gates are quantization-tight, not perceptual.
        /// </summary>
        private void AssertShaderMatchesEvaluator(
            string label, SlugTexelSerializer serializer, SlugGlyphData data,
            in SlugGlyphPlacement placement, SKMatrix emToDevice, int width, int height)
        {
            var effect = Compile(out _);

            using var curveImage = CreateTexture(serializer.CurveTexels, serializer.CurveRowCount);
            using var bandImage = CreateTexture(serializer.BandTexels, serializer.BandRowCount);
            using var curveShader = curveImage.ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                new SKSamplingOptions(SKFilterMode.Nearest));
            using var bandShader = bandImage.ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                new SKSamplingOptions(SKFilterMode.Nearest));

            Assert.True(emToDevice.TryInvert(out var deviceToEm));

            // What the HLSL derives per-fragment with fwidth is constant under an affine
            // transform: the L1 norm of each em coordinate's device-space gradient.
            var emsPerPixelX = Math.Abs(deviceToEm.ScaleX) + Math.Abs(deviceToEm.SkewX);
            var emsPerPixelY = Math.Abs(deviceToEm.SkewY) + Math.Abs(deviceToEm.ScaleY);

            using var shader = CreateGlyphShader(effect, curveShader, bandShader, in placement,
                1f / emsPerPixelX, 1f / emsPerPixelY, emToDevice);
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var paint = new SKPaint { Shader = shader };

            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.DrawRect(SKRect.Create(width, height), paint);

            using var pixels = surface.PeekPixels();

            Assert.NotNull(pixels);

            var sum = 0.0;
            var worst = 0.0;

            for (var py = 0; py < height; py++)
            {
                for (var px = 0; px < width; px++)
                {
                    var em = deviceToEm.MapPoint(new SKPoint(px + 0.5f, py + 0.5f));

                    var expected = SlugReferenceEvaluator.Evaluate(
                        serializer.CurveTexels, serializer.BandTexels, in placement,
                        em.X, em.Y, emsPerPixelX, emsPerPixelY);

                    var actual = pixels.GetPixelColor(px, py).Alpha / 255.0;
                    var delta = Math.Abs(actual - expected);

                    sum += delta;
                    worst = Math.Max(worst, delta);
                }
            }

            var mean = sum / (width * height);

            s_report.Add(FormattableString.Invariant(
                $"{label,-24} {width,4}x{height,-4} mean {mean:0.00000}  worst {worst:0.00000}"));
            _output.WriteLine(s_report[^1]);

            FlushReport();

            // 8-bit readback quantization is 1/255 ~ 0.0039; everything past ~2 quantization
            // steps would mean the shader made a different decision than the evaluator.
            Assert.True(mean <= 0.002 && worst <= 3.0 / 255,
                FormattableString.Invariant($"{label}: mean {mean:0.00000}, worst {worst:0.00000}"));
        }

        private static (SlugGlyphData Data, SlugGlyphPlacement Placement, SlugTexelSerializer Serializer)
            Prepare(Action<SlugContourSink> draw)
        {
            var sink = new SlugContourSink();

            draw(sink);

            var data = SlugBandEncoder.Encode(sink);

            Assert.NotNull(data);

            var serializer = new SlugTexelSerializer();

            Assert.True(serializer.TryAdd(data!, out var placement));

            return (data!, placement, serializer);
        }

        private static SKMatrix WindowMatrix(SlugGlyphData data, float scale, float rotationDegrees,
            out int width, out int height)
        {
            // Em space is y-up; device space is y-down — the flip rides the matrix, winding is
            // direction-agnostic by construction.
            var matrix = SKMatrix.CreateScale(scale, -scale);

            if (rotationDegrees != 0)
            {
                matrix = SKMatrix.Concat(SKMatrix.CreateRotationDegrees(rotationDegrees), matrix);
            }

            Span<SKPoint> corners = stackalloc SKPoint[]
            {
                matrix.MapPoint(new SKPoint(data.MinX, data.MinY)),
                matrix.MapPoint(new SKPoint(data.MaxX, data.MinY)),
                matrix.MapPoint(new SKPoint(data.MinX, data.MaxY)),
                matrix.MapPoint(new SKPoint(data.MaxX, data.MaxY)),
            };

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;

            foreach (var corner in corners)
            {
                minX = Math.Min(minX, corner.X);
                minY = Math.Min(minY, corner.Y);
                maxX = Math.Max(maxX, corner.X);
                maxY = Math.Max(maxY, corner.Y);
            }

            const int margin = 3;

            width = (int)Math.Ceiling(maxX - minX) + margin * 2;
            height = (int)Math.Ceiling(maxY - minY) + margin * 2;

            return SKMatrix.Concat(SKMatrix.CreateTranslation(margin - minX, margin - minY), matrix);
        }

        private static void DrawBlob(SlugContourSink sink)
        {
            sink.BeginFigure(new Avalonia.Point(0, 0));
            sink.QuadraticBezierTo(new Avalonia.Point(2, 0.5), new Avalonia.Point(0.2, 1));
            sink.QuadraticBezierTo(new Avalonia.Point(-2, 1.5), new Avalonia.Point(0, 2));
            sink.LineTo(new Avalonia.Point(1, 2));
            sink.QuadraticBezierTo(new Avalonia.Point(3, 1), new Avalonia.Point(1.2, 0.2));
            sink.EndFigure(true);
        }

        private static (SlugGlyphData Data, SlugGlyphPlacement Placement, SlugTexelSerializer Serializer)
            PrepareInterGlyph(char character)
        {
            var bytes = LoadFontBytes("Inter-Regular.ttf");

            using var skData = SKData.CreateCopy(bytes);
            using var skTypeface = SKTypeface.FromData(skData);

            Assert.NotNull(skTypeface);

            var typeface = new GlyphTypeface(new SkiaTypeface(skTypeface!, FontSimulations.None));

            Assert.True(typeface.CharacterToGlyphMap.ContainsGlyph(character));

            var scale = 1.0 / typeface.Metrics.DesignEmHeight;
            var sink = new SlugContourSink();

            Assert.True(typeface.TryBuildGlyphContours(
                typeface.CharacterToGlyphMap[character], new Matrix(scale, 0, 0, scale, 0, 0), sink));

            var data = SlugBandEncoder.Encode(sink);

            Assert.NotNull(data);

            var serializer = new SlugTexelSerializer();

            Assert.True(serializer.TryAdd(data!, out var placement));

            return (data!, placement, serializer);
        }

        [Fact]
        public void The_Ported_Shader_Compiles()
        {
            var effect = Compile(out var variant);

            s_report.Add($"compiles under: {variant}");
            _output.WriteLine(s_report[^1]);
            FlushReport();
            Assert.Equal(2, effect.Children.Count);
        }

        [Fact]
        public void Blob_Coverage_Matches_The_Reference_Evaluator()
        {
            var (data, placement, serializer) = Prepare(DrawBlob);
            var matrix = WindowMatrix(data, 64, 0, out var width, out var height);

            AssertShaderMatchesEvaluator("blob 64px", serializer, data, in placement, matrix, width, height);
        }

        [Fact]
        public void Nonzero_Holes_Match_The_Reference_Evaluator()
        {
            var (data, placement, serializer) = Prepare(sink =>
            {
                sink.BeginFigure(new Avalonia.Point(0, 0));
                sink.LineTo(new Avalonia.Point(2, 0));
                sink.LineTo(new Avalonia.Point(2, 2));
                sink.LineTo(new Avalonia.Point(0, 2));
                sink.EndFigure(true);

                sink.BeginFigure(new Avalonia.Point(0.5, 0.5));
                sink.LineTo(new Avalonia.Point(0.5, 1.5));
                sink.LineTo(new Avalonia.Point(1.5, 1.5));
                sink.LineTo(new Avalonia.Point(1.5, 0.5));
                sink.EndFigure(true);
            });
            var matrix = WindowMatrix(data, 48, 0, out var width, out var height);

            AssertShaderMatchesEvaluator("hole 48px", serializer, data, in placement, matrix, width, height);
        }

        [Fact]
        public void An_Inter_Glyph_Matches_The_Reference_Evaluator()
        {
            var (data, placement, serializer) = PrepareInterGlyph('g');
            var matrix = WindowMatrix(data, 96, 0, out var width, out var height);

            AssertShaderMatchesEvaluator("Inter g 96px", serializer, data, in placement, matrix, width, height);
        }

        [Fact]
        public void A_Rotated_Draw_Matches_The_Reference_Evaluator()
        {
            // Rotation exercises the fwidth replacement: anisotropic L1-norm uniforms feed both
            // the shader and the evaluator, and the local matrix carries the full transform.
            var (data, placement, serializer) = PrepareInterGlyph('g');
            var matrix = WindowMatrix(data, 96, 30, out var width, out var height);

            AssertShaderMatchesEvaluator("Inter g 96px rot30", serializer, data, in placement, matrix, width, height);
        }

        [Fact]
        public void Per_Glyph_Draw_Cost_Is_Measured()
        {
            var (data, placement, serializer) = PrepareInterGlyph('g');
            var effect = Compile(out _);

            using var curveImage = CreateTexture(serializer.CurveTexels, serializer.CurveRowCount);
            using var bandImage = CreateTexture(serializer.BandTexels, serializer.BandRowCount);
            using var curveShader = curveImage.ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                new SKSamplingOptions(SKFilterMode.Nearest));
            using var bandShader = bandImage.ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                new SKSamplingOptions(SKFilterMode.Nearest));

            var matrix = WindowMatrix(data, 48, 0, out var width, out var height);

            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var paint = new SKPaint();

            const int iterations = 2000;

            // Cold-ish path: per-glyph uniform rebuild + shader creation + draw — the shape of a
            // real run where each glyph carries its own placement constants.
            var stopwatch = Stopwatch.StartNew();

            for (var i = 0; i < iterations; i++)
            {
                using var shader = CreateGlyphShader(effect, curveShader, bandShader, in placement,
                    48f, 48f, matrix);

                paint.Shader = shader;
                surface.Canvas.DrawRect(SKRect.Create(width, height), paint);
                paint.Shader = null;
            }

            surface.Canvas.Flush();
            stopwatch.Stop();

            var perGlyphBuildAndDraw = stopwatch.Elapsed.TotalMicroseconds / iterations;

            // Warm path: shader reused, draw only — the shape of a cached per-run artifact.
            using var cached = CreateGlyphShader(effect, curveShader, bandShader, in placement, 48f, 48f, matrix);

            paint.Shader = cached;
            stopwatch.Restart();

            for (var i = 0; i < iterations; i++)
            {
                surface.Canvas.DrawRect(SKRect.Create(width, height), paint);
            }

            surface.Canvas.Flush();
            stopwatch.Stop();

            var perGlyphDrawOnly = stopwatch.Elapsed.TotalMicroseconds / iterations;

            s_report.Add(FormattableString.Invariant(
                $"raster cost, {width}x{height}px glyph: build+draw {perGlyphBuildAndDraw:0.0} us, draw-only {perGlyphDrawOnly:0.0} us ({iterations} iterations)"));
            _output.WriteLine(s_report[^1]);
            FlushReport();
        }

        private static byte[] LoadFontBytes(string fileName)
        {
            // Walk up from the test output directory to the repo's tests folder — the same
            // convention the render tests use — so the .otf corpus needs no embedding.
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && directory.Name != "tests")
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);

            return File.ReadAllBytes(Path.Combine(directory!.FullName, "Avalonia.RenderTests", "Assets", fileName));
        }
    }
}
