using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Skia.Helpers;
using Avalonia.UnitTests;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// The COLR v1 record-time split: v1 glyphs draw through the typeface's own paint graphs
    /// (the "prefer our implementation" rule), the surrounding stretches keep glyph-run nodes,
    /// and runs without v1 content decline the split entirely.
    /// </summary>
    public class ColorGlyphV1SplitTests
    {
        [Fact]
        public void V1_Glyphs_Draw_Through_Our_Painter_Between_Ordinary_Segments()
        {
            using var scope = CreateEnvironment();
            var typeface = CreateV1Typeface(out var v1Glyph);

            var glyphA = typeface.CharacterToGlyphMap['A'];
            var glyphB = typeface.CharacterToGlyphMap['B'];

            var run = CreateRun(typeface, new[] { glyphA, v1Glyph, glyphB });

            var info = new SKImageInfo(160, 56, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var contextImpl = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));
            using var context = new PlatformDrawingContext(contextImpl, ownsImpl: false);

            canvas.Clear(SKColors.White);

            Assert.True(ColorGlyphRunSplitter.TryDraw(context, run, Brushes.Black),
                "the splitter declined a run containing a v1 glyph");

            var pixels = bitmap.GetPixelSpan().ToArray();
            var red = 0;
            var black = 0;

            for (var i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 2] > 150 && pixels[i] < 100 && pixels[i + 1] < 100)
                {
                    red++;
                }
                else if (pixels[i + 2] < 60 && pixels[i] < 60 && pixels[i + 1] < 60 && pixels[i + 3] == 255)
                {
                    black++;
                }
            }

            // The v1 glyph paints solid palette red through our resolver; the neighboring
            // ordinary glyphs render as black sub-runs. Both must be present — red missing
            // means the paint graph did not draw, black missing means the segments were lost.
            Assert.True(red > 8, $"expected red v1 paint pixels, found {red}");
            Assert.True(black > 8, $"expected black segment pixels, found {black}");
        }

        [Fact]
        public void Runs_Without_V1_Content_Decline_The_Split()
        {
            using var scope = CreateEnvironment();

            var bytes = LoadFontBytes("Inter-Regular.ttf");
            using var skData = SKData.CreateCopy(bytes);
            var typeface = new GlyphTypeface(new SkiaTypeface(SKTypeface.FromData(skData)!, FontSimulations.None));

            var run = CreateRun(typeface, new[]
            {
                typeface.CharacterToGlyphMap['A'],
                typeface.CharacterToGlyphMap['B'],
            });

            var info = new SKImageInfo(64, 32, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var contextImpl = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));
            using var context = new PlatformDrawingContext(contextImpl, ownsImpl: false);

            Assert.False(ColorGlyphRunSplitter.TryDraw(context, run, Brushes.Black));
        }

        private static GlyphRun CreateRun(GlyphTypeface typeface, ushort[] glyphs)
        {
            var scale = 32.0 / typeface.Metrics.DesignEmHeight;
            var infos = new List<GlyphInfo>();
            var cluster = 0;

            foreach (var glyph in glyphs)
            {
                typeface.TryGetGlyphMetrics(glyph, out var metrics);
                infos.Add(new GlyphInfo(glyph, cluster++, Math.Max(metrics.AdvanceWidth * scale, 14)));
            }

            return new GlyphRun(typeface, 32, default, infos, new Point(8, 44));
        }

        private static GlyphTypeface CreateV1Typeface(out ushort v1Glyph)
        {
            var baseFont = SyntheticFont.FromBytes(LoadFontBytes("Inter-Regular.ttf"));
            var probe = baseFont.TryCreateGlyphTypeface();
            Assert.NotNull(probe);

            var baseGlyph = probe!.CharacterToGlyphMap['H'];
            var outlineGlyph = probe.CharacterToGlyphMap['A'];

            // Grafted bytes round-trip through Skia so the run gets a real platform typeface;
            // our tables parse the same bytes for the COLR side.
            var grafted = ColrTestFont.Graft(
                baseFont,
                BuildColrV1GlyphSolid(baseGlyph, outlineGlyph),
                ColrTestFont.Cpal(new[] { new[] { Colors.Red } })).ToBytes();

            using var skData = SKData.CreateCopy(grafted);
            var skTypeface = SKTypeface.FromData(skData);
            Assert.NotNull(skTypeface);

            v1Glyph = baseGlyph;
            return new GlyphTypeface(new SkiaTypeface(skTypeface!, FontSimulations.None));
        }

        /// <summary>
        /// COLR v1: base glyph → PaintGlyph(outlineGlyph) → PaintSolid on palette entry 0 —
        /// the same sequential layout the COLR characterization tests use.
        /// </summary>
        private static byte[] BuildColrV1GlyphSolid(ushort baseGlyph, ushort outlineGlyph)
        {
            var colr = new BigEndianBuffer();

            colr.UInt16(1);
            colr.UInt16(0);
            colr.UInt32(0);
            colr.UInt32(0);
            colr.UInt16(0);
            var baseListOffsetPos = colr.ReserveOffset32();
            colr.UInt32(0);
            colr.UInt32(0);
            colr.UInt32(0);
            colr.UInt32(0);

            var baseListStart = colr.Position;
            colr.PatchUInt32(baseListOffsetPos, (uint)baseListStart);
            colr.UInt32(1);
            colr.UInt16(baseGlyph);
            var recordPaintOffsetPos = colr.ReserveOffset32();

            colr.PatchUInt32(recordPaintOffsetPos, (uint)(colr.Position - baseListStart));
            colr.UInt8(10);
            colr.UInt24(6);
            colr.UInt16(outlineGlyph);

            colr.UInt8(2);
            colr.UInt16(0);
            colr.F2Dot14(1.0);

            return colr.ToArray();
        }

        private static IDisposable CreateEnvironment()
        {
            var scope = AvaloniaLocator.EnterScope();

            AvaloniaLocator.CurrentMutable
                .Bind<IPlatformRenderInterface>().ToConstant(new PlatformRenderInterface());
            AvaloniaLocator.CurrentMutable
                .Bind<FontManagerOptions>().ToConstant(new FontManagerOptions
                {
                    TextRasterizationMode = TextRasterizationMode.Managed,
                });

            return scope;
        }

        private static byte[] LoadFontBytes(string fileName)
        {
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
