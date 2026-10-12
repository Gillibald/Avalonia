using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Embedded glyph images decode to the premultiplied BGRA texels Skia's bitmap decode gives
    /// for the same payload.
    /// </summary>
    public class BitmapGlyphDecoderTests
    {
        public static IEnumerable<object[]> Payloads()
        {
            yield return new object[] { "png alpha gradient", Encode(CreateGradient(37, 29), SKEncodedImageFormat.Png) };
            yield return new object[] { "png opaque checker", Encode(CreateChecker(64, 64), SKEncodedImageFormat.Png) };
            yield return new object[] { "png gray alpha", Encode(CreateGray(20, 33), SKEncodedImageFormat.Png) };
            yield return new object[] { "jpeg", Encode(CreateChecker(48, 40), SKEncodedImageFormat.Jpeg) };
        }

        [Theory]
        [MemberData(nameof(Payloads))]
        public void Decoded_Texels_Equal_Skias_Bitmap_Decode(string name, byte[] payload)
        {
            AssertDecodesLikeSkia(name, payload);
        }

        [Fact]
        public void Apple_Color_Emoji_Strikes_Decode_Like_Skias_Bitmap_Decode()
        {
            const string path = "/System/Library/Fonts/Apple Color Emoji.ttc";

            Assert.SkipUnless(File.Exists(path), "Apple Color Emoji is not installed.");
            Assert.True(SfntFace.TryLoad(path, 0, out var face));

            var typeface = new GlyphTypeface(face);
            var source = typeface.BitmapSource!;
            var recorder = new RecordingDecoder();
            var decoded = 0;

            foreach (var ppem in new[] { 20, 40, 64, 160 })
            {
                var strike = source.SelectStrike(ppem);

                for (var codepoint = 0x1F600; codepoint < 0x1F620; codepoint++)
                {
                    var glyph = typeface.CharacterToGlyphMap[codepoint];

                    if (glyph != 0 && source.TryGetPlacement(strike, glyph, recorder, out _))
                    {
                        decoded++;
                    }
                }
            }

            typeface.Dispose();

            Assert.True(decoded > 40, $"only {decoded} images");

            foreach (var payload in recorder.Payloads)
            {
                AssertDecodesLikeSkia("Apple Color Emoji", payload);
            }
        }

        private static void AssertDecodesLikeSkia(string name, byte[] payload)
        {
            Assert.True(new SkiaBitmapGlyphDecoder().TryDecode(payload, out var actual), name);

            var expected = DecodeWithSkiaBitmap(payload);

            Assert.Equal(expected.Width, actual.Width);
            Assert.Equal(expected.Height, actual.Height);
            Assert.True(expected.Bgra.AsSpan().SequenceEqual(actual.Bgra), $"{name}: texels differ");
        }

        /// <summary>Skia's bitmap decode read back as premultiplied BGRA.</summary>
        private static unsafe DecodedGlyphBitmap DecodeWithSkiaBitmap(byte[] payload)
        {
            using var data = SKData.CreateCopy(payload);
            using var bitmap = SKBitmap.Decode(data);

            Assert.NotNull(bitmap);

            var info = new SKImageInfo(bitmap!.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var pixels = new byte[info.BytesSize];

            using var pixmap = bitmap.PeekPixels();

            fixed (byte* target = pixels)
            {
                Assert.True(pixmap.ReadPixels(info, (IntPtr)target, info.RowBytes, 0, 0));
            }

            return new DecodedGlyphBitmap(pixels, bitmap.Width, bitmap.Height);
        }

        private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format)
        {
            using (bitmap)
            using (var image = SKImage.FromBitmap(bitmap))
            using (var data = image.Encode(format, 90))
            {
                return data.ToArray();
            }
        }

        private static SKBitmap CreateGradient(int width, int height)
        {
            var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    bitmap.SetPixel(x, y, new SKColor((byte)(x * 7), (byte)(y * 9), (byte)(x * y), (byte)((x + y) * 255 / (width + height))));
                }
            }

            return bitmap;
        }

        private static SKBitmap CreateChecker(int width, int height)
        {
            var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    bitmap.SetPixel(x, y, ((x / 4 + y / 4) & 1) == 0 ? new SKColor(220, 30, 60) : new SKColor(20, 200, 90));
                }
            }

            return bitmap;
        }

        private static SKBitmap CreateGray(int width, int height)
        {
            var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var level = (byte)(x * 12);

                    bitmap.SetPixel(x, y, new SKColor(level, level, level, (byte)(y * 7)));
                }
            }

            return bitmap;
        }

        private sealed class RecordingDecoder : IBitmapGlyphDecoder
        {
            private readonly SkiaBitmapGlyphDecoder _inner = new();

            public List<byte[]> Payloads { get; } = new();

            public bool TryDecode(ReadOnlyMemory<byte> encoded, out DecodedGlyphBitmap bitmap)
            {
                Payloads.Add(encoded.ToArray());
                return _inner.TryDecode(encoded, out bitmap);
            }
        }
    }
}
