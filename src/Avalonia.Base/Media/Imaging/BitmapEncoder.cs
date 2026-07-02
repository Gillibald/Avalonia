using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// Base class of the per-format encoders. The base owns the shared contract (frames,
    /// metadata, transform-on-encode, color profile, <see cref="Save"/>); each concrete
    /// encoder adds only its own options as properties and declares its format's
    /// capabilities as data.
    /// </summary>
    /// <remarks>
    /// <see cref="Save"/> validates the frame set against both the encoder-declared
    /// capabilities and the active backend's per-format capabilities before any byte is
    /// written, then dispatches to the backend's <see cref="IBitmapEncoderImpl"/> for
    /// this container format. Encoding an unsupported format fails fast naming the
    /// active backend.
    /// </remarks>
    public abstract class BitmapEncoder
    {
        /// <summary>
        /// Gets the frames to encode. Single-frame formats accept exactly one; encoding
        /// more than one frame requires <see cref="BitmapCodecCapabilities.MultiFrameEncode"/>.
        /// </summary>
        public IList<BitmapEncoderFrame> Frames { get; } = new List<BitmapEncoderFrame>();

        /// <summary>
        /// Gets or sets container-level metadata; requires the format's
        /// <see cref="BitmapCodecCapabilities.Metadata"/> capability.
        /// </summary>
        public BitmapMetadata? Metadata { get; set; }

        /// <summary>
        /// Gets or sets an orthogonal transform applied while encoding.
        /// </summary>
        public BitmapTransform Transform { get; set; }

        /// <summary>
        /// Gets or sets an ICC profile written into the container; requires the format's
        /// <see cref="BitmapCodecCapabilities.ColorProfile"/> capability.
        /// </summary>
        public ReadOnlyMemory<byte>? ColorProfile { get; set; }

        /// <summary>
        /// Gets the container format this encoder produces, see <see cref="BitmapContainerFormats"/>.
        /// </summary>
        protected abstract Guid ContainerFormat { get; }

        /// <summary>
        /// Gets the capabilities of this encoder's format model, validated in <see cref="Save"/>.
        /// </summary>
        protected abstract BitmapCodecCapabilities Capabilities { get; }

        /// <summary>
        /// Encodes the frames to the stream. Validation runs before any byte is written.
        /// </summary>
        public void Save(Stream stream)
        {
            _ = stream ?? throw new ArgumentNullException(nameof(stream));

            var backend = ImagingBackend.Current;

            Validate(backend);

            var impl = backend.CreateEncoder(ContainerFormat);

            impl.Encode(this, stream);
        }

        /// <summary>
        /// Encodes the frames to the stream on a background thread.
        /// </summary>
        public Task SaveAsync(Stream stream, CancellationToken cancellationToken = default)
            => Task.Run(() => Save(stream), cancellationToken);

        /// <summary>
        /// Creates the encoder for a well-known format.
        /// </summary>
        public static BitmapEncoder Create(BitmapFileFormat format) => format switch
        {
            BitmapFileFormat.Png => new PngBitmapEncoder(),
            BitmapFileFormat.Jpeg => new JpegBitmapEncoder(),
            BitmapFileFormat.Tiff => new TiffBitmapEncoder(),
            BitmapFileFormat.Gif => new GifBitmapEncoder(),
            BitmapFileFormat.Webp => new WebpBitmapEncoder(),
            BitmapFileFormat.Bmp => new BmpBitmapEncoder(),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

        private void Validate(IImagingBackend backend)
        {
            if (Frames.Count == 0)
                throw new InvalidOperationException(
                    $"{GetType().Name} has no frames to encode; add at least one to {nameof(Frames)}.");

            var backendCapabilities = GetBackendCapabilities(backend);

            if (Frames.Count > 1)
                RequireCapability(backend, backendCapabilities, BitmapCodecCapabilities.MultiFrameEncode, "multi-frame encoding");

            if (Metadata is not null || HasFrameMetadata())
                RequireCapability(backend, backendCapabilities, BitmapCodecCapabilities.Metadata, "writing metadata");

            if (ColorProfile is not null)
                RequireCapability(backend, backendCapabilities, BitmapCodecCapabilities.ColorProfile, "embedding a color profile");
        }

        private void RequireCapability(IImagingBackend backend, BitmapCodecCapabilities? backendCapabilities,
            BitmapCodecCapabilities capability, string operation)
        {
            if ((Capabilities & capability) == 0)
                throw new NotSupportedException(
                    $"{GetType().Name} does not support {operation}.");

            if (backendCapabilities is { } caps && (caps & capability) == 0)
                throw new NotSupportedException(
                    $"The '{backend.Name}' imaging backend does not support {operation} for this format.");
        }

        private bool HasFrameMetadata()
        {
            for (var i = 0; i < Frames.Count; i++)
            {
                if (Frames[i].Metadata is not null)
                    return true;
            }

            return false;
        }

        private BitmapCodecCapabilities? GetBackendCapabilities(IImagingBackend backend)
        {
            var codecs = backend.SupportedCodecs;

            for (var i = 0; i < codecs.Count; i++)
            {
                if (codecs[i].ContainerFormat == ContainerFormat)
                    return codecs[i].Capabilities;
            }

            return null;
        }
    }
}
