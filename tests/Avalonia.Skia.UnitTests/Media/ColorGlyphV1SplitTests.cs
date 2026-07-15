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
        public void V1_Foreground_Sentinel_Follows_The_Brush()
        {
            using var scope = CreateEnvironment();
            var typeface = CreateV1Typeface(out var v1Glyph, paletteIndex: 0xFFFF);

            var green = RenderSingle(typeface, v1Glyph, Brushes.Green);
            var red = RenderSingle(typeface, v1Glyph, Brushes.Red);

            // The paint's palette entry is the CPAL sentinel: the same glyph must track the
            // run's foreground across draws (R13 for the v1 drawing path).
            Assert.True(green.green > 8 && green.red <= 2,
                $"expected green sentinel paint, found green={green.green} red={green.red}");
            Assert.True(red.red > 8 && red.green <= 2,
                $"expected red sentinel paint, found red={red.red} green={red.green}");
        }

        private static (int red, int green) RenderSingle(GlyphTypeface typeface, ushort glyph, ISolidColorBrush brush)
        {
            var run = CreateRun(typeface, new[] { glyph });

            var info = new SKImageInfo(72, 56, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var contextImpl = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));
            using var context = new PlatformDrawingContext(contextImpl, ownsImpl: false);

            canvas.Clear(SKColors.White);
            Assert.True(ColorGlyphRunSplitter.TryDraw(context, run, brush));

            var pixels = bitmap.GetPixelSpan();
            var red = 0;
            var green = 0;

            for (var i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 2] > 150 && pixels[i + 1] < 100 && pixels[i] < 100)
                {
                    red++;
                }
                else if (pixels[i + 1] > 100 && pixels[i + 2] < 100 && pixels[i] < 100)
                {
                    green++;
                }
            }

            return (red, green);
        }

        [Fact]
        public void Backend_Mode_Splits_V0_Glyphs_Through_Our_Drawings()
        {
            // Explicit Backend mode, where the blob would otherwise rasterize COLR
            // itself — so v0 splits to our layer drawings too.
            using var scope = CreateEnvironment(managed: false);
            var typeface = CreateV0Typeface(out var v0Glyph);

            var run = CreateRun(typeface, new[] { v0Glyph });

            var info = new SKImageInfo(72, 56, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var contextImpl = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));
            using var context = new PlatformDrawingContext(contextImpl, ownsImpl: false);

            canvas.Clear(SKColors.White);

            Assert.True(ColorGlyphRunSplitter.TryDraw(context, run, Brushes.Black),
                "backend mode declined a v0 color run");

            var pixels = bitmap.GetPixelSpan();
            var red = 0;

            for (var i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 2] > 150 && pixels[i] < 100 && pixels[i + 1] < 100)
                {
                    red++;
                }
            }

            Assert.True(red > 8, $"expected red v0 layer pixels via our drawing, found {red}");
        }

        [Fact]
        public void Managed_Mode_Keeps_V0_Runs_For_The_Mask_Renderer()
        {
            using var scope = CreateEnvironment();
            var typeface = CreateV0Typeface(out var v0Glyph);

            var run = CreateRun(typeface, new[] { v0Glyph });

            var info = new SKImageInfo(48, 48, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var contextImpl = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));
            using var context = new PlatformDrawingContext(contextImpl, ownsImpl: false);

            Assert.False(ColorGlyphRunSplitter.TryDraw(context, run, Brushes.Black));
        }

        private static GlyphTypeface CreateV0Typeface(out ushort v0Glyph)
        {
            var baseFont = SyntheticFont.FromBytes(LoadFontBytes("Inter-Regular.ttf"));
            var probe = baseFont.TryCreateGlyphTypeface();
            Assert.NotNull(probe);

            var baseGlyph = probe!.CharacterToGlyphMap['H'];
            var layerGlyph = probe.CharacterToGlyphMap['A'];

            // COLR v0: one base record, one layer on palette 0 (red).
            var colr = new BigEndianBuffer();
            colr.UInt16(0).UInt16(1).UInt32(14).UInt32(20).UInt16(1)
                .UInt16(baseGlyph).UInt16(0).UInt16(1)
                .UInt16(layerGlyph).UInt16(0);

            var grafted = ColrTestFont.Graft(
                baseFont, colr.ToArray(), ColrTestFont.Cpal(new[] { new[] { Colors.Red } })).ToBytes();

            using var skData = SKData.CreateCopy(grafted);
            var skTypeface = SKTypeface.FromData(skData);
            Assert.NotNull(skTypeface);

            v0Glyph = baseGlyph;
            return new GlyphTypeface(new SkiaTypeface(skTypeface!, FontSimulations.None));
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

        private static GlyphTypeface CreateV1Typeface(out ushort v1Glyph, int paletteIndex = 0)
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
                BuildColrV1GlyphSolid(baseGlyph, outlineGlyph, paletteIndex),
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
        private static byte[] BuildColrV1GlyphSolid(ushort baseGlyph, ushort outlineGlyph, int paletteIndex = 0)
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
            colr.UInt16(paletteIndex);
            colr.F2Dot14(1.0);

            return colr.ToArray();
        }

        private static IDisposable CreateEnvironment(bool managed = true)
        {
            var scope = AvaloniaLocator.EnterScope();

            AvaloniaLocator.CurrentMutable
                .Bind<IPlatformRenderInterface>().ToConstant(new PlatformRenderInterface());

            // Managed is the framework default now, so backend-mode tests must opt out
            // explicitly rather than relying on unregistered options.
            AvaloniaLocator.CurrentMutable
                .Bind<FontManagerOptions>().ToConstant(new FontManagerOptions
                {
                    TextRasterizationMode = managed
                        ? TextRasterizationMode.Managed
                        : TextRasterizationMode.Backend,
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
