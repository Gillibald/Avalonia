using System;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// Memory and concurrency budgets for the imaging layer. Bind an instance at startup
    /// with <c>AppBuilder.With(new ImagingOptions { ... })</c>; when none is bound the
    /// defaults below apply.
    /// </summary>
    /// <remarks>
    /// The defaults are desktop-safe. Hardware-limited targets should lower them, e.g.
    /// a 4 MB pool, a 2 MB animation cache, 16 million pixels and 2 concurrent decodes
    /// on a 256 MB device.
    /// </remarks>
    public class ImagingOptions
    {
        private static readonly ImagingOptions s_default = new();

        /// <summary>
        /// Gets or sets the ceiling for idle pooled decode memory, counted in bucket
        /// bytes. Returning a buffer beyond the ceiling frees it instead of pooling it,
        /// so the ceiling bounds retention, not peak usage; peak is bounded by
        /// <see cref="MaxConcurrentDecodes"/>.
        /// </summary>
        public long DecodePoolMaxBytes { get; set; } = 16 * 1024 * 1024;

        /// <summary>
        /// Gets or sets the decompression-bomb guard applied when
        /// <see cref="BitmapDecodeOptions.MaxPixels"/> is not set: the maximum total pixel
        /// count (width times height) a frame may have before decoding is rejected, checked
        /// before any frame memory is allocated.
        /// </summary>
        public long DefaultMaxPixels { get; set; } = 256_000_000;

        /// <summary>
        /// Gets or sets how many frame decodes may hold pixel buffers concurrently,
        /// bounding peak native memory when many images decode in parallel.
        /// </summary>
        public int MaxConcurrentDecodes { get; set; } = Environment.ProcessorCount;

        /// <summary>
        /// Gets or sets the byte ceiling for realized frames cached by animation playback.
        /// Cached frames are pool rentals, so this is accounting within
        /// <see cref="DecodePoolMaxBytes"/>, not on top of it.
        /// </summary>
        public long AnimationCacheMaxBytes { get; set; } = 8 * 1024 * 1024;

        /// <summary>
        /// Gets the bound options, or the defaults when the application bound none.
        /// </summary>
        internal static ImagingOptions Effective =>
            AvaloniaLocator.Current.GetService<ImagingOptions>() ?? s_default;
    }
}
