using System;
using Avalonia.Metadata;

namespace Avalonia.Platform
{
    /// <summary>
    /// The backend decoder for a single image stream. Frames are a forward cursor,
    /// not a list: forward-only containers (animated GIF) do not know their frame
    /// count up front and composite sequentially. A backend with random frame access
    /// additionally implements <see cref="ISeekableFrameDecoder"/>.
    /// </summary>
    /// <remarks>
    /// A decoder and its frames are not thread-safe for arbitrary concurrent access;
    /// creating decoders concurrently through <see cref="IImagingBackend"/> is safe.
    /// </remarks>
    [Unstable]
    public interface IBitmapDecoder : IDisposable
    {
        /// <summary>
        /// Gets the codec descriptor for the detected container format.
        /// </summary>
        IBitmapCodecInfo CodecInfo { get; }

        /// <summary>
        /// Gets the number of frames, or null while unknown for forward-only formats
        /// (known once the cursor is exhausted).
        /// </summary>
        int? FrameCount { get; }

        /// <summary>
        /// Advances the frame cursor and returns the next frame, or null when exhausted.
        /// </summary>
        IBitmapFrameSource? ReadNextFrame();
    }

    /// <summary>
    /// Opt-in capability for decoders with random frame access (multi-page TIFF).
    /// </summary>
    [Unstable]
    public interface ISeekableFrameDecoder
    {
        /// <summary>
        /// Gets the known frame count.
        /// </summary>
        int FrameCount { get; }

        /// <summary>
        /// Returns the frame at the given index.
        /// </summary>
        IBitmapFrameSource GetFrame(int index);
    }

    /// <summary>
    /// Opt-in capability for decoders of animated containers.
    /// </summary>
    [Unstable]
    public interface IAnimatedDecoder
    {
        /// <summary>
        /// Gets the animation loop count. Zero means loop forever.
        /// </summary>
        int LoopCount { get; }
    }
}
