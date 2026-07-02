using System;
using System.IO;

namespace Avalonia.Imaging.TestKit.Fixtures
{
    /// <summary>
    /// An encoded image whose EXIF orientation tag is 6 (the raw image must rotate 90
    /// degrees clockwise to display upright), plus the facts an orientation-respecting
    /// decode must reproduce. Backend test projects supply one through
    /// <see cref="CodecBackendFixture.CreateOrientedFixture"/>; the orientation
    /// contract tests skip when it is null.
    /// </summary>
    public sealed class OrientedImageFixture
    {
        private readonly byte[] _encoded;
        private readonly byte[] _orientedRgba;

        public OrientedImageFixture(string formatName, byte[] encodedBytes, PixelSize rawSize,
            byte[] orientedRgba, int pixelTolerance)
        {
            FormatName = !string.IsNullOrWhiteSpace(formatName)
                ? formatName
                : throw new ArgumentException("A format name is required.", nameof(formatName));
            _encoded = encodedBytes ?? throw new ArgumentNullException(nameof(encodedBytes));
            RawSize = rawSize;
            _orientedRgba = orientedRgba ?? throw new ArgumentNullException(nameof(orientedRgba));
            PixelTolerance = pixelTolerance;
        }

        /// <summary>Gets the container format name, matching the backend catalog.</summary>
        public string FormatName { get; }

        /// <summary>Gets the stored (raw) size the header declares.</summary>
        public PixelSize RawSize { get; }

        /// <summary>Gets the upright display size: the raw size with its axes swapped.</summary>
        public PixelSize OrientedSize => new(RawSize.Height, RawSize.Width);

        /// <summary>
        /// Gets the expected upright RGBA pixels, row-major with four bytes per pixel.
        /// </summary>
        public ReadOnlyMemory<byte> OrientedRgba => _orientedRgba;

        /// <summary>Gets the per-channel tolerance for comparing decoded pixels.</summary>
        public int PixelTolerance { get; }

        /// <summary>Opens a fresh read-only, seekable stream over the encoded bytes.</summary>
        public Stream OpenRead() => new MemoryStream(_encoded, 0, _encoded.Length, writable: false);
    }
}
