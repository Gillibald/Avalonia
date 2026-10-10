using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.TextFormatting;
using Avalonia.UnitTests;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Strong hinting under natural layout: glyph positions are the shaper's in every mode, so
    /// a glyph whose ink the hinter moved or resized horizontally hands the difference to the
    /// gaps beside it. Grayscale and subpixel Strong keep the unhinted x and quarter-pixel
    /// phases, which leaves the ink gaps as even as Light's; bi-level Strong keeps the font's
    /// x fitting on whole-pixel pens and centres the fitted ink on the glyph's shaper slot.
    /// Every measurement reads what the renderer drew: each glyph is rendered alone at its run
    /// position, with the other glyphs of the run swapped for spaces.
    /// </summary>
    public class StrongHintingSpacingTests
    {
        private const string Sample = "Hamburgefonstiv0123";
        private const double OriginX = 8.3;
        private const double BaselineY = 30;
        private const int SurfaceWidth = 280;
        private const int SurfaceHeight = 40;

        /// <summary>The instructed system fonts the gasp table escalates or the hinting
        /// complaints name, plus Inter, whose embedded build has no programs and goes through
        /// the auto-hinter.</summary>
        public static TheoryData<string> Fonts => new()
        {
            "segoeui.ttf", "georgia.ttf", "tahoma.ttf", "verdana.ttf", "Inter-Regular.ttf",
        };

        [Theory]
        [MemberData(nameof(Fonts))]
        public void Strong_Ink_Gaps_Stay_As_Even_As_Light(string fontFile)
        {
            using var app = StartApp();
            var typeface = LoadFont(fontFile);

            var light = MeasureGapErrors(typeface, TextHintingMode.Light);
            var strong = MeasureGapErrors(typeface, TextHintingMode.Strong);

            var lightMean = light.Average(Math.Abs);
            var strongMean = strong.Average(Math.Abs);
            var lightWorst = light.Max(Math.Abs);
            var strongWorst = strong.Max(Math.Abs);

            Assert.True(strongMean <= lightMean + 0.1,
                FormattableString.Invariant(
                    $"{fontFile}: mean gap error Strong {strongMean:0.000} px vs Light {lightMean:0.000} px"));
            Assert.True(strongWorst <= lightWorst + 0.25,
                FormattableString.Invariant(
                    $"{fontFile}: worst gap error Strong {strongWorst:0.000} px vs Light {lightWorst:0.000} px"));
        }

        [Fact]
        public void Strong_Places_Glyphs_At_Quarter_Pixel_Phases()
        {
            using var app = StartApp();
            var typeface = LoadFont("Inter-Regular.ttf");

            // Two origins inside one pixel land in different quarter phases.
            var first = RenderRun(typeface, "HHH", 24, 8.26, TextHintingMode.Strong, TextRenderingMode.Antialias);
            var second = RenderRun(typeface, "HHH", 24, 8.49, TextHintingMode.Strong, TextRenderingMode.Antialias);

            Assert.False(first.AsSpan().SequenceEqual(second),
                "Strong grayscale output must follow the subpixel origin");
        }

        [Fact]
        public void Strong_On_The_Auto_Hinter_Renders_Like_Light()
        {
            using var app = StartApp();
            var typeface = LoadFont("Inter-Regular.ttf");

            // The auto-hinter fits Strong and Light text alike in y and leaves x natural, so
            // without a font program to differ in, the two modes draw the same pixels.
            var strong = RenderRun(typeface, Sample, 13, OriginX, TextHintingMode.Strong, TextRenderingMode.Antialias);
            var light = RenderRun(typeface, Sample, 13, OriginX, TextHintingMode.Light, TextRenderingMode.Antialias);

            Assert.True(strong.AsSpan().SequenceEqual(light),
                "Strong grayscale on the auto-hinter must render exactly like Light");
        }

        [Fact]
        public void Aliased_Strong_Renders_Identically_At_Fractional_Origins()
        {
            using var app = StartApp();
            var typeface = LoadFont("Inter-Regular.ttf");

            // Bi-level Strong keeps whole-pixel pens: two origins inside one pixel draw the
            // same bytes.
            var first = RenderRun(typeface, "HHH", 24, 8.26, TextHintingMode.Strong, TextRenderingMode.Alias);
            var second = RenderRun(typeface, "HHH", 24, 8.49, TextHintingMode.Strong, TextRenderingMode.Alias);

            Assert.True(first.AsSpan().SequenceEqual(second), "Aliased Strong output varies with subpixel origin");
        }

        [Theory]
        [MemberData(nameof(Fonts))]
        public void Aliased_Strong_Centres_The_Fitted_Ink_On_The_Shaper_Slot(string fontFile)
        {
            using var app = StartApp();
            var typeface = LoadFont(fontFile);
            var scratch = new GlyphPathBuilder();
            var failures = new List<string>();

            for (var px = 9; px <= 16; px++)
            {
                var scale = px / (double)typeface.Metrics.DesignEmHeight;
                var scaleQ = GlyphMaskKey.QuantizeScale(px);
                var infos = CreateInfos(typeface, Sample, px, out var positions);

                for (var k = 0; k < infos.Count; k++)
                {
                    var glyph = infos[k].GlyphIndex;

                    Assert.True(typeface.TryGetGlyphInkBounds(glyph, out var box));

                    // The fitted outline the bi-level mask is rasterized from stays in the
                    // scratch builder; its x extent against the design box is how far the
                    // fit moved the ink.
                    var mask = GlyphMasks.Build(typeface, scratch,
                        new GlyphMaskKey(glyph, scaleQ, 0, GlyphMaskMode.Aliased, GridFit: true, Strong: true));

                    Assert.True(scratch.TryGetPointBounds(out var fittedLeft, out _, out var fittedRight, out _));

                    var peaks = RenderIsolated(typeface, px, infos, k, TextHintingMode.Strong, TextRenderingMode.Alias);
                    var drawnColumn = Array.FindIndex(peaks, p => p > 0.5f);
                    var maskColumn = FirstInkColumn(mask);

                    if (drawnColumn < 0 || maskColumn is null)
                    {
                        continue;
                    }

                    // The pen pixel the glyph was drawn at, and the fitted ink centre on it. The
                    // run origin snaps to a whole pixel as a whole, which moves every glyph
                    // alike, so the slot is measured from the snapped origin.
                    var pen = drawnColumn - (mask.Left + maskColumn.Value);
                    var drawnCentre = pen + (fittedLeft + fittedRight) / 2.0;
                    var slotCentre = Math.Round(OriginX) + positions[k] + (box.XMin + box.XMax) * scale / 2;
                    var error = drawnCentre - slotCentre;

                    if (Math.Abs(error) > 0.5 + 1.0 / 64)
                    {
                        failures.Add(FormattableString.Invariant(
                            $"{px}px '{Sample[k]}': fitted ink centre {error:+0.000;-0.000} px off its slot"));
                    }
                }
            }

            Assert.True(failures.Count == 0, $"{fontFile}: " + string.Join("; ", failures));
        }

        /// <summary>
        /// Per neighbour pair at 9-16 px: the drawn ink gap minus the gap between the unhinted
        /// ink boxes at the shaper's fractional positions.
        /// </summary>
        private static List<double> MeasureGapErrors(GlyphTypeface typeface, TextHintingMode hinting)
        {
            var errors = new List<double>();

            for (var px = 9; px <= 16; px++)
            {
                var scale = px / (double)typeface.Metrics.DesignEmHeight;
                var infos = CreateInfos(typeface, Sample, px, out var positions);
                var left = new double[infos.Count];
                var right = new double[infos.Count];
                var idealLeft = new double[infos.Count];
                var idealRight = new double[infos.Count];

                for (var k = 0; k < infos.Count; k++)
                {
                    Assert.True(typeface.TryGetGlyphInkBounds(infos[k].GlyphIndex, out var box));

                    idealLeft[k] = OriginX + positions[k] + box.XMin * scale;
                    idealRight[k] = OriginX + positions[k] + box.XMax * scale;

                    var peaks = RenderIsolated(typeface, px, infos, k, hinting, TextRenderingMode.Antialias);

                    Assert.True(TryGetEdges(peaks, out left[k], out right[k]),
                        $"glyph '{Sample[k]}' drew no ink at {px}px");
                }

                for (var k = 0; k + 1 < infos.Count; k++)
                {
                    errors.Add(left[k + 1] - right[k] - (idealLeft[k + 1] - idealRight[k]));
                }
            }

            return errors;
        }

        private static int? FirstInkColumn(GlyphMask mask)
        {
            for (var x = 0; x < mask.Width; x++)
            {
                for (var y = 0; y < mask.Height; y++)
                {
                    if (mask.Alpha[y * mask.Width + x] >= 128)
                    {
                        return x;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Fractional ink edges in device pixels: the extreme columns contribute their peak
        /// coverage, so a stem edge a third into a pixel reads a third in.
        /// </summary>
        private static bool TryGetEdges(float[] peaks, out double left, out double right)
        {
            left = right = 0;

            var first = Array.FindIndex(peaks, p => p > 0.02f);
            var last = Array.FindLastIndex(peaks, p => p > 0.02f);

            if (first < 0)
            {
                return false;
            }

            left = first + (1 - peaks[first]);
            right = last + peaks[last];

            return true;
        }

        private static List<GlyphInfo> CreateInfos(GlyphTypeface typeface, string text, double emSize,
            out double[] positions)
        {
            var scale = emSize / typeface.Metrics.DesignEmHeight;
            var infos = new List<GlyphInfo>();
            var pen = 0.0;

            positions = new double[text.Length];

            for (var i = 0; i < text.Length; i++)
            {
                var glyph = typeface.CharacterToGlyphMap[text[i]];

                Assert.NotEqual(0, glyph);
                typeface.TryGetGlyphMetrics(glyph, out var metrics);

                var advance = metrics.AdvanceWidth * scale;

                positions[i] = pen;
                infos.Add(new GlyphInfo(glyph, i, advance));
                pen += advance;
            }

            return infos;
        }

        /// <summary>
        /// Renders the run with every glyph but <paramref name="keep"/> swapped for the space
        /// glyph, keeping all advances, and returns each device column's peak coverage.
        /// </summary>
        private static float[] RenderIsolated(GlyphTypeface typeface, double emSize, List<GlyphInfo> infos, int keep,
            TextHintingMode hinting, TextRenderingMode rendering)
        {
            var space = typeface.CharacterToGlyphMap[' '];
            var isolated = new List<GlyphInfo>(infos.Count);

            for (var i = 0; i < infos.Count; i++)
            {
                isolated.Add(i == keep
                    ? infos[i]
                    : new GlyphInfo(space, infos[i].GlyphCluster, infos[i].GlyphAdvance));
            }

            using var run = new ManagedGlyphRunImpl(typeface, emSize, isolated, new Point(OriginX, BaselineY));
            var bytes = Render(run, hinting, rendering);
            var peaks = new float[SurfaceWidth];

            for (var y = 0; y < SurfaceHeight; y++)
            {
                for (var x = 0; x < SurfaceWidth; x++)
                {
                    // Black on white: the green channel's darkness is the drawn coverage.
                    var coverage = 1 - bytes[(y * SurfaceWidth + x) * 4 + 1] / 255f;

                    peaks[x] = Math.Max(peaks[x], coverage);
                }
            }

            return peaks;
        }

        private static byte[] RenderRun(GlyphTypeface typeface, string text, double emSize, double originX,
            TextHintingMode hinting, TextRenderingMode rendering)
        {
            var infos = CreateInfos(typeface, text, emSize, out _);

            using var run = new ManagedGlyphRunImpl(typeface, emSize, infos, new Point(originX, BaselineY));

            return Render(run, hinting, rendering);
        }

        private static byte[] Render(ManagedGlyphRunImpl run, TextHintingMode hinting, TextRenderingMode rendering)
        {
            var info = new SKImageInfo(SurfaceWidth, SurfaceHeight, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(info);

            using (var context = new Avalonia.Skia.DrawingContextImpl(new Avalonia.Skia.DrawingContextImpl.CreateInfo
                   {
                       Surface = surface,
                       Canvas = surface.Canvas,
                       Dpi = new Vector(96, 96),
                   }))
            {
                surface.Canvas.Clear(SKColors.White);
                context.PushTextOptions(new TextOptions { TextHintingMode = hinting, TextRenderingMode = rendering });
                context.DrawGlyphRun(Brushes.Black, run);
                context.PopTextOptions();
            }

            using var snapshot = surface.Snapshot();
            using var readback = new SKBitmap(info);

            Assert.True(snapshot.ReadPixels(info, readback.GetPixels(), readback.RowBytes, 0, 0));

            var bytes = new byte[info.Width * info.Height * 4];

            System.Runtime.InteropServices.Marshal.Copy(readback.GetPixels(), bytes, 0, bytes.Length);

            return bytes;
        }

        /// <summary>
        /// A system font from the Windows font folder, or a render-test asset, served through
        /// the synthetic platform typeface so only the managed pipeline draws it.
        /// </summary>
        private static GlyphTypeface LoadFont(string fileName)
        {
            string path;

            if (fileName.StartsWith("Inter", StringComparison.Ordinal))
            {
                var directory = new DirectoryInfo(AppContext.BaseDirectory);

                while (directory is not null && directory.Name != "tests")
                {
                    directory = directory.Parent;
                }

                Assert.NotNull(directory);
                path = Path.Combine(directory!.FullName, "Avalonia.RenderTests", "Assets", fileName);
            }
            else
            {
                Assert.SkipWhen(!OperatingSystem.IsWindows(), "Relies on the Windows-shipped fonts.");
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), fileName);
                Assert.SkipWhen(!File.Exists(path), $"{fileName} is not installed.");
            }

            var typeface = SyntheticFont.FromBytes(File.ReadAllBytes(path)).TryCreateGlyphTypeface();

            Assert.NotNull(typeface);

            return typeface!;
        }

        private static IDisposable StartApp()
            => UnitTestApplication.Start(TestServices.MockPlatformRenderInterface
                .With(renderInterface: new Avalonia.Skia.PlatformRenderInterface(null)));
    }
}
