using System;
using Avalonia.Media.Imaging;
using Avalonia.Metadata;

namespace Avalonia.Platform
{
    /// <summary>
    /// An imaging backend capability: transforms colors from one ICC profile to another.
    /// Core has no color management of its own, so <see cref="BitmapColorTransform"/> is
    /// available only on backends that implement this.
    /// </summary>
    /// <remarks>
    /// The core checks both profiles' headers and only passes RGB, gray and CMYK profiles.
    /// It converts pixels to and from the exchange rows, so the backend deals with color
    /// values only.
    /// </remarks>
    [Unstable]
    public interface IColorTransformProvider
    {
        /// <summary>
        /// Prepares a transform between two profiles.
        /// </summary>
        /// <param name="sourceProfile">The ICC profile the input colors are in.</param>
        /// <param name="destinationProfile">The ICC profile the output colors are in.</param>
        /// <param name="intent">
        /// The rendering intent to apply, or null to choose one from what the profiles hold.
        /// </param>
        /// <exception cref="NotSupportedException">
        /// The backend cannot use one of the profiles or does not offer the intent.
        /// </exception>
        IColorTransformImpl CreateColorTransform(ReadOnlySpan<byte> sourceProfile,
            ReadOnlySpan<byte> destinationProfile, ColorRenderingIntent? intent);
    }

    /// <summary>
    /// A prepared color transform. <see cref="Transform"/> may be called from several
    /// threads at once.
    /// </summary>
    [Unstable]
    public interface IColorTransformImpl : IDisposable
    {
        /// <summary>
        /// Transforms a run of colors.
        /// </summary>
        /// <param name="source">
        /// The input colors, channel-interleaved in the source profile's order (RGB, gray or
        /// CMYK), each channel the encoded value in 0 to 1. No alpha.
        /// </param>
        /// <param name="destination">
        /// The output colors in the destination profile's channel order and encoding. Values
        /// outside 0 to 1 are clamped by the caller.
        /// </param>
        /// <param name="pixelCount">The number of colors in the run.</param>
        void Transform(ReadOnlySpan<float> source, Span<float> destination, int pixelCount);
    }
}
