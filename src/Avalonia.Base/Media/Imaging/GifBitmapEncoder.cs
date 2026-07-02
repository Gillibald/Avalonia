using System;
using Avalonia.Platform;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// Encodes to GIF, including animations.
    /// </summary>
    public sealed class GifBitmapEncoder : BitmapEncoder
    {
        /// <summary>
        /// Gets or sets the animation loop count. Zero loops forever.
        /// </summary>
        public int LoopCount { get; set; }

        /// <summary>
        /// Gets or sets the global palette; null lets the encoder quantize.
        /// </summary>
        public BitmapPalette? GlobalPalette { get; set; }

        /// <inheritdoc />
        protected override Guid ContainerFormat => BitmapContainerFormats.Gif;

        /// <inheritdoc />
        protected override BitmapCodecCapabilities Capabilities =>
            BitmapCodecCapabilities.Encode | BitmapCodecCapabilities.MultiFrameEncode |
            BitmapCodecCapabilities.Animation | BitmapCodecCapabilities.Palette;
    }
}
