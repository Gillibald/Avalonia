using System;
using System.Collections.Generic;
using Avalonia.Imaging.TestKit.Fixtures;
using Avalonia.Imaging.TestKit.Instrumentation;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// The shared plumbing of the contract test classes: a fresh fixture and backend
    /// per test, the generated reference fixtures gated by the manifest, and
    /// pixel-level assertions against fixture references.
    /// </summary>
    /// <typeparam name="TFixture">The concrete backend fixture under test.</typeparam>
    public abstract class ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        /// <summary>The skip reason used when the manifest gates out every generated fixture.</summary>
        protected const string NoDecodableReferenceFixtures =
            "The manifest declares no decodable format with a generated fixture.";

        private IImagingBackend? _backend;

        protected ImagingContractTests()
        {
            Fixture = new TFixture();
        }

        /// <summary>Gets the backend fixture; a fresh instance per test.</summary>
        protected TFixture Fixture { get; }

        /// <summary>Gets the backend under test, created on first use.</summary>
        protected IImagingBackend Backend => _backend ??= Fixture.CreateBackend();

        /// <summary>Gets the capability declarations of the backend under test.</summary>
        protected BackendManifest Manifest => Fixture.Manifest;

        /// <summary>Gets the counting allocator the backend under test rents from.</summary>
        protected CountingAllocator Allocator => Fixture.Allocator;

        /// <summary>Gets the generated fixtures that carry reference pixels, in a stable order.</summary>
        protected static IReadOnlyList<ImageFixture> ReferenceFixtures { get; } = new[]
        {
            FixtureImages.Rgb4x4Png,
            FixtureImages.AlphaGradientPng,
            FixtureImages.Rgb4x4Bmp,
            FixtureImages.Rgb565Bmp,
        };

        /// <summary>Returns the manifest declaration for a format, or null.</summary>
        protected FormatManifest? TryGetFormat(string formatName) => Manifest.TryGetFormat(formatName);

        /// <summary>Enumerates the reference fixtures whose format the manifest declares decodable.</summary>
        protected IEnumerable<ImageFixture> DecodableReferenceFixtures()
        {
            foreach (var fixture in ReferenceFixtures)
            {
                if (TryGetFormat(fixture.ExpectedFormatName) is { Decode: true })
                    yield return fixture;
            }
        }

        /// <summary>Returns any decodable reference fixture, skipping the test when there is none.</summary>
        protected ImageFixture RequireAnyDecodableReferenceFixture()
        {
            foreach (var fixture in DecodableReferenceFixtures())
                return fixture;

            Assert.Skip(NoDecodableReferenceFixtures);
            return null!; // unreachable
        }

        /// <summary>
        /// The rent size from which an allocation counts as frame-sized for a fixture:
        /// header or I/O scratch stays below it, any pixel buffer is at or above it.
        /// </summary>
        protected static long FrameSizedThreshold(ImageFixture fixture) =>
            Math.Max(4096, (long)fixture.ExpectedSize.Width * fixture.ExpectedSize.Height);

        /// <summary>Creates a decoder over a fresh stream of the fixture, owning the stream.</summary>
        protected IBitmapDecoder CreateDecoder(ImageFixture fixture, BitmapDecodeOptions? options = null) =>
            Backend.CreateDecoder(fixture.OpenRead(), ownsStream: true, options);

        /// <summary>Reads the first frame, asserting there is one.</summary>
        protected static IBitmapFrameSource ReadFirstFrame(IBitmapDecoder decoder)
        {
            var frame = decoder.ReadNextFrame();

            Assert.True(frame is not null, "The decoder produced no frame.");

            return frame!;
        }

        /// <summary>Locks the frame and compares its pixels against the fixture reference.</summary>
        protected void AssertFrameMatchesReference(ImageFixture fixture, IBitmapFrameSource frame)
        {
            using var view = frame.Lock();

            Assert.Equal(fixture.ExpectedSize, view.Size);
            AssertRgbaMatchesReference(fixture, FramebufferPixels.ReadRgba(view), view.AlphaFormat);
        }

        /// <summary>
        /// Compares RGBA pixels against the fixture reference, premultiplying the
        /// reference when the produced alpha format is premultiplied and applying the
        /// manifest's per-format tolerance.
        /// </summary>
        protected void AssertRgbaMatchesReference(ImageFixture fixture, byte[] actualRgba, AlphaFormat alphaFormat)
        {
            var expected = fixture.ExpectedRgba.Span;

            Assert.True(expected.Length > 0, $"{fixture.Name} has no reference pixels.");
            Assert.Equal(expected.Length, actualRgba.Length);

            var tolerance = TryGetFormat(fixture.ExpectedFormatName)?.PixelTolerance ?? 0;
            var width = fixture.ExpectedSize.Width;

            for (var offset = 0; offset < expected.Length; offset += 4)
            {
                int a = expected[offset + 3];
                int r = expected[offset];
                int g = expected[offset + 1];
                int b = expected[offset + 2];

                if (alphaFormat == AlphaFormat.Premul)
                {
                    r = r * a / 255;
                    g = g * a / 255;
                    b = b * a / 255;
                }

                if (Math.Abs(actualRgba[offset] - r) > tolerance ||
                    Math.Abs(actualRgba[offset + 1] - g) > tolerance ||
                    Math.Abs(actualRgba[offset + 2] - b) > tolerance ||
                    Math.Abs(actualRgba[offset + 3] - a) > tolerance)
                {
                    var index = offset / 4;

                    Assert.Fail(
                        $"{fixture.Name}: pixel ({index % width},{index / width}) expected " +
                        $"({r},{g},{b},{a}) but was ({actualRgba[offset]},{actualRgba[offset + 1]}," +
                        $"{actualRgba[offset + 2]},{actualRgba[offset + 3]}), tolerance {tolerance}.");
                }
            }
        }
    }
}
