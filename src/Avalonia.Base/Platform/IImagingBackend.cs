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
    /// startup; <see cref="TryIdentify"/>, <see cref="CreateDecoder"/> and
    /// <see cref="CreateEncoder"/> are safe to call concurrently.
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
        /// Reads header-only image facts without decoding pixels. Must not allocate
        /// frame-sized memory and must not lose data from a non-seekable stream (a
        /// subsequent <see cref="CreateDecoder"/> over the same stream still decodes).
        /// Returns false when the format is not recognized.
        /// </summary>
        bool TryIdentify(Stream stream, out BitmapImageInfo info);

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
                "select one explicitly with an UseXxxImaging AppBuilder extension.");

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
