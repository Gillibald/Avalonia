using System;
using Avalonia.Platform;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// One frame to encode: a pixel source plus per-frame settings. Exactly one pixel
    /// source must be set - <see cref="Pixels"/> or <see cref="Frame"/>.
    /// </summary>
    public sealed class BitmapEncoderFrame
    {
        /// <summary>
        /// Gets an immutable managed snapshot to encode - the source for generated or
        /// externally produced pixels. Obtain one from <see cref="PixelBuffer.Create"/>,
        /// from <see cref="PixelBuffer.CopyFrom"/> over any framebuffer view, or from
        /// <see cref="BitmapFrame.ToPixelBuffer"/>.
        /// </summary>
        public PixelBuffer? Pixels { get; init; }

        /// <summary>
        /// Gets a decoded frame to encode directly - the zero-copy source: the encoder
        /// reads the frame's pixels through a transient view instead of a snapshot. The
        /// caller keeps ownership; the frame must stay undisposed until
        /// <see cref="BitmapEncoder.Save"/> returns.
        /// </summary>
        public BitmapFrame? Frame { get; init; }

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

        /// <summary>
        /// Returns a transient view over the frame's pixels for the backend encoder,
        /// whichever source is set. Dispose the view when the pixels are consumed.
        /// </summary>
        internal ILockedFramebuffer LockPixels()
        {
            if (Pixels is { } pixels)
                return pixels.Lock();

            if (Frame is { } frame)
                return frame.Lock();

            throw new InvalidOperationException(
                $"The {nameof(BitmapEncoderFrame)} has no pixel source; set {nameof(Pixels)} or {nameof(Frame)}.");
        }
    }
}
