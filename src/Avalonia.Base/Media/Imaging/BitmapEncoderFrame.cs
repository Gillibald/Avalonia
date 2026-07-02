using System;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// One frame to encode: an immutable pixel snapshot plus per-frame settings.
    /// </summary>
    public sealed class BitmapEncoderFrame
    {
        /// <summary>
        /// Gets the pixels to encode. Obtain one from a decoded frame, from
        /// <see cref="PixelBuffer.CopyFrom"/> over any framebuffer view, or from a
        /// WriteableBitmap lock.
        /// </summary>
        public required PixelBuffer Pixels { get; init; }

        /// <summary>
        /// Gets or sets per-frame metadata (e.g. a TIFF page's tags).
        /// </summary>
        public BitmapMetadata? Metadata { get; set; }

        /// <summary>
        /// Gets or sets the palette for indexed encode targets.
        /// </summary>
        public BitmapPalette? Palette { get; set; }

        /// <summary>
        /// Gets or sets the display duration when encoding an animation frame.
        /// </summary>
        public TimeSpan Delay { get; set; }

        /// <summary>
        /// Gets or sets the disposal method when encoding an animation frame.
        /// </summary>
        public FrameDisposalMethod Disposal { get; set; }
    }
}
