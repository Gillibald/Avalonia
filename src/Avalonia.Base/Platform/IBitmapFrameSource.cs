using System;
using Avalonia.Media.Imaging;
using Avalonia.Metadata;

namespace Avalonia.Platform
{
    /// <summary>
    /// A single decoded (or lazily decodable) frame produced by an <see cref="IBitmapDecoder"/>.
    /// Header data is cheap; pixels are paid for on the first <see cref="Lock"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="PixelSize"/>, <see cref="PixelFormat"/> and <see cref="AlphaFormat"/> report
    /// the post-decode-plan values: when a decode plan (target size, region, target format)
    /// was supplied to <see cref="IImagingBackend.CreateDecoder"/>, they describe the pixels
    /// the frame will actually produce. Pixels are exposed only through
    /// <see cref="ILockedFramebuffer"/>; the codec layer never produces an
    /// <see cref="IBitmapImpl"/>.
    /// </remarks>
    [Unstable]
    public interface IBitmapFrameSource : IDisposable
    {
        /// <summary>
        /// Gets the frame size in device pixels, after the decode plan is applied.
        /// </summary>
        PixelSize PixelSize { get; }

        /// <summary>
        /// Gets the frame DPI as stored in the file, or 96 when the format carries none.
        /// </summary>
        Vector Dpi { get; }

        /// <summary>
        /// Gets the pixel format the frame produces, after the decode plan is applied.
        /// </summary>
        PixelFormat PixelFormat { get; }

        /// <summary>
        /// Gets the alpha format the frame produces, after the decode plan is applied.
        /// </summary>
        AlphaFormat AlphaFormat { get; }

        /// <summary>
        /// Returns a transient view over the frame's decoded pixels for zero-copy interop
        /// (render install, transcode, encode). Decodes on first call - deferral is sound
        /// because the decoder owns a stable encoded source independent of the caller's
        /// stream. The frame owns the memory; disposing the view releases one use.
        /// </summary>
        ILockedFramebuffer Lock();
    }

    /// <summary>
    /// Opt-in capability: the frame or decoder exposes metadata.
    /// </summary>
    [Unstable]
    public interface IMetadataSource
    {
        /// <summary>
        /// Gets the metadata attached to this frame or container.
        /// </summary>
        BitmapMetadata Metadata { get; }
    }

    /// <summary>
    /// Opt-in capability: the frame carries an embedded thumbnail.
    /// </summary>
    [Unstable]
    public interface IThumbnailSource
    {
        /// <summary>
        /// Gets the embedded thumbnail, or null when the file has none.
        /// </summary>
        IBitmapFrameSource? Thumbnail { get; }
    }

    /// <summary>
    /// Opt-in capability: the frame carries an ICC color profile.
    /// </summary>
    [Unstable]
    public interface IColorProfileSource
    {
        /// <summary>
        /// Gets the raw ICC profile bytes.
        /// </summary>
        ReadOnlyMemory<byte> IccProfile { get; }
    }

    /// <summary>
    /// Opt-in capability: the frame is part of an animation.
    /// </summary>
    [Unstable]
    public interface IAnimatedFrame
    {
        /// <summary>
        /// Gets the display duration of this frame.
        /// </summary>
        TimeSpan Delay { get; }

        /// <summary>
        /// Gets how the frame is disposed before the next frame is composited.
        /// </summary>
        FrameDisposalMethod Disposal { get; }

        /// <summary>
        /// Gets the placement of this (possibly partial) frame within the logical screen.
        /// </summary>
        PixelPoint Origin { get; }

        /// <summary>
        /// Gets how this frame blends onto the composited result of the previous frames.
        /// </summary>
        FrameBlendMode Blend { get; }
    }

    /// <summary>
    /// Opt-in capability: the frame decodes from an indexed format and exposes its palette.
    /// </summary>
    [Unstable]
    public interface IIndexedFrame
    {
        /// <summary>
        /// Gets the frame's palette.
        /// </summary>
        BitmapPalette Palette { get; }

        /// <summary>
        /// Returns a transient view over the raw index buffer, not transcoded.
        /// </summary>
        ILockedFramebuffer LockIndexed();
    }

    /// <summary>
    /// The parts of a decode plan a backend executed natively for a frame.
    /// </summary>
    [Flags]
    public enum FusedDecodeParts
    {
        /// <summary>The backend fused nothing; the shared pipeline applies the whole plan.</summary>
        None = 0,

        /// <summary>The backend scaled at decode time (possibly to a nearest supported size).</summary>
        Scale = 1,

        /// <summary>The backend decoded only the requested source region.</summary>
        Region = 2,

        /// <summary>The backend produced the requested target pixel/alpha format.</summary>
        Format = 4,

        /// <summary>The backend applied the EXIF orientation.</summary>
        Orientation = 8,
    }

    /// <summary>
    /// Opt-in capability: the backend can execute (part of) the decode plan natively,
    /// e.g. JPEG IDCT scaling. The shared pixel pipeline consults this to decide how
    /// much of the plan remains to be applied.
    /// </summary>
    [Unstable]
    public interface ISupportsFusedDecode
    {
        /// <summary>
        /// Gets which parts of the decode plan this frame fused natively.
        /// </summary>
        FusedDecodeParts FusedParts { get; }

        /// <summary>
        /// Returns the nearest size the backend can decode to natively for the given
        /// target (e.g. an SKCodec 1/2, 1/4, 1/8 fraction); the shared pipeline
        /// resamples the remainder.
        /// </summary>
        PixelSize GetNearestDecodeSize(PixelSize target);
    }

    /// <summary>
    /// Opt-in capability: the backend can apply an orthogonal transform natively.
    /// </summary>
    [Unstable]
    public interface ISupportsBitmapTransforms
    {
        /// <summary>
        /// Returns a transformed frame, or null when the backend cannot apply the transform.
        /// </summary>
        IBitmapFrameSource? TryTransform(BitmapTransform transform);
    }
}
