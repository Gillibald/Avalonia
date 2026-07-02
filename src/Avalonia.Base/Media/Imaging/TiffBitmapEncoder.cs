using System;
using Avalonia.Platform;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// Encodes to TIFF, including multi-page documents.
    /// </summary>
    public sealed class TiffBitmapEncoder : BitmapEncoder
    {
        /// <summary>
        /// Gets or sets the compression scheme.
        /// </summary>
        public TiffCompression Compression { get; set; } = TiffCompression.Lzw;

        /// <inheritdoc />
        protected override Guid ContainerFormat => BitmapContainerFormats.Tiff;

        /// <inheritdoc />
        protected override BitmapCodecCapabilities Capabilities =>
            BitmapCodecCapabilities.Encode | BitmapCodecCapabilities.MultiFrameEncode |
            BitmapCodecCapabilities.Metadata | BitmapCodecCapabilities.ColorProfile;
    }

    /// <summary>
    /// TIFF compression schemes.
    /// </summary>
    public enum TiffCompression
    {
        /// <summary>No compression.</summary>
        None,

        /// <summary>Lempel-Ziv-Welch.</summary>
        Lzw,

        /// <summary>Deflate (zlib).</summary>
        Deflate,

        /// <summary>PackBits run-length encoding.</summary>
        PackBits,

        /// <summary>CCITT Group 3 fax encoding (bilevel).</summary>
        Ccitt3,

        /// <summary>CCITT Group 4 fax encoding (bilevel).</summary>
        Ccitt4,
    }
}
