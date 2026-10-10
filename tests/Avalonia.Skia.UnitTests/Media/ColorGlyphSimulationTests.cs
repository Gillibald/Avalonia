using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Skia.Helpers;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Font simulations apply to outline glyphs only: colour glyphs (COLR v0 and v1, CBDT and
    /// sbix strikes) render exactly as the unsimulated face draws them, while the outline
    /// glyphs of the same run are still emboldened and slanted.
    /// </summary>
    public class ColorGlyphSimulationTests
    {
        private const int Width = 120;
        private const int Height = 56;
        private const double EmSize = 32;

        public enum ColorKind
        {
            ColrV0,
            ColrV1,
            Cbdt,
            Sbix,
        }

        public static IEnumerable<object[]> ColorKindsAndSimulations()
        {
            foreach (var kind in new[] { ColorKind.ColrV0, ColorKind.ColrV1, ColorKind.Cbdt, ColorKind.Sbix })
            {
                foreach (var simulations in Simulations)
                {
                    yield return new object[] { kind, simulations };
                }
            }
        }

        private static readonly FontSimulations[] Simulations =
        {
            FontSimulations.Bold, FontSimulations.Oblique, FontSimulations.Bold | FontSimulations.Oblique,
        };

        [Theory]
        [MemberData(nameof(ColorKindsAndSimulations))]
        public void Colour_Glyphs_Are_Not_Simulated_On_The_Mask_Path(ColorKind kind, FontSimulations simulations)
        {
            using var scope = CreateEnvironment();
            var typeface = CreateTypeface(kind, out var colorGlyph, out _);

            var expected = RenderMask(typeface, new[] { colorGlyph });
            var actual = RenderMask(typeface.WithSimulations(simulations), new[] { colorGlyph });

            Assert.True(CountInk(expected) > 20, $"{kind}: the unsimulated colour glyph drew nothing");
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"{kind} {simulations}: the colour glyph changed");
        }

        [Theory]
        [InlineData(FontSimulations.Bold)]
        [InlineData(FontSimulations.Oblique)]
        [InlineData(FontSimulations.Bold | FontSimulations.Oblique)]
        public void Colour_V0_Glyphs_Are_Not_Simulated_On_The_Transformed_Mask_Path(FontSimulations simulations)
        {
            using var scope = CreateEnvironment();
            var typeface = CreateTypeface(ColorKind.ColrV0, out var colorGlyph, out var plainGlyph);

            // Rotated, the upright mask path declines the run and the transformed tier draws its
            // COLR layers; strikes never reach that tier.
            var expected = RenderTransformed(typeface, colorGlyph);
            var actual = RenderTransformed(typeface.WithSimulations(simulations), colorGlyph);

            Assert.True(CountInk(expected) > 20, "the unsimulated colour glyph drew nothing");
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"{simulations}: the colour glyph changed");

            // The outline glyphs of the same face are still simulated on this tier.
            Assert.False(RenderTransformed(typeface, plainGlyph).AsSpan()
                    .SequenceEqual(RenderTransformed(typeface.WithSimulations(simulations), plainGlyph)),
                $"{simulations}: the outline glyph was not simulated");
        }

        [Theory]
        [InlineData(FontSimulations.Bold)]
        [InlineData(FontSimulations.Oblique)]
        [InlineData(FontSimulations.Bold | FontSimulations.Oblique)]
        public void Colour_V1_Glyphs_Are_Not_Simulated_Through_The_Split(FontSimulations simulations)
        {
            // The record-time split is the vector path; under managed rasterization it only
            // splits v1 glyphs with colour masks switched off.
            using var masksOff = ColorGlyphMaskTests.SwitchColorMasksOff();
            using var scope = CreateEnvironment();
            var typeface = ColorGlyphV1SplitTests.CreateV1Typeface(out var v1Glyph);

            var expected = RenderSplit(typeface, v1Glyph);
            var actual = RenderSplit(typeface.WithSimulations(simulations), v1Glyph);

            Assert.True(CountInk(expected) > 20, "the unsimulated v1 glyph drew nothing");
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"{simulations}: the v1 glyph changed");
        }

        [Theory]
        [MemberData(nameof(ColorKindsAndSimulations))]
        public void Mixed_Runs_Simulate_Only_Their_Outline_Glyphs(ColorKind kind, FontSimulations simulations)
        {
            using var scope = CreateEnvironment();
            var typeface = CreateTypeface(kind, out var colorGlyph, out var plainGlyph);

            var expected = RenderMask(typeface, new[] { plainGlyph, colorGlyph });
            var actual = RenderMask(typeface.WithSimulations(simulations), new[] { plainGlyph, colorGlyph });

            // The outline glyph owns the columns left of the colour glyph's pen; its slanted
            // or emboldened ink stays well inside them.
            var split = (int)(8 + PlainAdvance);

            Assert.False(Columns(expected, 0, split).AsSpan().SequenceEqual(Columns(actual, 0, split)),
                $"{kind} {simulations}: the outline glyph was not simulated");
            Assert.True(Columns(expected, split, Width).AsSpan().SequenceEqual(Columns(actual, split, Width)),
                $"{kind} {simulations}: the colour glyph changed");
        }

        private const double PlainAdvance = 40;

        private static GlyphTypeface CreateTypeface(ColorKind kind, out ushort colorGlyph, out ushort plainGlyph)
        {
            switch (kind)
            {
                case ColorKind.ColrV0:
                {
                    var typeface = ColorGlyphV1SplitTests.CreateV0Typeface(out colorGlyph);
                    plainGlyph = typeface.CharacterToGlyphMap['A'];
                    return typeface;
                }
                case ColorKind.ColrV1:
                {
                    var typeface = ColorGlyphV1SplitTests.CreateV1Typeface(out colorGlyph);
                    plainGlyph = typeface.CharacterToGlyphMap['A'];
                    return typeface;
                }
                case ColorKind.Cbdt:
                    return BitmapGlyphRenderingTests.CreateBitmapTypeface(out colorGlyph, out plainGlyph);
                default:
                {
                    var typeface = BitmapGlyphRenderingTests.CreateSbixTypeface(out colorGlyph);
                    plainGlyph = typeface.CharacterToGlyphMap['A'];
                    return typeface;
                }
            }
        }

        private static byte[] RenderMask(GlyphTypeface typeface, ushort[] glyphs)
        {
            var infos = new List<GlyphInfo>();

            for (var i = 0; i < glyphs.Length; i++)
            {
                infos.Add(new GlyphInfo(glyphs[i], i, i < glyphs.Length - 1 ? PlainAdvance : 32));
            }

            using var run = new ManagedGlyphRunImpl(typeface, EmSize, infos, new Point(8, 40));

            var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var context = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));

            canvas.Clear(SKColors.White);

            Assert.True(MaskGlyphRunRenderer.TryDraw(context, run, Brushes.Black, TextRenderingMode.Antialias,
                TextHintingMode.None), "the mask path declined the run");

            return bitmap.GetPixelSpan().ToArray();
        }

        private static byte[] RenderTransformed(GlyphTypeface typeface, ushort glyph)
        {
            using var run = new ManagedGlyphRunImpl(typeface, EmSize, new List<GlyphInfo> { new(glyph, 0, 32) },
                new Point(8, 40));

            var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var context = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));

            canvas.Clear(SKColors.White);
            context.Transform = Matrix.CreateRotation(0.1) * Matrix.CreateTranslation(6, -4);

            Assert.True(MaskGlyphRunRenderer.TryDrawTransformed(context, run, Brushes.Black,
                TextRenderingMode.Antialias), "the transformed mask path declined the run");

            return bitmap.GetPixelSpan().ToArray();
        }

        private static byte[] RenderSplit(GlyphTypeface typeface, ushort glyph)
        {
            using var run = new GlyphRun(typeface, EmSize, default,
                new List<GlyphInfo> { new(glyph, 0, 32) }, new Point(8, 40));

            var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var contextImpl = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));
            using var context = new PlatformDrawingContext(contextImpl, ownsImpl: false);

            canvas.Clear(SKColors.White);

            Assert.True(ColorGlyphRunSplitter.TryDraw(context, run, Brushes.Black), "the split declined the run");

            return bitmap.GetPixelSpan().ToArray();
        }

        private static byte[] Columns(byte[] pixels, int from, int to)
        {
            var columns = new byte[(to - from) * 4 * Height];
            var width = (to - from) * 4;

            for (var y = 0; y < Height; y++)
            {
                Array.Copy(pixels, (y * Width + from) * 4, columns, y * width, width);
            }

            return columns;
        }

        private static int CountInk(byte[] pixels)
        {
            var ink = 0;

            for (var i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i] < 200 || pixels[i + 1] < 200 || pixels[i + 2] < 200)
                {
                    ink++;
                }
            }

            return ink;
        }

        private static IDisposable CreateEnvironment()
        {
            var scope = AvaloniaLocator.EnterScope();

            AvaloniaLocator.CurrentMutable
                .Bind<IPlatformRenderInterface>().ToConstant(new PlatformRenderInterface());
            AvaloniaLocator.CurrentMutable
                .Bind<IBitmapGlyphDecoder>().ToConstant(new SkiaBitmapGlyphDecoder());
            AvaloniaLocator.CurrentMutable
                .Bind<FontManagerOptions>().ToConstant(new FontManagerOptions
                {
                    TextRasterizationMode = TextRasterizationMode.Managed,
                });

            return scope;
        }
    }
}
