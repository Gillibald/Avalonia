using System;
using Avalonia.Platform;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// Encodes to JPEG.
    /// </summary>
    public sealed class JpegBitmapEncoder : BitmapEncoder
    {
        /// <summary>
        /// Gets or sets the compression quality, 1 to 100.
        /// </summary>
        public int Quality { get; set; } = 90;

        /// <inheritdoc />
        protected override void ValidateOptions() =>
            RequireRange(Quality, 1, 100, nameof(JpegBitmapEncoder), nameof(Quality));

        /// <inheritdoc />
        protected override Guid ContainerFormat => BitmapContainerFormats.Jpeg;

        /// <inheritdoc />
        protected override BitmapCodecCapabilities Capabilities =>
            BitmapCodecCapabilities.Encode | BitmapCodecCapabilities.Metadata |
            BitmapCodecCapabilities.Thumbnail | BitmapCodecCapabilities.ColorProfile;
    }
}
