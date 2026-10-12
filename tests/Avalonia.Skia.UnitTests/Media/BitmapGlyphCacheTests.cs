using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Fonts.Tables.Bitmaps;
using Avalonia.UnitTests;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Decoded strike images are glyph cache content: charged to the typeface's budget, evicted
    /// by age with everything else it holds, released with the typeface.
    /// </summary>
    public class BitmapGlyphCacheTests
    {
        private const int ImageSize = 256;
        private const long ImageBytes = ImageSize * ImageSize * 4;

        [Fact]
        public void Decoded_Images_Are_Charged_To_The_Typefaces_Budget()
        {
            var budget = new GlyphCacheBudget(64L * 1024 * 1024);
            var typeface = CreateTypeface(budget, out var glyphs);
            var decoder = new CountingDecoder();

            using (budget.BeginFrame())
            {
                Assert.True(Place(typeface, glyphs[0], decoder));
                Assert.True(Place(typeface, glyphs[1], decoder));
            }

            Assert.Equal(2 * ImageBytes, budget.UsedBytes);

            typeface.Dispose();
        }

        [Fact]
        public void The_Budget_Evicts_The_Images_Drawn_Longest_Ago()
        {
            // 26 images of 256 KB against a 4 MB limit: the oldest give way, the recent stay.
            var budget = new GlyphCacheBudget(4L * 1024 * 1024);
            var typeface = CreateTypeface(budget, out var glyphs);
            var decoder = new CountingDecoder();

            foreach (var glyph in glyphs)
            {
                using (budget.BeginFrame())
                {
                    Assert.True(Place(typeface, glyph, decoder));
                }
            }

            Assert.Equal(glyphs.Length, decoder.Decodes);
            Assert.True(budget.UsedBytes <= budget.SoftLimitBytes, $"{budget.UsedBytes} bytes held");

            using (budget.BeginFrame())
            {
                for (var i = glyphs.Length - 12; i < glyphs.Length; i++)
                {
                    Assert.True(Place(typeface, glyphs[i], decoder));
                }

                Assert.Equal(glyphs.Length, decoder.Decodes);

                Assert.True(Place(typeface, glyphs[0], decoder));
                Assert.Equal(glyphs.Length + 1, decoder.Decodes);
            }

            typeface.Dispose();
        }

        [Fact]
        public void Disposing_The_Typeface_Releases_Its_Decoded_Images()
        {
            var budget = new GlyphCacheBudget(64L * 1024 * 1024);
            var typeface = CreateTypeface(budget, out var glyphs);
            var decoder = new CountingDecoder();

            using (budget.BeginFrame())
            {
                for (var i = 0; i < 4; i++)
                {
                    Assert.True(Place(typeface, glyphs[i], decoder));
                }
            }

            typeface.Dispose();

            Assert.Equal(0, budget.UsedBytes);
        }

        private static bool Place(GlyphTypeface typeface, ushort glyph, IBitmapGlyphDecoder decoder)
        {
            var source = typeface.BitmapSource!;

            return source.TryGetPlacement(source.SelectStrike(ImageSize), glyph, decoder, out _);
        }

        /// <summary>
        /// Inter with an sbix strike at <see cref="ImageSize"/> ppem holding a distinct
        /// <see cref="ImageSize"/>-pixel PNG for each of the glyphs of 'A' to 'Z'.
        /// </summary>
        private static GlyphTypeface CreateTypeface(GlyphCacheBudget budget, out ushort[] glyphs)
        {
            var font = SyntheticFont.FromBytes(File.ReadAllBytes(AssetPath("Inter-Regular.ttf")));
            var probe = font.CreateGlyphTypeface();
            var glyphCount = probe.GlyphCount;
            var images = new SortedDictionary<ushort, byte[]>();

            for (var c = 'A'; c <= 'Z'; c++)
            {
                images[probe.CharacterToGlyphMap[c]] = EncodePng((c - 'A') * 9);
            }

            probe.Dispose();

            var sbix = new BigEndianBuffer();
            sbix.UInt16(1).UInt16(0).UInt32(1).UInt32(12);
            sbix.UInt16(ImageSize).UInt16(72);

            var offset = 4 + (glyphCount + 1) * 4;

            for (ushort g = 0; g <= glyphCount; g++)
            {
                sbix.UInt32((uint)offset);

                if (images.TryGetValue(g, out var image))
                {
                    offset += 8 + image.Length;
                }
            }

            foreach (var image in images.Values)
            {
                sbix.Int16(0).Int16(0);
                foreach (var c in "png ") sbix.UInt8((byte)c);
                foreach (var b in image) sbix.UInt8(b);
            }

            var typeface = font.Replace("sbix", sbix.ToArray()).CreateGlyphTypeface();

            typeface.CacheBudget = budget;
            glyphs = new ushort[images.Count];
            images.Keys.CopyTo(glyphs, 0);

            return typeface;
        }

        private static byte[] EncodePng(int hue)
        {
            using var bitmap = new SKBitmap(new SKImageInfo(ImageSize, ImageSize, SKColorType.Bgra8888, SKAlphaType.Premul));

            bitmap.Erase(SKColor.FromHsl(hue, 80, 50));

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);

            return data.ToArray();
        }

        private static string AssetPath(string name)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && directory.Name != "tests")
            {
                directory = directory.Parent;
            }

            return Path.Combine(directory!.FullName, "Avalonia.RenderTests", "Assets", name);
        }

        private sealed class CountingDecoder : IBitmapGlyphDecoder
        {
            private readonly SkiaBitmapGlyphDecoder _inner = new();

            public int Decodes { get; private set; }

            public bool TryDecode(ReadOnlyMemory<byte> encoded, out DecodedGlyphBitmap bitmap)
            {
                Decodes++;
                return _inner.TryDecode(encoded, out bitmap);
            }
        }
    }
}
