using System;
using Avalonia.Platform;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// Encodes to WebP.
    /// </summary>
    public sealed class WebpBitmapEncoder : BitmapEncoder
    {
        /// <summary>
        /// Gets or sets whether the output is lossless.
        /// </summary>
        public bool Lossless { get; set; }

        /// <summary>
        /// Gets or sets the compression quality, 1 to 100.
        /// </summary>
        public int Quality { get; set; } = 75;

        /// <inheritdoc />
        protected override void ValidateOptions() =>
            RequireRange(Quality, 1, 100, nameof(WebpBitmapEncoder), nameof(Quality));

        /// <inheritdoc />
        protected override Guid ContainerFormat => BitmapContainerFormats.Webp;

        /// <inheritdoc />
        protected override BitmapCodecCapabilities Capabilities =>
            BitmapCodecCapabilities.Encode | BitmapCodecCapabilities.MultiFrameEncode |
            BitmapCodecCapabilities.Animation;
    }
}
