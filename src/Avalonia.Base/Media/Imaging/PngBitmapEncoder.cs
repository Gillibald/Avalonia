using System;
using Avalonia.Platform;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// Encodes to PNG.
    /// </summary>
    public sealed class PngBitmapEncoder : BitmapEncoder
    {
        /// <summary>
        /// Gets or sets the zlib compression level, 0 (none) to 9 (best).
        /// </summary>
        public int CompressionLevel { get; set; } = 6;

        /// <summary>
        /// Gets or sets the interlacing mode.
        /// </summary>
        public PngInterlaceMode Interlace { get; set; }

        /// <summary>
        /// Gets or sets the scanline filter strategy.
        /// </summary>
        public PngFilterMode Filter { get; set; }

        /// <inheritdoc />
        protected override void ValidateOptions() =>
            RequireRange(CompressionLevel, 0, 9, nameof(PngBitmapEncoder), nameof(CompressionLevel));

        /// <inheritdoc />
        protected override Guid ContainerFormat => BitmapContainerFormats.Png;

        /// <inheritdoc />
        protected override BitmapCodecCapabilities Capabilities =>
            BitmapCodecCapabilities.Encode | BitmapCodecCapabilities.Palette |
            BitmapCodecCapabilities.Metadata | BitmapCodecCapabilities.ColorProfile;
    }

    /// <summary>
    /// PNG interlacing modes.
    /// </summary>
    public enum PngInterlaceMode
    {
        /// <summary>No interlacing.</summary>
        None,

        /// <summary>Adam7 interlacing.</summary>
        Adam7,
    }

    /// <summary>
    /// PNG scanline filter strategies.
    /// </summary>
    public enum PngFilterMode
    {
        /// <summary>The encoder picks the best filter per scanline.</summary>
        Auto,

        /// <summary>No filtering.</summary>
        None,

        /// <summary>The Sub filter.</summary>
        Sub,

        /// <summary>The Up filter.</summary>
        Up,

        /// <summary>The Average filter.</summary>
        Average,

        /// <summary>The Paeth filter.</summary>
        Paeth,
    }
}
