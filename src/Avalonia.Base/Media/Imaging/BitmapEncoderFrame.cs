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
        /// Gets or sets the ICC profile written for this frame; it takes precedence over
        /// <see cref="BitmapEncoder.ColorProfile"/>. Requires the format's
        /// <see cref="BitmapCodecCapabilities.ColorProfile"/> capability.
        /// </summary>
        /// <remarks>
        /// A decoded <see cref="Frame"/> does not contribute its embedded profile; copy it
        /// with <see cref="BitmapFrame.TryGetColorProfile"/> to keep it.
        /// </remarks>
        public ReadOnlyMemory<byte>? ColorProfile { get; set; }

        /// <summary>
        /// Gets or sets the palette the frame is written with. Pixels that are not already
        /// indexed in this palette are mapped to its nearest entries, without dithering,
        /// before the backend sees them. When null, an indexed source keeps its own palette.
        /// Requires the format's <see cref="BitmapCodecCapabilities.Palette"/> capability.
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
        /// Gets or sets the frame's offset on the animation canvas, such as the GIF logical
        /// screen. Requires the format's <see cref="BitmapCodecCapabilities.Animation"/> capability
        /// when not zero.
        /// </summary>
        public PixelPoint Origin { get; set; }

        internal PixelSize Size => Pixels?.Size ?? Frame?.PixelSize ?? default;

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
