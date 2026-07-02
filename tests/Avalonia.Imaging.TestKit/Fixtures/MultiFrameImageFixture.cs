using System;
using System.IO;

namespace Avalonia.Imaging.TestKit.Fixtures
{
    /// <summary>
    /// An encoded multi-frame image (e.g. a two-page TIFF) whose frames carry
    /// distinguishable reference pixels. Backend test projects supply one through
    /// <see cref="CodecBackendFixture.CreateMultiFrameFixture"/>; the frame cursor
    /// contract tests skip when it is null.
    /// </summary>
    public sealed class MultiFrameImageFixture
    {
        private readonly byte[] _encoded;
        private readonly byte[][] _frameRgba;

        public MultiFrameImageFixture(string formatName, byte[] encodedBytes, PixelSize frameSize,
            byte[][] frameRgba)
        {
            FormatName = !string.IsNullOrWhiteSpace(formatName)
                ? formatName
                : throw new ArgumentException("A format name is required.", nameof(formatName));
            _encoded = encodedBytes ?? throw new ArgumentNullException(nameof(encodedBytes));
            FrameSize = frameSize;
            _frameRgba = frameRgba is { Length: >= 2 }
                ? frameRgba
                : throw new ArgumentException("At least two frame references are required.", nameof(frameRgba));
        }

        /// <summary>Gets the container format name, matching the backend catalog.</summary>
        public string FormatName { get; }

        /// <summary>Gets the size every frame decodes to.</summary>
        public PixelSize FrameSize { get; }

        /// <summary>Gets the number of frames the container stores.</summary>
        public int FrameCount => _frameRgba.Length;

        /// <summary>
        /// Gets a frame's expected RGBA pixels, row-major with four bytes per pixel.
        /// </summary>
        public ReadOnlyMemory<byte> GetFrameRgba(int frameIndex) => _frameRgba[frameIndex];

        /// <summary>Opens a fresh read-only, seekable stream over the encoded bytes.</summary>
        public Stream OpenRead() => new MemoryStream(_encoded, 0, _encoded.Length, writable: false);
    }
}
