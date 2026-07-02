using System.Threading;
using Avalonia.Platform;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// The decode plan: transformations fused into the decode pass, plus decode guards.
    /// Every member is guaranteed semantics on every imaging backend; whether a backend
    /// executes a member natively or the shared pixel pipeline applies it is an
    /// implementation detail.
    /// </summary>
    /// <remarks>
    /// The plan applies in a fixed order: EXIF orientation, then <see cref="SourceRegion"/>
    /// (in oriented source pixels), then <see cref="TargetSize"/>, then pixel/alpha format
    /// conversion. Frame descriptors (<see cref="Avalonia.Platform.IBitmapFrameSource.PixelSize"/>)
    /// report the post-plan values.
    /// </remarks>
    public class BitmapDecodeOptions
    {
        /// <summary>
        /// Gets or sets the target size to decode to. When one dimension is zero the
        /// aspect ratio is preserved. Null decodes at the source size.
        /// </summary>
        public PixelSize? TargetSize { get; set; }

        /// <summary>
        /// Gets or sets the source region to decode, in oriented source pixels.
        /// Null decodes the full frame.
        /// </summary>
        public PixelRect? SourceRegion { get; set; }

        /// <summary>
        /// Gets or sets the pixel format frames should produce, e.g.
        /// <see cref="PixelFormats.Rgb565"/> for a 565 framebuffer target. Null keeps the
        /// backend's native decode format.
        /// </summary>
        public PixelFormat? TargetFormat { get; set; }

        /// <summary>
        /// Gets or sets the alpha format frames should produce; premultiplication is fused
        /// into the decode pass. Null keeps the backend's native alpha format.
        /// </summary>
        public AlphaFormat? TargetAlphaFormat { get; set; }

        /// <summary>
        /// Gets or sets the interpolation used when <see cref="TargetSize"/> requires resampling.
        /// </summary>
        public BitmapInterpolationMode Interpolation { get; set; } = BitmapInterpolationMode.HighQuality;

        /// <summary>
        /// Gets or sets whether the EXIF orientation tag is applied to the decoded pixels.
        /// </summary>
        public bool RespectExifOrientation { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum total pixel count (width times height) a frame may have
        /// before decoding is rejected, guarding against decompression bombs. Null uses
        /// <see cref="ImagingOptions.DefaultMaxPixels"/>.
        /// </summary>
        public long? MaxPixels { get; set; }

        /// <summary>
        /// Gets or sets a token the decode observes between rows, strips or frames where
        /// the backend supports cooperative cancellation.
        /// </summary>
        public CancellationToken Cancellation { get; set; }

        /// <summary>
        /// Gets the effective decompression-bomb guard for these options.
        /// </summary>
        internal long EffectiveMaxPixels => MaxPixels ?? ImagingOptions.Effective.DefaultMaxPixels;
    }

    /// <summary>
    /// JPEG-specific decode options. Format-specific options are validated against the
    /// detected container format: supplying these for a non-JPEG stream is an error.
    /// </summary>
    public sealed class JpegDecodeOptions : BitmapDecodeOptions
    {
        /// <summary>
        /// Gets or sets an explicit libjpeg-style scale denominator (2, 4 or 8) for
        /// DCT-domain scaling. <see cref="BitmapDecodeOptions.TargetSize"/> picks a
        /// denominator automatically; set this only to force one.
        /// </summary>
        public int? ScaleDenominator { get; set; }
    }
}
