using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.UnitTests;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>Scratch probe: the demo waterfall rendered through the real draw path
    /// (DrawingContextImpl + TextOptions) side by side per hinting mode, subpixel and
    /// grayscale, at a fractional pen origin. Env-gated, not part of the suite.</summary>
    public class HintingWaterfallProbe
    {
        [Fact]
        public void Dump_Waterfall_Per_Hinting_Mode()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("HINTING_WATERFALL_DIR") is not { Length: > 0 }, "probe");

            var directory = Environment.GetEnvironmentVariable("HINTING_WATERFALL_DIR")!;

            Directory.CreateDirectory(directory);

            using var app = UnitTestApplication.Start(TestServices.MockPlatformRenderInterface
                .With(renderInterface: new PlatformRenderInterface(null)));

            var typeface = LoadTypeface();
            var sizes = new[] { 8.0, 9, 10, 11, 12, 13, 14, 16, 20 };
            const string sample = "Hamburgefonstiv Base8S 123";
            const int columnWidth = 420;
            const int rowHeight = 30;

            var modes = new[] { TextHintingMode.None, TextHintingMode.Light, TextHintingMode.Strong };
            var info = new SKImageInfo(columnWidth * modes.Length, sizes.Length * rowHeight + 30,
                SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(info, new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal));

            surface.Canvas.Clear(SKColors.White);

            using (var context = new Avalonia.Skia.DrawingContextImpl(new Avalonia.Skia.DrawingContextImpl.CreateInfo
                   {
                       Surface = surface,
                       Canvas = surface.Canvas,
                       Dpi = new Vector(96, 96),
                       SurfaceIsDisplay = true,
                   }))
            {
                for (var m = 0; m < modes.Length; m++)
                {
                    using (var font = new SKFont(SKTypeface.Default, 12))
                    using (var label = new SKPaint { Color = SKColors.Gray })
                    {
                        surface.Canvas.DrawText(modes[m].ToString(), columnWidth * m + 8.0f, 18,
                            SKTextAlign.Left, font, label);
                    }

                    context.PushTextOptions(new TextOptions
                    {
                        TextRenderingMode = TextRenderingMode.SubpixelAntialias,
                        TextHintingMode = modes[m],
                    });

                    for (var s = 0; s < sizes.Length; s++)
                    {
                        // Fractional origin so Light exercises subpixel phases.
                        using var run = CreateRun(typeface, sample, sizes[s],
                            new Point(columnWidth * m + 8.3, 30 + rowHeight * s + 20));

                        context.DrawGlyphRun(Brushes.Black, run);
                    }

                    context.PopTextOptions();
                }
            }

            using var snapshot = surface.Snapshot();
            using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(Path.Combine(directory, "hinting-waterfall.png"));

            data.SaveTo(file);

            Assert.Fail(Path.Combine(directory, "hinting-waterfall.png"));
        }

        private static ManagedGlyphRunImpl CreateRun(GlyphTypeface typeface, string text,
            double emSize, Point origin)
        {
            var scale = emSize / typeface.Metrics.DesignEmHeight;
            var infos = new List<GlyphInfo>();
            var cluster = 0;

            foreach (var c in text)
            {
                var glyph = typeface.CharacterToGlyphMap[c];

                typeface.TryGetGlyphMetrics(glyph, out var metrics);
                infos.Add(new GlyphInfo(glyph, cluster++, metrics.AdvanceWidth * scale));
            }

            return new ManagedGlyphRunImpl(typeface, emSize, infos, origin);
        }

        private static GlyphTypeface LoadTypeface()
        {
            using var skTypeface = SKFontManager.Default.MatchFamily("Segoe UI", SKFontStyle.Normal);

            Assert.NotNull(skTypeface);

            return new GlyphTypeface(new Avalonia.Skia.SkiaTypeface(skTypeface!, FontSimulations.None));
        }
    }
}
