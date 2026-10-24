using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Imaging.TestKit.Fixtures;
using Avalonia.Imaging.TestKit.Instrumentation;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// <see cref="BitmapDecodeOptions.PreservePixelFormat"/> and <see cref="BitmapDecodeOptions.FormatProfile"/>
    /// through the public decode surface. By default a frame converts to the render backend's
    /// default format (here the kit's fake render interface: premultiplied
    /// <see cref="PixelFormats.Bgra8888"/>); preserving keeps the stored layout; the WIC profile
    /// is a codec capability.
    /// </summary>
    public abstract class FormatProfileContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        private const string NoPngDecode = "The manifest does not declare PNG decoding.";
        private const string NotPreserving = "The backend's PNG codec does not declare PreservesStoredFormat.";
        private const string NoWicProfile = "The backend's PNG codec does not declare WicFormatProfile.";
        private const string HasWicProfile = "The backend's PNG codec declares WicFormatProfile.";

        // Opaque 8-bit RGB: (10, 20, 30) and (200, 100, 50).
        private static readonly byte[] s_rgb24 = PngFixtureWriter.WriteRaw(2, 1, 2, 8, 6, (row, _) =>
        {
            row[1] = 10; row[2] = 20; row[3] = 30;
            row[4] = 200; row[5] = 100; row[6] = 50;
        });

        // Gray 0x1234 and 0xABCD: converting keeps the high byte.
        private static readonly byte[] s_gray16 = PngFixtureWriter.WriteRaw(2, 1, 0, 16, 4, (row, _) =>
        {
            row[1] = 0x12; row[2] = 0x34; row[3] = 0xAB; row[4] = 0xCD;
        });

        // Straight alpha (100, 50, 200, 128) and a transparent pixel.
        private static readonly byte[] s_rgba32 = PngFixtureWriter.WriteRaw(2, 1, 6, 8, 8, (row, _) =>
        {
            row[1] = 100; row[2] = 50; row[3] = 200; row[4] = 128;
        });

        private static readonly byte[] s_indexed4 = PngFixtureWriter.WriteRaw(3, 1, 3, 4, 2, (row, _) =>
        {
            row[1] = 0x12; row[2] = 0x00;
        }, palette: new byte[] { 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255 });

        /// <summary>
        /// Fixtures with the premultiplied BGRA bytes their first pixels convert to, and
        /// whether the image has alpha.
        /// </summary>
        public static IEnumerable<object[]> Converted()
        {
            yield return new object[] { "Rgb24", s_rgb24, new byte[] { 30, 20, 10, 255, 50, 100, 200, 255 }, false };
            yield return new object[] { "Gray16", s_gray16, new byte[] { 0x12, 0x12, 0x12, 255, 0xAB, 0xAB, 0xAB, 255 }, false };
            yield return new object[] { "Rgba32", s_rgba32, new byte[] { 100, 25, 50, 128, 0, 0, 0, 0 }, true };
            yield return new object[] { "Indexed4", s_indexed4, new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 }, false };
        }

        [Fact]
        public void Options_DefaultToConvertingForTheRenderBackend()
        {
            var options = new BitmapDecodeOptions();

            Assert.False(options.PreservePixelFormat);
            Assert.Equal(BitmapFormatProfile.RenderBackend, options.FormatProfile);
        }

        [Theory]
        [MemberData(nameof(Converted))]
        public void Decode_ByDefault_ConvertsToTheRenderBackendFormat(string name, byte[] png, byte[] expected,
            bool hasAlpha)
        {
            RequirePngDecode();

            using var scope = BindPlatform();
            using var decoder = BitmapDecoder.Create(new MemoryStream(png));
            using var frame = decoder.ReadNextFrame()!;

            Assert.True(frame.Format == PixelFormats.Bgra8888 && IsRenderAlpha(frame.AlphaFormat, hasAlpha),
                $"{name}: decoded to {frame.Format} {frame.AlphaFormat}, expected the render default Bgra8888 Premul.");
            AssertFirstPixels(frame, expected, name);
        }

        [Fact]
        public async Task CreateAsync_ByDefault_ConvertsToTheRenderBackendFormat()
        {
            RequirePngDecode();

            using var scope = BindPlatform();
            using var decoder = await BitmapDecoder.CreateAsync(new MemoryStream(s_gray16));
            using var frame = decoder.ReadNextFrame()!;

            Assert.Equal(PixelFormats.Bgra8888, frame.Format);
            Assert.True(IsRenderAlpha(frame.AlphaFormat, hasAlpha: false), frame.AlphaFormat.ToString());
        }

        [Fact]
        public void BitmapDecode_ByDefault_InstallsTheRenderBackendFormat()
        {
            RequirePngDecode();

            using var scope = BindPlatform();
            using var bitmap = Bitmap.Decode(new MemoryStream(s_gray16));

            Assert.Equal(PixelFormats.Bgra8888, Fixture.RenderInstall.Format);
            Assert.Equal(PixelFormats.Bgra8888, bitmap.Format);
        }

        [Theory]
        [MemberData(nameof(Converted))]
        public void Decode_PreservePixelFormat_KeepsTheStoredFormat(string name, byte[] png, byte[] expected,
            bool hasAlpha)
        {
            _ = (expected, hasAlpha);
            RequirePreservingPngCodec();

            using var scope = BindPlatform();
            var info = BitmapDecoder.Identify(new MemoryStream(png));
            using var decoder = BitmapDecoder.Create(new MemoryStream(png),
                new BitmapDecodeOptions { PreservePixelFormat = true });
            using var frame = decoder.ReadNextFrame()!;

            Assert.True(info.PixelFormat == frame.Format,
                $"{name}: preserved decode gave {frame.Format}, identify reported {info.PixelFormat}.");
        }

        [Fact]
        public void Identify_ReportsTheStoredFormatWhateverTheDecodeConvertsTo()
        {
            RequirePreservingPngCodec();

            using var scope = BindPlatform();
            var info = BitmapDecoder.Identify(new MemoryStream(s_gray16));

            Assert.Equal(PixelFormats.Gray16, info.PixelFormat);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Decode_TargetFormat_OverridesThePreserveChoice(bool preserve)
        {
            RequirePngDecode();

            using var scope = BindPlatform();
            using var decoder = BitmapDecoder.Create(new MemoryStream(s_rgb24), new BitmapDecodeOptions
            {
                PreservePixelFormat = preserve,
                TargetFormat = PixelFormats.Rgb565,
            });
            using var frame = decoder.ReadNextFrame()!;

            Assert.Equal(PixelFormats.Rgb565, frame.Format);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Decode_WicProfile_WithoutTheCapability_FailsNamingTheBackend(bool preserve)
        {
            RequirePngDecode();
            Assert.SkipWhen(PngDeclares(BitmapCodecCapabilities.WicFormatProfile), HasWicProfile);

            using var scope = BindPlatform();
            var error = Assert.Throws<NotSupportedException>(() =>
            {
                using var decoder = BitmapDecoder.Create(new MemoryStream(s_rgb24), new BitmapDecodeOptions
                {
                    PreservePixelFormat = preserve,
                    FormatProfile = BitmapFormatProfile.Wic,
                });
                using var frame = decoder.ReadNextFrame();
            });

            Assert.Contains(Backend.Name, error.Message);
        }

        [Fact]
        public void Decode_WicProfile_Preserved_UsesTheWicNames()
        {
            RequireWicProfile();

            var options = new BitmapDecodeOptions { PreservePixelFormat = true, FormatProfile = BitmapFormatProfile.Wic };

            Assert.Equal(PixelFormats.Bgr24, DecodeFormat(s_rgb24, options));
            Assert.Equal(PixelFormats.Gray16, DecodeFormat(s_gray16, options));
            Assert.Equal(PixelFormats.Indexed4, DecodeFormat(s_indexed4, options));
        }

        [Fact]
        public void Decode_WicProfile_Converted_UsesWicsDefaultConversion()
        {
            RequireWicProfile();

            var options = new BitmapDecodeOptions { FormatProfile = BitmapFormatProfile.Wic };

            Assert.Equal(PixelFormats.Bgr32, DecodeFormat(s_rgb24, options));
            Assert.Equal(PixelFormats.Bgr32, DecodeFormat(s_gray16, options));
            Assert.Equal(PixelFormats.Indexed4, DecodeFormat(s_indexed4, options));

            using var scope = BindPlatform();
            using var decoder = BitmapDecoder.Create(new MemoryStream(s_rgba32), options);
            using var frame = decoder.ReadNextFrame()!;

            Assert.Equal(PixelFormats.Bgra8888, frame.Format);
            Assert.Equal(AlphaFormat.Premul, frame.AlphaFormat);
        }

        private PixelFormat DecodeFormat(byte[] png, BitmapDecodeOptions options)
        {
            using var scope = BindPlatform();
            using var decoder = BitmapDecoder.Create(new MemoryStream(png), options);
            using var frame = decoder.ReadNextFrame()!;

            return frame.Format;
        }

        // Opaque pixels are the same bytes premultiplied or not, so an opaque image may
        // report Opaque instead of the render backend's Premul.
        private static bool IsRenderAlpha(AlphaFormat alpha, bool hasAlpha) =>
            alpha == AlphaFormat.Premul || (!hasAlpha && alpha == AlphaFormat.Opaque);

        // Premultiplication may round either way, so each sample may be off by one.
        private static void AssertFirstPixels(BitmapFrame frame, byte[] expected, string name)
        {
            using var view = frame.Lock();
            var actual = new byte[expected.Length];

            Marshal.Copy(view.Address, actual, 0, actual.Length);

            for (var i = 0; i < expected.Length; i++)
                Assert.True(Math.Abs(actual[i] - expected[i]) <= 1,
                    $"{name}: byte {i} is {actual[i]}, expected {expected[i]} ({string.Join(", ", actual)}).");
        }

        private bool PngDeclares(BitmapCodecCapabilities capability) =>
            Backend.SupportedCodecs.FirstOrDefault(codec => codec.FormatName == FixtureImages.PngFormatName) is { } png
            && (png.Capabilities & capability) != 0;

        private void RequirePngDecode() =>
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true }, NoPngDecode);

        private void RequirePreservingPngCodec()
        {
            RequirePngDecode();
            Assert.SkipWhen(!PngDeclares(BitmapCodecCapabilities.PreservesStoredFormat), NotPreserving);
        }

        private void RequireWicProfile()
        {
            RequirePngDecode();
            Assert.SkipWhen(!PngDeclares(BitmapCodecCapabilities.WicFormatProfile), NoWicProfile);
        }

        private IDisposable BindPlatform() =>
            LocatorScope.With(Backend, new FakeRenderInterface(Fixture.RenderInstall));
    }
}
