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
    /// COLR v0 through the managed mask path: flat-color layers composed server-side as tinted
    /// mask stacks (plan §5.4), including the 0xFFFF "use text foreground" palette sentinel —
    /// the mask-path half of the R13 gap, which the drawing-based v1 path still has open.
    /// </summary>
    public class ColorGlyphMaskRenderingTests
    {
        [Fact]
        public void V0_Layers_Render_With_Their_Palette_Colors()
        {
            using var scope = CreateEnvironment();
            var typeface = CreateColorTypeface(out var baseGlyph, out _);

            var pixels = Draw(typeface, baseGlyph, Brushes.Black);

            // Layer 0 ('l') is palette red, layer 1 ('o') palette blue — both must appear, and
            // the black foreground must not replace them.
            Assert.True(CountDominant(pixels, r: true) > 4, "no red layer pixels rendered");
            Assert.True(CountDominant(pixels, r: false) > 4, "no blue layer pixels rendered");
        }

        [Fact]
        public void V0_Foreground_Sentinel_Uses_The_Run_Tint()
        {
            using var scope = CreateEnvironment();
            var typeface = CreateColorTypeface(out _, out var sentinelGlyph);

            var green = Draw(typeface, sentinelGlyph, Brushes.Green);
            var red = Draw(typeface, sentinelGlyph, Brushes.Red);

            // The 0xFFFF layer follows the foreground: same glyph, different brush, different
            // pixels — and each matches its brush's hue.
            Assert.True(green.Count(p => p.G > 100 && p.R < 80 && p.B < 80) > 4, "no green sentinel pixels");
            Assert.True(red.Count(p => p.R > 100 && p.G < 80 && p.B < 80) > 4, "no red sentinel pixels");
        }

        private static GlyphTypeface CreateColorTypeface(out ushort colorGlyph, out ushort sentinelGlyph)
        {
            var baseFont = SyntheticFont.FromBytes(LoadFontBytes("Inter-Regular.ttf"));
            var probe = baseFont.TryCreateGlyphTypeface();
            Assert.NotNull(probe);

            var glyphA = probe!.CharacterToGlyphMap['A'];
            var glyphB = probe.CharacterToGlyphMap['B'];
            var glyphL = probe.CharacterToGlyphMap['l'];
            var glyphO = probe.CharacterToGlyphMap['o'];

            var colr = BuildColrV0(
                (glyphA, new[] { (glyphL, (ushort)0), (glyphO, (ushort)1) }),
                (glyphB, new[] { (glyphO, (ushort)0xFFFF) }));

            var cpal = ColrTestFont.Cpal(new[] { new[] { Colors.Red, Colors.Blue } });

            var typeface = ColrTestFont.Graft(baseFont, colr, cpal).TryCreateGlyphTypeface();
            Assert.NotNull(typeface);

            colorGlyph = glyphA;
            sentinelGlyph = glyphB;
            return typeface!;
        }

        private static byte[] BuildColrV0(params (ushort BaseGlyph, (ushort Glyph, ushort Palette)[] Layers)[] bases)
        {
            var sorted = bases.OrderBy(b => b.BaseGlyph).ToArray();
            var totalLayers = sorted.Sum(b => b.Layers.Length);
            var buffer = new BigEndianBuffer();

            buffer.UInt16(0)                                        // version 0
                .UInt16(sorted.Length)                              // numBaseGlyphRecords
                .UInt32(14)                                         // baseGlyphRecordsOffset
                .UInt32((uint)(14 + 6 * sorted.Length))             // layerRecordsOffset
                .UInt16(totalLayers);                               // numLayerRecords

            var firstLayerIndex = 0;

            foreach (var (baseGlyph, layers) in sorted)
            {
                buffer.UInt16(baseGlyph).UInt16(firstLayerIndex).UInt16(layers.Length);
                firstLayerIndex += layers.Length;
            }

            foreach (var (_, layers) in sorted)
            {
                foreach (var (glyph, palette) in layers)
                {
                    buffer.UInt16(glyph).UInt16(palette);
                }
            }

            return buffer.ToArray();
        }

        private static (byte B, byte G, byte R, byte A)[] Draw(GlyphTypeface typeface, ushort glyph, ISolidColorBrush foreground)
        {
            typeface.TryGetGlyphMetrics(glyph, out var metrics);
            var scale = 32.0 / typeface.Metrics.DesignEmHeight;
            var infos = new List<GlyphInfo> { new(glyph, 0, metrics.AdvanceWidth * scale) };

            using var run = new ManagedGlyphRunImpl(typeface, 32, infos, new Point(8, 44));

            var info = new SKImageInfo(64, 56, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var context = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));

            canvas.Clear(SKColors.White);

            Assert.True(MaskGlyphRunRenderer.TryDraw(context, run, foreground, TextRenderingMode.Antialias),
                "the mask path rejected a v0 color draw it should handle");

            var span = bitmap.GetPixelSpan();
            var result = new (byte, byte, byte, byte)[span.Length / 4];

            for (var i = 0; i < result.Length; i++)
            {
                result[i] = (span[i * 4], span[i * 4 + 1], span[i * 4 + 2], span[i * 4 + 3]);
            }

            return result;
        }

        private static int CountDominant((byte B, byte G, byte R, byte A)[] pixels, bool r)
            => pixels.Count(p => r ? p.R > 150 && p.B < 100 : p.B > 150 && p.R < 100);

        private static IDisposable CreateEnvironment()
        {
            var scope = AvaloniaLocator.EnterScope();

            AvaloniaLocator.CurrentMutable
                .Bind<IPlatformRenderInterface>().ToConstant(new PlatformRenderInterface());

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
