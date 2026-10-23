using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Imaging
{
    public class OptimalPaletteTests
    {
        private static PixelBuffer Rgba(params (byte R, byte G, byte B, byte A)[] pixels)
        {
            var bytes = new byte[pixels.Length * 4];

            for (var i = 0; i < pixels.Length; i++)
            {
                bytes[i * 4] = pixels[i].R;
                bytes[i * 4 + 1] = pixels[i].G;
                bytes[i * 4 + 2] = pixels[i].B;
                bytes[i * 4 + 3] = pixels[i].A;
            }

            return PixelBuffer.Create(bytes, new PixelSize(pixels.Length, 1), bytes.Length, PixelFormats.Rgba8888,
                AlphaFormat.Unpremul);
        }

        [Fact]
        public void Few_Colors_Should_Be_Kept_Exactly_Most_Frequent_First()
        {
            var palette = BitmapPalette.CreateOptimal(Rgba((10, 20, 30, 255), (200, 0, 0, 255), (200, 0, 0, 255)), 16);

            Assert.Equal(new[] { Color.FromRgb(200, 0, 0), Color.FromRgb(10, 20, 30) }, palette.Colors);
            Assert.Equal(WellKnownPaletteType.Optimal, palette.Type);
            Assert.False(palette.HasTransparency);
        }

        [Fact]
        public void Transparent_Pixels_Should_Not_Contribute_And_A_Transparent_Entry_Should_Come_Last()
        {
            var palette = BitmapPalette.CreateOptimal(Rgba((1, 2, 3, 0), (40, 50, 60, 255)), 4, addTransparentColor: true);

            Assert.Equal(new[] { Color.FromRgb(40, 50, 60), default }, palette.Colors);
            Assert.True(palette.HasTransparency);
        }

        [Fact]
        public void Many_Colors_Should_Be_Cut_At_The_Weighted_Median()
        {
            var ramp = new (byte, byte, byte, byte)[256];

            for (var i = 0; i < ramp.Length; i++)
                ramp[i] = ((byte)i, 0, 0, 255);

            var palette = BitmapPalette.CreateOptimal(Rgba(ramp), 4);

            // Quarters of the ramp, each represented by its mean.
            Assert.Equal(new[] { Color.FromRgb(32, 0, 0), Color.FromRgb(160, 0, 0), Color.FromRgb(96, 0, 0),
                Color.FromRgb(224, 0, 0) }, palette.Colors);
        }

        [Fact]
        public void Indexed_Sources_Should_Be_Read_Through_Their_Palette()
        {
            var source = new BitmapPalette(new[] { Colors.Red, Colors.Blue });
            var buffer = PixelBuffer.Create(new byte[] { 0b0100_0000 }, new PixelSize(2, 1), 1, PixelFormats.Indexed2,
                AlphaFormat.Unpremul, palette: source);

            var palette = BitmapPalette.CreateOptimal(buffer, 8);

            // Both colors appear once, so the lower RGB value comes first.
            Assert.Equal(new[] { Colors.Blue, Colors.Red }, palette.Colors);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(257)]
        public void The_Color_Count_Should_Be_From_1_To_256(int maxColors)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => BitmapPalette.CreateOptimal(Rgba((0, 0, 0, 255)), maxColors));
        }

        [Fact]
        public void A_Backend_Quantizer_Should_Be_Used_When_Available()
        {
            using var scope = AvaloniaLocator.EnterScope();

            var backend = new QuantizingBackend();

            ImagingBackend.Register(backend);

            var palette = BitmapPalette.CreateOptimal(Rgba((1, 2, 3, 255)), 7, true);

            Assert.Same(backend.Result, palette);
            Assert.Equal((7, true), backend.Arguments);
        }

        private sealed class QuantizingBackend : IImagingBackend, ISupportsPaletteGeneration
        {
            public BitmapPalette Result { get; } = new(new[] { Colors.Green });

            public (int, bool) Arguments { get; private set; }

            public string Name => "Quantizing";

            public IReadOnlyList<IBitmapCodecInfo> SupportedCodecs => Array.Empty<IBitmapCodecInfo>();

            public int IdentifyPrefixLength => 0;

            public BitmapPalette CreateOptimalPalette(ILockedFramebuffer source, BitmapPalette? sourcePalette,
                int maxColors, bool addTransparentColor)
            {
                Arguments = (maxColors, addTransparentColor);
                return Result;
            }

            public bool TryIdentify(Stream stream, out BitmapImageInfo info) => throw new NotSupportedException();

            public bool TryIdentify(ReadOnlySpan<byte> data, out BitmapImageInfo info) => throw new NotSupportedException();

            public IBitmapDecoder CreateDecoder(Stream stream, bool ownsStream, BitmapDecodeOptions? options = null)
                => throw new NotSupportedException();

            public IBitmapEncoderImpl CreateEncoder(Guid containerFormat) => throw new NotSupportedException();
        }
    }
}
