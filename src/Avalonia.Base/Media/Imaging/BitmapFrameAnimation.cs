using System;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// The animation data of a single frame in an animated container.
    /// </summary>
    /// <param name="Delay">How long the frame is displayed.</param>
    /// <param name="Disposal">How the frame is disposed before the next one is composited.</param>
    /// <param name="Origin">The placement of a (possibly partial) frame within the logical screen.</param>
    /// <param name="Blend">How the frame blends onto the composited previous frames.</param>
    public readonly record struct BitmapFrameAnimation(
        TimeSpan Delay,
        FrameDisposalMethod Disposal,
        PixelPoint Origin,
        FrameBlendMode Blend);

    /// <summary>
    /// How an animation frame is disposed before the next frame is composited.
    /// </summary>
    public enum FrameDisposalMethod
    {
        /// <summary>The format did not specify a disposal method.</summary>
        Unspecified = 0,

        /// <summary>Leave the frame in place; the next frame composites over it.</summary>
        DoNotDispose = 1,

        /// <summary>Restore the frame's area to the background color before the next frame.</summary>
        RestoreToBackground = 2,

        /// <summary>Restore the frame's area to the previous composited state before the next frame.</summary>
        RestoreToPrevious = 3,
    }

    /// <summary>
    /// How an animation frame blends onto the composited result of the previous frames.
    /// </summary>
    public enum FrameBlendMode
    {
        /// <summary>Replace the pixels in the frame's area.</summary>
        Source,

        /// <summary>Alpha-blend over the existing pixels.</summary>
        Over,
    }
}
