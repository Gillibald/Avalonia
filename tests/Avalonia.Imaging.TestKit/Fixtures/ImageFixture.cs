using System;
using System.IO;

namespace Avalonia.Imaging.TestKit.Fixtures
{
    /// <summary>
    /// A deterministic, in-memory encoded image plus the facts a decode of it must
    /// reproduce. The encoded bytes are generated on first use; no binary files exist.
    /// </summary>
    public sealed class ImageFixture
    {
        private readonly Lazy<byte[]> _encoded;
        private readonly byte[]? _expectedRgba;

        internal ImageFixture(string name, string expectedFormatName, PixelSize expectedSize,
            bool expectedHasAlpha, byte[]? expectedRgba, Func<byte[]> encode)
        {
            Name = name;
            ExpectedFormatName = expectedFormatName;
            ExpectedSize = expectedSize;
            ExpectedHasAlpha = expectedHasAlpha;
            _expectedRgba = expectedRgba;
            _encoded = new Lazy<byte[]>(encode);
        }

        /// <summary>Gets the fixture name used in assertion messages.</summary>
        public string Name { get; }

        /// <summary>
        /// Gets the container format name a backend must report, or an empty string for
        /// data that is not an image.
        /// </summary>
        public string ExpectedFormatName { get; }

        /// <summary>Gets the size the image header declares.</summary>
        public PixelSize ExpectedSize { get; }

        /// <summary>Gets whether the image carries an alpha channel.</summary>
        public bool ExpectedHasAlpha { get; }

        /// <summary>
        /// Gets the straight (unpremultiplied) RGBA reference pixels, row-major with
        /// four bytes per pixel, or empty when the fixture carries no pixel reference.
        /// </summary>
        public ReadOnlyMemory<byte> ExpectedRgba => _expectedRgba;

        /// <summary>Gets the encoded bytes, generated on first use.</summary>
        public ReadOnlyMemory<byte> EncodedBytes => _encoded.Value;

        /// <summary>Opens a fresh read-only, seekable stream over the encoded bytes.</summary>
        public Stream OpenRead()
        {
            var bytes = _encoded.Value;

            return new MemoryStream(bytes, 0, bytes.Length, writable: false);
        }
    }
}
