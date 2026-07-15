using System;
using System.Buffers.Binary;
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
    /// <summary>
    /// Gasp respect for Unspecified hinting: fonts whose gasp table requests classic grid
    /// fitting without any ClearType-aware flag at the rendered ppem (legacy fonts like
    /// Courier New) escalate Unspecified to the full grid fit - integer pens and stem
    /// snapping - the way DirectWrite picks GDI-classic rendering for them. ClearType-aware
    /// ranges and fonts without a gasp table keep the natural Light treatment, and an
    /// explicit hinting choice always wins over the table.
    /// </summary>
    public class GaspHintingTests
    {
        // Courier New's actual table: version 0; <=8 grayscale only, <=36 gridfit only
        // (the crisp range), above that gridfit + grayscale. No ClearType-aware flags.
        private static byte[] LegacyGasp() => BuildGasp(0, (8, 0x0002), (36, 0x0001), (0xFFFF, 0x0003));

        // Segoe UI's actual table: version 1; the 9-19 range has GRIDFIT set but also
        // SYMMETRIC_GRIDFIT - a ClearType-tuned font that must NOT escalate.
        private static byte[] ClearTypeAwareGasp() => BuildGasp(1, (8, 0x000A), (19, 0x0007), (0xFFFF, 0x000F));

        [Fact]
        public void Unspecified_Escalates_To_Full_Grid_Fit_When_Gasp_Requests_Classic_Grid_Fit()
        {
            using var app = StartApp();
            var typeface = CreateTypeface(LegacyGasp());

            // 24 px sits in the gridfit-only range (<= 36): Unspecified must render exactly
            // like Strong - pens rounded, stems snapped - and not like Light.
            var unspecified = Render(typeface, 24, 8.26, TextHintingMode.Unspecified);
            var strong = Render(typeface, 24, 8.26, TextHintingMode.Strong);
            var light = Render(typeface, 24, 8.26, TextHintingMode.Light);

            Assert.True(unspecified.AsSpan().SequenceEqual(strong),
                "Unspecified must escalate to the full grid fit when gasp requests classic gridfit");
            Assert.False(unspecified.AsSpan().SequenceEqual(light),
                "escalated output should differ from Light at a fractional origin");
        }

        [Fact]
        public void Unspecified_Stays_Light_For_ClearType_Aware_Gasp()
        {
            using var app = StartApp();
            var typeface = CreateTypeface(ClearTypeAwareGasp());

            // 13 px hits the (19, 0x0007) range: GRIDFIT is set, but SYMMETRIC_GRIDFIT marks
            // the font ClearType-tuned - natural Light rendering, no escalation.
            var unspecified = Render(typeface, 13, 8.26, TextHintingMode.Unspecified);
            var light = Render(typeface, 13, 8.26, TextHintingMode.Light);
            var strong = Render(typeface, 13, 8.26, TextHintingMode.Strong);

            Assert.True(unspecified.AsSpan().SequenceEqual(light),
                "ClearType-aware gasp ranges must keep the natural Light treatment");
            Assert.False(unspecified.AsSpan().SequenceEqual(strong),
                "ClearType-aware gasp must not escalate to the full grid fit");
        }

        [Fact]
        public void Unspecified_Stays_Light_Without_A_Gasp_Table()
        {
            using var app = StartApp();
            var typeface = CreateTypeface(gasp: null);

            var unspecified = Render(typeface, 24, 8.26, TextHintingMode.Unspecified);
            var light = Render(typeface, 24, 8.26, TextHintingMode.Light);

            Assert.True(unspecified.AsSpan().SequenceEqual(light),
                "fonts without a gasp table keep the natural Light treatment");
        }

        [Fact]
        public void Explicit_Light_Wins_Over_A_Legacy_Gasp_Table()
        {
            using var app = StartApp();
            var typeface = CreateTypeface(LegacyGasp());

            var light = Render(typeface, 24, 8.26, TextHintingMode.Light);
            var strong = Render(typeface, 24, 8.26, TextHintingMode.Strong);

            Assert.False(light.AsSpan().SequenceEqual(strong),
                "an explicit Light request must not be escalated by the gasp table");
        }

        private static byte[] BuildGasp(ushort version, params (ushort MaxPpem, ushort Behavior)[] ranges)
        {
            var bytes = new byte[4 + ranges.Length * 4];

            BinaryPrimitives.WriteUInt16BigEndian(bytes, version);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), (ushort)ranges.Length);

            for (var i = 0; i < ranges.Length; i++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4 + i * 4), ranges[i].MaxPpem);
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6 + i * 4), ranges[i].Behavior);
            }

            return bytes;
        }

        /// <summary>Inter with the given gasp table injected (or removed), served through the
        /// synthetic platform typeface so only the managed pipeline is exercised.</summary>
        private static GlyphTypeface CreateTypeface(byte[]? gasp)
        {
            var font = SyntheticFont.FromBytes(LoadInterBytes());

            if (gasp is null)
            {
                font.Remove("gasp");
            }
            else
            {
                font.Replace("gasp", gasp);
            }

            var typeface = font.TryCreateGlyphTypeface();

            Assert.NotNull(typeface);

            return typeface!;
        }

        private static byte[] Render(GlyphTypeface typeface, double emSize, double originX, TextHintingMode hinting)
        {
            var info = new SKImageInfo(140, 48, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(info, new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal));
            using var run = CreateRun(typeface, "HHH", emSize, originX);

            using (var context = new Avalonia.Skia.DrawingContextImpl(new Avalonia.Skia.DrawingContextImpl.CreateInfo
                   {
                       Surface = surface,
                       Canvas = surface.Canvas,
                       Dpi = new Vector(96, 96),
                   }))
            {
                surface.Canvas.Clear(SKColors.White);
                context.PushTextOptions(new TextOptions { TextHintingMode = hinting });
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

        private static ManagedGlyphRunImpl CreateRun(GlyphTypeface typeface, string text,
            double emSize, double originX)
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

            return new ManagedGlyphRunImpl(typeface, emSize, infos, new Point(originX, 34));
        }

        private static byte[] LoadInterBytes()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && directory.Name != "tests")
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);

            return File.ReadAllBytes(Path.Combine(directory!.FullName, "Avalonia.RenderTests", "Assets", "Inter-Regular.ttf"));
        }

        private static IDisposable StartApp()
            => UnitTestApplication.Start(TestServices.MockPlatformRenderInterface
                .With(renderInterface: new Avalonia.Skia.PlatformRenderInterface(null)));
    }
}
