using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Metadata;

namespace Avalonia.Platform
{
    /// <summary>
    /// The imaging backend: decode, encode and identify for a set of container formats.
    /// Exactly one backend is active per application, selected at startup; backends are
    /// never mixed and there is no fallback between them.
    /// </summary>
    /// <remarks>
    /// The backend is render-independent: pixels only ever cross this boundary as
    /// <see cref="ILockedFramebuffer"/> views or <see cref="PixelBuffer"/> snapshots,
    /// never as <see cref="IBitmapImpl"/>. Implementations must be immutable after
    /// startup; <see cref="TryIdentify(Stream, out BitmapImageInfo)"/>,
    /// <see cref="CreateDecoder"/> and <see cref="CreateEncoder"/> are safe to call
    /// concurrently.
    /// </remarks>
    [Unstable]
    public interface IImagingBackend
    {
        /// <summary>
        /// Gets the backend name used in error messages, e.g. "SkiaSharp", "ImageSharp", "PixelMan".
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets the per-format capability catalog of this backend.
        /// </summary>
        IReadOnlyList<IBitmapCodecInfo> SupportedCodecs { get; }

        /// <summary>
        /// Gets the prefix length in bytes that suffices to identify every format this
        /// backend supports. <see cref="TryIdentify(ReadOnlySpan{byte}, out BitmapImageInfo)"/>
        /// is defined for complete payloads or prefixes of at least this length.
        /// </summary>
        int IdentifyPrefixLength { get; }

        /// <summary>
        /// Reads header-only image facts without decoding pixels or allocating
        /// frame-sized memory. The stream must be seekable; its position is restored, so
        /// identify is repeatable and consumes nothing. Returns false only when the
        /// format is not recognized.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// The stream is not seekable. Identify cannot read a forward-only stream
        /// without consuming it - create a decoder instead and read
        /// <see cref="IBitmapDecoder.Info"/>, or identify from bytes.
        /// </exception>
        bool TryIdentify(Stream stream, out BitmapImageInfo info);

        /// <summary>
        /// Reads header-only image facts from in-memory encoded data: a complete payload
        /// or a prefix of at least <see cref="IdentifyPrefixLength"/> bytes. Returns
        /// false only when the format is not recognized.
        /// </summary>
        bool TryIdentify(ReadOnlySpan<byte> data, out BitmapImageInfo info);

        /// <summary>
        /// Detects the container format from the stream header and returns a decoder for it.
        /// Throws <see cref="NotSupportedException"/> naming this backend when the format
        /// is not supported.
        /// </summary>
        /// <param name="stream">The encoded image stream.</param>
        /// <param name="ownsStream">
        /// When true the decoder takes ownership of the stream and disposes it with itself.
        /// </param>
        /// <param name="options">The decode plan applied to all frames of this decoder.</param>
        /// <remarks>
        /// From creation on, the decoder owns a stable, rewindable encoded source. When
        /// the stream is seekable and owned, the decoder may keep it and read on demand;
        /// the stream belongs to the decoder from this call on. In every other case the
        /// backend materializes an owned stable copy of the remaining encoded data before
        /// returning (a memory buffer, a duplicated file handle or a temporary file), and
        /// the caller's stream is entirely free once this method returns. This is what
        /// makes deferred frame decoding sound: a frame's pixels can be produced at any
        /// later time without touching the caller's stream. Materialization observes
        /// <see cref="BitmapDecodeOptions.CancellationToken"/>;
        /// <see cref="BitmapDecodeOptions.MaterializeSource"/> forces it even when
        /// deferral would be allowed.
        /// </remarks>
        IBitmapDecoder CreateDecoder(Stream stream, bool ownsStream, BitmapDecodeOptions? options = null);

        /// <summary>
        /// Creates the encoder implementation for a container format. Throws
        /// <see cref="NotSupportedException"/> naming this backend when it cannot encode
        /// the format.
        /// </summary>
        IBitmapEncoderImpl CreateEncoder(Guid containerFormat);
    }

    /// <summary>
    /// Access to the single active <see cref="IImagingBackend"/>.
    /// </summary>
    public static class ImagingBackend
    {
        /// <summary>
        /// Gets the active imaging backend, throwing when none is configured.
        /// </summary>
        public static IImagingBackend Current =>
            AvaloniaLocator.Current.GetService<IImagingBackend>() ??
            throw new InvalidOperationException(
                "No imaging backend is configured. The default backend is bound by UseSkia; " +
                "select one explicitly with a UseXxxImaging AppBuilder extension.");

        /// <summary>
        /// Gets the active imaging backend, or null when none is configured.
        /// </summary>
        public static IImagingBackend? CurrentOrNull =>
            AvaloniaLocator.Current.GetService<IImagingBackend>();

        /// <summary>
        /// Binds an imaging backend. Called by the UseXxxImaging AppBuilder extensions
        /// (with <paramref name="isDefault"/> false) and by the render backend that
        /// supplies the default (with <paramref name="isDefault"/> true).
        /// </summary>
        /// <remarks>
        /// Exactly one backend may be selected: registering a second, different explicit
        /// backend throws. A default registration never replaces an existing binding.
        /// </remarks>
        public static void Register(IImagingBackend backend, bool isDefault = false)
        {
            _ = backend ?? throw new ArgumentNullException(nameof(backend));

            if (isDefault)
            {
                if (AvaloniaLocator.Current.GetService<IImagingBackend>() is null)
                    AvaloniaLocator.CurrentMutable.Bind<IImagingBackend>().ToConstant(backend);
                return;
            }

            var existing = AvaloniaLocator.Current.GetService<ExplicitRegistration>();

            // The marker can be inherited from a parent locator scope whose backend
            // binding has since been shadowed; only a marker that still matches the
            // resolvable backend is authoritative.
            if (existing is not null &&
                !ReferenceEquals(AvaloniaLocator.Current.GetService<IImagingBackend>(), existing.Backend))
            {
                existing = null;
            }

            if (existing is not null && !string.Equals(existing.Backend.Name, backend.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"An imaging backend is already configured ('{existing.Backend.Name}'); " +
                    $"'{backend.Name}' cannot also be used. Exactly one imaging backend must be selected.");
            }

            AvaloniaLocator.CurrentMutable.Bind<IImagingBackend>().ToConstant(backend);
            AvaloniaLocator.CurrentMutable.Bind<ExplicitRegistration>().ToConstant(new ExplicitRegistration(backend));
        }

        private sealed class ExplicitRegistration
        {
            public ExplicitRegistration(IImagingBackend backend) => Backend = backend;

            public IImagingBackend Backend { get; }
        }
    }
}
