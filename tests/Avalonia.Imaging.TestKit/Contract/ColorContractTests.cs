using System;
using System.IO;
using System.Linq;
using Avalonia.Imaging.TestKit.Fixtures;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Embedded ICC profiles survive an encode and decode through the public API, and
    /// backends with color management transform between profiles correctly.
    /// </summary>
    /// <remarks>
    /// The transform cases use two profiles with identical primaries that differ only in
    /// gamma, so the expected result is the exact power function, independent of any
    /// color management system.
    /// </remarks>
    public abstract class ColorContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        /// <summary>The skip reason when the backend has no color management.</summary>
        protected const string NoColorManagement = "The backend does not implement IColorTransformProvider.";

        /// <summary>The skip reason when the backend's PNG codec cannot round-trip a profile.</summary>
        protected const string NoPngColorProfile =
            "The backend's PNG codec does not declare decode, encode and ColorProfile.";

        private static readonly byte[] s_linear = IccProfileFixture.CreateRgb(1.0);
        private static readonly byte[] s_gamma2 = IccProfileFixture.CreateRgb(2.0);

        [Fact]
        public void A_Frame_Profile_Should_Round_Trip_Through_Png()
        {
            RequirePngColorProfile();

            using var scope = LocatorScope.With(Backend);

            var encoder = new PngBitmapEncoder();

            encoder.Frames.Add(new BitmapEncoderFrame
            {
                Pixels = PixelBuffer.Create(new byte[16], new PixelSize(2, 2), 8, PixelFormats.Bgra8888,
                    AlphaFormat.Unpremul),
                ColorProfile = s_gamma2,
            });

            using var stream = new MemoryStream();

            encoder.Save(stream);
            stream.Position = 0;

            using var decoder = BitmapDecoder.Create(stream);
            using var frame = decoder.ReadNextFrame()!;

            Assert.True(frame.TryGetColorProfile(out var profile));
            Assert.Equal(s_gamma2, profile.ToArray());
        }

        [Fact]
        public void Without_Color_Management_Create_Should_Throw()
        {
            Assert.SkipWhen(Backend is IColorTransformProvider, "The backend implements IColorTransformProvider.");

            using var scope = LocatorScope.With(Backend);

            Assert.False(BitmapColorTransform.IsSupported);
            Assert.Throws<NotSupportedException>(() => BitmapColorTransform.Create(s_linear, s_gamma2));
        }

        [Fact]
        public void TransformColors_Should_Apply_The_Destination_Curve()
        {
            RequireColorManagement();

            using var scope = LocatorScope.With(Backend);
            using var transform = BitmapColorTransform.Create(s_linear, s_gamma2);

            var source = new[] { 0.25f, 0.5f, 1f, 0f, 0.09f, 0.64f };
            var destination = new float[6];

            transform.TransformColors(source, destination);

            for (var i = 0; i < source.Length; i++)
                Assert.True(Math.Abs(Math.Sqrt(source[i]) - destination[i]) < 0.005,
                    $"Channel {i}: expected {Math.Sqrt(source[i]):F4}, got {destination[i]:F4}.");
        }

        [Fact]
        public void Apply_Should_Transform_Pixels_And_Keep_Alpha()
        {
            RequireColorManagement();

            using var scope = LocatorScope.With(Backend);
            using var transform = BitmapColorTransform.Create(s_gamma2, s_linear);

            var source = PixelBuffer.Create(new byte[] { 0, 64, 128, 77, 255, 255, 255, 255 }, new PixelSize(2, 1), 8,
                PixelFormats.Bgra8888, AlphaFormat.Unpremul);
            var result = transform.Apply(source, PixelFormats.Bgra8888).Pixels.ToArray();

            static int Squared(int value) => (int)Math.Round(value / 255.0 * (value / 255.0) * 255);

            var expected = new[] { 0, Squared(64), Squared(128), 77, 255, 255, 255, 255 };

            for (var i = 0; i < expected.Length; i++)
                Assert.True(Math.Abs(expected[i] - result[i]) <= 1, $"Byte {i}: expected {expected[i]}, got {result[i]}.");
        }

        [Fact]
        public void The_Same_Profile_Should_Leave_Pixels_Unchanged()
        {
            RequireColorManagement();

            using var scope = LocatorScope.With(Backend);
            using var transform = BitmapColorTransform.Create(s_gamma2, s_gamma2);

            var pixels = Enumerable.Range(0, 64).Select(i => (byte)(i * 4)).ToArray();
            var source = PixelBuffer.Create(pixels, new PixelSize(16, 1), 64, PixelFormats.Rgba8888,
                AlphaFormat.Unpremul);
            var result = transform.Apply(source, PixelFormats.Rgba8888).Pixels.ToArray();

            for (var i = 0; i < pixels.Length; i++)
                Assert.True(Math.Abs(pixels[i] - result[i]) <= 1, $"Byte {i}: expected {pixels[i]}, got {result[i]}.");
        }

        private void RequireColorManagement() =>
            Assert.SkipUnless(Backend is IColorTransformProvider, NoColorManagement);

        private void RequirePngColorProfile()
        {
            const BitmapCodecCapabilities required = BitmapCodecCapabilities.Decode |
                BitmapCodecCapabilities.Encode | BitmapCodecCapabilities.ColorProfile;

            var png = Backend.SupportedCodecs.FirstOrDefault(codec => codec.FormatName == FixtureImages.PngFormatName);

            Assert.SkipWhen(png is null || (png.Capabilities & required) != required, NoPngColorProfile);
        }
    }
}
