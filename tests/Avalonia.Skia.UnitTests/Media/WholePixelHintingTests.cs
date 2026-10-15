using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
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
    /// A font that sets head.flags bit 3 is hinted and drawn at the rounded ppem whenever its
    /// own programs grid-fit the text, so every fractional size of one rounded ppem draws the
    /// same glyph image from one cached mask. Noto Mono sets the bit and carries a full
    /// program set; a copy with the bit cleared hints at the exact fractional size.
    /// </summary>
    public class WholePixelHintingTests
    {
        private const double FirstSize = 15.5;

        [Theory]
        [InlineData(TextHintingMode.Light, true, 1)]
        [InlineData(TextHintingMode.Strong, true, 1)]
        [InlineData(TextHintingMode.None, true, 8)]
        [InlineData(TextHintingMode.Light, false, 8)]
        public void The_Eighths_Of_One_Rounded_Ppem_Share_The_Hinted_Glyph_Mask(TextHintingMode hinting,
            bool forcesIntegerPpem, int expectedMasks)
        {
            using var scope = CreateEnvironment(forcesIntegerPpem, out var typeface);

            Assert.Equal(forcesIntegerPpem, typeface.HintsAtWholePixelsPerEm);

            // 15.5 to 16.375 px: all eight scale buckets round to 16 px.
            for (var step = 0; step < 8; step++)
            {
                DrawGlyph(typeface, FirstSize + step / 8.0, hinting);
            }

            Assert.Equal(expectedMasks, typeface.MaskCache.Count);
        }

        [Fact]
        public void A_Fractional_Size_Draws_The_Rounded_Size_Glyph_Image_At_Its_Own_Position()
        {
            using var scope = CreateEnvironment(forcesIntegerPpem: true, out var typeface);

            var atFraction = DrawGlyph(typeface, 15.625, TextHintingMode.Light);
            var atWhole = DrawGlyph(typeface, 16, TextHintingMode.Light);

            Assert.Equal(atWhole, atFraction);
        }

        /// <summary>Draws one 'a' with its pen on a whole pixel and returns the canvas pixels.</summary>
        private static uint[] DrawGlyph(GlyphTypeface typeface, double emSize, TextHintingMode hinting)
        {
            var glyph = typeface.CharacterToGlyphMap['a'];

            typeface.TryGetGlyphMetrics(glyph, out var metrics);

            var advance = metrics.AdvanceWidth * emSize / typeface.Metrics.DesignEmHeight;

            using var run = new ManagedGlyphRunImpl(typeface, emSize,
                new List<GlyphInfo> { new(glyph, 0, advance) }, new Point(8, 32));

            var info = new SKImageInfo(48, 48, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var context = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));

            canvas.Clear(SKColors.White);

            Assert.True(MaskGlyphRunRenderer.TryDraw(context, run, Brushes.Black, TextRenderingMode.Antialias,
                hinting));

            canvas.Flush();

            var pixels = new uint[info.Width * info.Height];

            for (var y = 0; y < info.Height; y++)
            {
                for (var x = 0; x < info.Width; x++)
                {
                    pixels[y * info.Width + x] = (uint)bitmap.GetPixel(x, y);
                }
            }

            return pixels;
        }

        internal static IDisposable CreateEnvironment(bool forcesIntegerPpem, out GlyphTypeface typeface)
        {
            var scope = TextTestApplication.Enter();

            AvaloniaLocator.CurrentMutable
                .Bind<IPlatformRenderInterface>().ToConstant(new PlatformRenderInterface());

            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && directory.Name != "tests")
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);

            var bytes = File.ReadAllBytes(Path.Combine(directory!.FullName, "Avalonia.RenderTests", "Assets",
                "NotoMono-Regular.ttf"));

            if (!forcesIntegerPpem)
            {
                ClearForcePpemToInteger(bytes);
            }

            typeface = TestGlyphTypefaces.FromBytes(bytes);

            return scope;
        }

        private static void ClearForcePpemToInteger(byte[] font)
        {
            var tableCount = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));

            for (var i = 0; i < tableCount; i++)
            {
                var record = 12 + i * 16;

                if (font[record] == 'h' && font[record + 1] == 'e' && font[record + 2] == 'a' &&
                    font[record + 3] == 'd')
                {
                    var flags = font.AsSpan((int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 8)) + 16, 2);

                    Assert.NotEqual(0, BinaryPrimitives.ReadUInt16BigEndian(flags) & 8);
                    BinaryPrimitives.WriteUInt16BigEndian(flags, (ushort)(BinaryPrimitives.ReadUInt16BigEndian(flags) & ~8));
                    return;
                }
            }

            Assert.Fail("the font has no head table");
        }
    }
}
