using System;
using System.Collections.Generic;
using Avalonia.Imaging.TestKit.Fixtures;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Capability catalog: everything the manifest declares appears in the backend's
    /// codec catalog, declared decoding is observable, and codec descriptors are
    /// internally consistent.
    /// </summary>
    public abstract class CapabilityContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        [Fact]
        public void EveryDeclaredDecodeCapability_ObservableWhereFixtureExists()
        {
            var tested = 0;

            foreach (var fixture in new[] { FixtureImages.Rgb4x4Png, FixtureImages.Rgb4x4Bmp })
            {
                var format = TryGetFormat(fixture.ExpectedFormatName);

                if (format is not { Decode: true })
                    continue;

                tested++;

                var codec = FindCodec(fixture.ExpectedFormatName);

                Assert.True(codec is not null,
                    $"'{fixture.ExpectedFormatName}' is declared in the manifest but missing from SupportedCodecs.");

                AssertCapability(codec!, BitmapCodecCapabilities.Decode);

                if (format.Encode)
                    AssertCapability(codec!, BitmapCodecCapabilities.Encode);
                if (format.Metadata)
                    AssertCapability(codec!, BitmapCodecCapabilities.Metadata);
                if (format.Animation)
                    AssertCapability(codec!, BitmapCodecCapabilities.Animation);
                if (format.Palette)
                    AssertCapability(codec!, BitmapCodecCapabilities.Palette);
                if (format.SeekableFrames)
                    AssertCapability(codec!, BitmapCodecCapabilities.MultiFrame);
                if (format.MultiFrameEncode)
                    AssertCapability(codec!, BitmapCodecCapabilities.MultiFrameEncode);
                if (format.FusedParts != FusedDecodeParts.None)
                    AssertCapability(codec!, BitmapCodecCapabilities.FusedDecode);

                // Decoding must be observable, not just declared.
                using var decoder = CreateDecoder(fixture);
                using var frame = ReadFirstFrame(decoder);
                using var view = frame.Lock();

                Assert.NotEqual(IntPtr.Zero, view.Address);
            }

            Assert.SkipWhen(tested == 0, NoDecodableReferenceFixtures);
        }

        [Fact]
        public void CodecInfo_FormatNamesMimeExtensions_Consistent()
        {
            var codecs = Backend.SupportedCodecs;

            Assert.NotEmpty(codecs);

            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (var codec in codecs)
            {
                Assert.False(string.IsNullOrWhiteSpace(codec.FormatName), "Every codec needs a format name.");
                Assert.True(names.Add(codec.FormatName),
                    $"The format name '{codec.FormatName}' appears twice in SupportedCodecs.");
                Assert.NotEqual(Guid.Empty, codec.ContainerFormat);
                Assert.NotEqual(BitmapCodecCapabilities.None,
                    codec.Capabilities & (BitmapCodecCapabilities.Decode | BitmapCodecCapabilities.Encode));

                Assert.NotEmpty(codec.MimeTypes);

                foreach (var mimeType in codec.MimeTypes)
                {
                    Assert.True(!string.IsNullOrWhiteSpace(mimeType) && mimeType.Contains('/'),
                        $"'{codec.FormatName}' has a malformed MIME type '{mimeType}'.");
                }

                Assert.NotEmpty(codec.FileExtensions);

                foreach (var extension in codec.FileExtensions)
                {
                    Assert.True(!string.IsNullOrWhiteSpace(extension) && !extension.StartsWith('.'),
                        $"'{codec.FormatName}' has a malformed file extension '{extension}' (no leading dot expected).");
                }
            }

            foreach (var format in Manifest.Formats)
            {
                Assert.True(names.Contains(format.FormatName),
                    $"The manifest format '{format.FormatName}' is not in SupportedCodecs.");
            }
        }

        private IBitmapCodecInfo? FindCodec(string formatName)
        {
            foreach (var codec in Backend.SupportedCodecs)
            {
                if (string.Equals(codec.FormatName, formatName, StringComparison.Ordinal))
                    return codec;
            }

            return null;
        }

        private static void AssertCapability(IBitmapCodecInfo codec, BitmapCodecCapabilities capability)
        {
            Assert.True((codec.Capabilities & capability) != 0, $"'{codec.FormatName}' must declare {capability}.");
        }
    }
}
