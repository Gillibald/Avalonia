using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Imaging.TestKit.Fixtures;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Metadata a backend writes and reads back through the public encode and decode API:
    /// the frame resolution, an EXIF field set through a query path, and a frame ICC profile
    /// in JPEG. Each case runs for the formats the backend declares it for.
    /// </summary>
    public abstract class MetadataWriteContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        // Different on the two axes, so swapped axes fail; whole dots per inch survive every
        // container's resolution field, PNG's dots per metre within the tolerance.
        private static readonly Vector s_dpi = new(144, 72);

        private const double DpiTolerance = 0.05;

        private const string MakeQuery = "/app1/ifd/{ushort=271}";

        private static readonly byte[] s_profile = IccProfileFixture.CreateRgb(2.0);

        public static IEnumerable<object[]> ResolutionFormats() => new[]
        {
            new object[] { "PNG" },
            new object[] { "JPEG" },
            new object[] { "BMP" },
            new object[] { "TIFF" },
        };

        [Theory]
        [MemberData(nameof(ResolutionFormats))]
        public void Encoded_Resolution_Should_Read_Back(string formatName)
        {
            if (TryGetFormat(formatName) is not { Decode: true, Encode: true, Dpi: true })
            {
                Assert.Skip($"The manifest does not declare {formatName} resolution round trips.");
                return;
            }

            using var scope = LocatorScope.With(Backend);

            var encoder = CreateEncoder(formatName);

            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = CreatePixels(s_dpi) });

            using var decoder = RoundTrip(encoder);
            using var frame = decoder.ReadNextFrame()!;

            Assert.True(Math.Abs(frame.Dpi.X - s_dpi.X) <= DpiTolerance && Math.Abs(frame.Dpi.Y - s_dpi.Y) <= DpiTolerance,
                $"{formatName}: wrote {s_dpi.X}x{s_dpi.Y} DPI, read {frame.Dpi.X}x{frame.Dpi.Y}.");
        }

        [Fact]
        public void Exif_Written_To_Jpeg_Should_Read_Back()
        {
            RequireJpeg(BitmapCodecCapabilities.Metadata, "Metadata");

            using var scope = LocatorScope.With(Backend);

            var metadata = new BitmapMetadata(BitmapContainerFormats.Jpeg);

            metadata.SetQuery(MakeQuery, "TestKit Make");

            var encoder = new JpegBitmapEncoder { Metadata = metadata };

            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = CreatePixels(null) });

            using var decoder = RoundTrip(encoder);
            using var frame = decoder.ReadNextFrame()!;

            Assert.True(frame.TryGetMetadata(out var read), "The decoded JPEG frame exposes no metadata.");
            Assert.Equal("TestKit Make", read.GetQuery(MakeQuery));
        }

        [Fact]
        public void A_Frame_Profile_Should_Round_Trip_Through_Jpeg()
        {
            RequireJpeg(BitmapCodecCapabilities.ColorProfile, "ColorProfile");

            using var scope = LocatorScope.With(Backend);

            var encoder = new JpegBitmapEncoder();

            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = CreatePixels(null), ColorProfile = s_profile });

            using var decoder = RoundTrip(encoder);
            using var frame = decoder.ReadNextFrame()!;

            Assert.True(frame.TryGetColorProfile(out var profile), "The decoded JPEG frame has no colour profile.");
            Assert.Equal(s_profile, profile.ToArray());
        }

        private void RequireJpeg(BitmapCodecCapabilities capability, string name)
        {
            var required = BitmapCodecCapabilities.Decode | BitmapCodecCapabilities.Encode | capability;
            var jpeg = Backend.SupportedCodecs.FirstOrDefault(codec => codec.FormatName == "JPEG");

            Assert.SkipWhen(jpeg is null || (jpeg.Capabilities & required) != required,
                $"The backend's JPEG codec does not declare decode, encode and {name}.");
        }

        private static BitmapEncoder CreateEncoder(string formatName) => formatName switch
        {
            "PNG" => new PngBitmapEncoder(),
            "JPEG" => new JpegBitmapEncoder(),
            "BMP" => new BmpBitmapEncoder(),
            "TIFF" => new TiffBitmapEncoder(),
            _ => throw new ArgumentOutOfRangeException(nameof(formatName)),
        };

        private static PixelBuffer CreatePixels(Vector? dpi)
        {
            var bgra = new byte[4 * 4 * 4];

            for (var i = 0; i < bgra.Length; i += 4)
            {
                bgra[i] = 40;
                bgra[i + 1] = 120;
                bgra[i + 2] = 200;
                bgra[i + 3] = 255;
            }

            return PixelBuffer.Create(bgra, new PixelSize(4, 4), 16, PixelFormats.Bgra8888, AlphaFormat.Opaque, dpi);
        }

        private static BitmapDecoder RoundTrip(BitmapEncoder encoder)
        {
            var stream = new MemoryStream();

            encoder.Save(stream);
            stream.Position = 0;

            return BitmapDecoder.Create(stream, ownsStream: true);
        }
    }
}
