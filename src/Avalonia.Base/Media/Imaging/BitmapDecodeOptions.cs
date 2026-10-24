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
        /// <see cref="PixelFormats.Rgb565"/> for a 565 framebuffer target. It overrides
        /// <see cref="PreservePixelFormat"/> and <see cref="FormatProfile"/>; null lets them
        /// choose the format.
        /// </summary>
        public PixelFormat? TargetFormat { get; set; }

        /// <summary>
        /// Gets or sets the alpha format frames should produce; premultiplication is fused
        /// into the decode pass. It overrides <see cref="PreservePixelFormat"/> and
        /// <see cref="FormatProfile"/>; null lets them choose the alpha format.
        /// </summary>
        public AlphaFormat? TargetAlphaFormat { get; set; }

        /// <summary>
        /// Gets or sets whether frames keep the image's stored pixel format, named as
        /// <see cref="FormatProfile"/> names it. False, the default, converts frames to the
        /// profile's target format in the decode pass: for
        /// <see cref="BitmapFormatProfile.RenderBackend"/> that is the render backend's default
        /// pixel and alpha format, so a decoded frame installs for drawing without another
        /// conversion. Code that processes pixels sets it to keep the codec's layout:
        /// palette images stay indexed, 16-bit and floating-point images keep their depth.
        /// </summary>
        public bool PreservePixelFormat { get; set; }

        /// <summary>
        /// Gets or sets whose pixel formats apply when <see cref="TargetFormat"/> is not set.
        /// </summary>
        public BitmapFormatProfile FormatProfile { get; set; } = BitmapFormatProfile.RenderBackend;

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
        /// the backend supports cooperative cancellation, and while the decoder
        /// materializes its copy of the encoded data.
        /// </summary>
        public CancellationToken CancellationToken { get; set; }

        /// <summary>
        /// Forces the decoder to materialize its own copy of the encoded data at
        /// creation even when it could defer reads to an owned seekable stream,
        /// releasing the input (e.g. a file lock) immediately.
        /// </summary>
        public bool MaterializeSource { get; set; }

        /// <summary>
        /// Set when <see cref="TargetAlphaFormat"/> is the render backend's default rather
        /// than the caller's choice: an opaque source then keeps <see cref="AlphaFormat.Opaque"/>.
        /// Opaque pixels are the same bytes premultiplied or not, so this installs as is and
        /// saves the decode a premultiplication pass that would change nothing.
        /// </summary>
        internal bool OpaqueSatisfiesTargetAlpha { get; set; }

        /// <summary>
        /// The alpha format a frame decoded from <paramref name="sourceFormat"/> with
        /// <paramref name="sourceAlpha"/> produces, or null to keep the backend's own. An
        /// indexed source counts as not opaque, since its palette may carry alpha.
        /// </summary>
        internal AlphaFormat? ResolveTargetAlpha(PixelFormat sourceFormat, AlphaFormat sourceAlpha) =>
            OpaqueSatisfiesTargetAlpha && sourceAlpha == AlphaFormat.Opaque && !sourceFormat.IsIndexed
                ? AlphaFormat.Opaque
                : TargetAlphaFormat;

        /// <summary>
        /// Gets the effective decompression-bomb guard for these options.
        /// </summary>
        internal long EffectiveMaxPixels => MaxPixels ?? ImagingOptions.Effective.DefaultMaxPixels;

        /// <summary>
        /// Returns a shallow copy preserving the runtime type, so async entry points can
        /// inject a token without mutating the caller's instance.
        /// </summary>
        internal BitmapDecodeOptions Clone() => (BitmapDecodeOptions)MemberwiseClone();
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
