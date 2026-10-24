using System;
using Avalonia.Platform;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// Encodes to GIF, including animations.
    /// </summary>
    /// <remarks>
    /// Frames are written as given: each at its <see cref="BitmapEncoderFrame.Origin"/> on the
    /// logical screen, with its <see cref="BitmapEncoderFrame.Delay"/> and
    /// <see cref="BitmapEncoderFrame.Disposal"/>. A frame does not have to cover the screen,
    /// and frames are not composited. GIF stores delays in hundredths of a second, so a delay
    /// rounds to the nearest one.
    /// </remarks>
    public sealed class GifBitmapEncoder : BitmapEncoder
    {
        private static readonly TimeSpan s_maxDelay = TimeSpan.FromMilliseconds(ushort.MaxValue * 10.0);

        /// <summary>
        /// Gets or sets the animation loop count. Zero loops forever.
        /// </summary>
        public int LoopCount { get; set; }

        /// <summary>
        /// Gets or sets the global palette, which frames without a
        /// <see cref="BitmapEncoderFrame.Palette"/> of their own are written with; null lets the
        /// encoder quantize.
        /// </summary>
        public BitmapPalette? GlobalPalette { get; set; }

        /// <summary>
        /// Gets or sets the size of the logical screen the frames are placed on. Null uses
        /// the smallest screen that holds every frame at its origin.
        /// </summary>
        public PixelSize? ScreenSize { get; set; }

        /// <inheritdoc />
        protected override Guid ContainerFormat => BitmapContainerFormats.Gif;

        /// <inheritdoc />
        protected override BitmapCodecCapabilities Capabilities =>
            BitmapCodecCapabilities.Encode | BitmapCodecCapabilities.MultiFrameEncode |
            BitmapCodecCapabilities.Animation | BitmapCodecCapabilities.Palette;

        private protected override BitmapPalette? DefaultFramePalette => GlobalPalette;

        /// <summary>
        /// Returns <see cref="ScreenSize"/>, or the smallest screen that holds every frame.
        /// </summary>
        internal PixelSize GetScreenSize()
        {
            if (ScreenSize is { } size)
                return size;

            var width = 0;
            var height = 0;

            foreach (var frame in Frames)
            {
                width = Math.Max(width, frame.Origin.X + frame.Size.Width);
                height = Math.Max(height, frame.Origin.Y + frame.Size.Height);
            }

            return new PixelSize(width, height);
        }

        /// <inheritdoc />
        protected override void ValidateOptions()
        {
            RequireRange(LoopCount, 0, ushort.MaxValue, nameof(GifBitmapEncoder), nameof(LoopCount));

            if (ScreenSize is { } size)
            {
                RequireRange(size.Width, 1, ushort.MaxValue, nameof(GifBitmapEncoder), nameof(ScreenSize) + ".Width");
                RequireRange(size.Height, 1, ushort.MaxValue, nameof(GifBitmapEncoder), nameof(ScreenSize) + ".Height");
            }

            var screen = GetScreenSize();

            RequireRange(screen.Width, 1, ushort.MaxValue, nameof(GifBitmapEncoder), "screen width");
            RequireRange(screen.Height, 1, ushort.MaxValue, nameof(GifBitmapEncoder), "screen height");

            for (var i = 0; i < Frames.Count; i++)
            {
                var frame = Frames[i];

                if (frame.Origin.X + frame.Size.Width > screen.Width || frame.Origin.Y + frame.Size.Height > screen.Height)
                    throw new InvalidOperationException(
                        $"Frame {i} at {frame.Origin} with size {frame.Size} does not fit the {screen} screen.");

                if (frame.Delay < TimeSpan.Zero || frame.Delay > s_maxDelay)
                    throw new InvalidOperationException(
                        $"Frame {i} has a delay of {frame.Delay}; GIF delays are between zero and {s_maxDelay}.");
            }
        }
    }
}
