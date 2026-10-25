using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private ReadOnlyMemory<byte>? _colorProfile;

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
        /// Gets or sets an ICC profile written for every frame that sets no
        /// <see cref="BitmapEncoderFrame.ColorProfile"/> of its own; requires the format's
        /// <see cref="BitmapCodecCapabilities.ColorProfile"/> capability. An empty profile, such as
        /// a null array converts to, means none.
        /// </summary>
        public ReadOnlyMemory<byte>? ColorProfile
        {
            get => _colorProfile;
            set => _colorProfile = value is { IsEmpty: true } ? null : value;
        }

        /// <summary>
        /// Gets or sets whose encoding choices apply to what the options leave open: the stored
        /// pixel format, the metadata layout and the defaults of options that are null.
        /// <see cref="BitmapFormatProfile.Wic"/> encodes as WIC does and requires the backend's
        /// <see cref="BitmapCodecCapabilities.WicFormatProfile"/> capability for the format.
        /// </summary>
        public BitmapFormatProfile FormatProfile { get; set; } = BitmapFormatProfile.RenderBackend;

        /// <summary>
        /// Gets the container format this encoder produces, see <see cref="BitmapContainerFormats"/>.
        /// </summary>
        protected abstract Guid ContainerFormat { get; }

        /// <summary>
        /// Gets the capabilities of this encoder's format model, validated in <see cref="Save"/>.
        /// </summary>
        protected abstract BitmapCodecCapabilities Capabilities { get; }

        /// <summary>
        /// Encodes the frames to the stream. Validation runs before any byte is written;
        /// the token is observed between frames where the backend supports cooperative
        /// cancellation.
        /// </summary>
        public void Save(Stream stream, CancellationToken cancellationToken = default)
        {
            _ = stream ?? throw new ArgumentNullException(nameof(stream));

            if (!stream.CanWrite)
                throw new ArgumentException("The stream is not writable.", nameof(stream));

            cancellationToken.ThrowIfCancellationRequested();

            var backend = ImagingBackend.Current;
            var backendCapabilities = Validate(backend);
            var impl = backend.CreateEncoder(ContainerFormat);

            if (stream.CanSeek ||
                (backendCapabilities & BitmapCodecCapabilities.EncodeRequiresSeekableStream) == 0)
            {
                impl.Encode(this, stream, cancellationToken);
                return;
            }

            using var buffer = new MemoryStream();

            impl.Encode(this, buffer, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            buffer.WriteTo(stream);
        }

        /// <summary>
        /// Encodes the frames to the stream on a background thread. The token gates the
        /// start and is observed between frames where the backend supports cooperative
        /// cancellation. A stream other than a <see cref="MemoryStream"/>,
        /// <see cref="UnmanagedMemoryStream"/> or <see cref="FileStream"/> is encoded into
        /// memory first and then written asynchronously, so streams that refuse synchronous
        /// writes, such as the browser's file streams, work too.
        /// </summary>
        public Task SaveAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            _ = stream ?? throw new ArgumentNullException(nameof(stream));

            if (!stream.CanWrite)
                throw new ArgumentException("The stream is not writable.", nameof(stream));

            if (AsyncStreamBuffer.SupportsSynchronousIO(stream))
                return Task.Run(() => Save(stream, cancellationToken), cancellationToken);

            return SaveBufferedAsync(stream, cancellationToken);
        }

        private async Task SaveBufferedAsync(Stream stream, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();

            await Task.Run(() => Save(buffer, cancellationToken), cancellationToken).ConfigureAwait(false);

            // The memory overload, the one a stream written to current guidance overrides.
            await stream.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

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

        /// <summary>
        /// Validates this encoder's option values, called from <see cref="Save"/> before
        /// any byte is written. Concrete encoders override to range-check their options
        /// and throw <see cref="InvalidOperationException"/> on invalid values.
        /// </summary>
        protected virtual void ValidateOptions()
        {
        }

        /// <summary>
        /// Gets the palette of frames that set no <see cref="BitmapEncoderFrame.Palette"/>; null
        /// when the format has no container-wide palette.
        /// </summary>
        private protected virtual BitmapPalette? DefaultFramePalette => null;

        /// <summary>
        /// Returns a transient view over the pixels a backend writes for a frame, with the
        /// frame's palette applied: indexed pixels in the frame palette (or
        /// <see cref="DefaultFramePalette"/>) when one is set, otherwise the source as is.
        /// </summary>
        /// <param name="index">The frame index.</param>
        /// <param name="palette">The palette of the returned pixels when they are indexed; null otherwise.</param>
        internal ILockedFramebuffer LockFramePixels(int index, out BitmapPalette? palette)
        {
            var frame = Frames[index];

            if ((frame.Palette ?? DefaultFramePalette) is not { } target)
            {
                var view = frame.LockPixels();

                palette = !view.Format.IsIndexed ? null
                    : frame.Pixels is { } pixels ? pixels.Palette
                    : frame.Frame!.TryGetPalette(out var own) ? own
                    : null;

                return view;
            }

            var source = frame.Pixels ?? frame.Frame!.ToPixelBuffer();
            var format = GetIndexedFormat(target.Colors.Count);

            palette = target;

            if (source.Format == format && source.Palette is { } sourcePalette &&
                sourcePalette.Colors.SequenceEqual(target.Colors))
            {
                return source.Lock();
            }

            return source.Convert(format, AlphaFormat.Unpremul, new PixelConversionOptions { Palette = target }).Lock();
        }

        /// <summary>
        /// Returns a transient view over a frame's pixels for backends that write no
        /// palettes: indexed sources are expanded to <see cref="PixelFormats.Bgra8888"/>.
        /// </summary>
        internal ILockedFramebuffer LockFramePixelsExpanded(int index)
        {
            var view = LockFramePixels(index, out var palette);

            if (!view.Format.IsIndexed)
                return view;

            using (view)
            {
                return PixelBuffer.CopyFrom(view, palette).Convert(PixelFormats.Bgra8888, AlphaFormat.Unpremul).Lock();
            }
        }

        private static PixelFormat GetIndexedFormat(int colorCount) => colorCount switch
        {
            <= 2 => PixelFormats.Indexed1,
            <= 4 => PixelFormats.Indexed2,
            <= 16 => PixelFormats.Indexed4,
            _ => PixelFormats.Indexed8,
        };

        private protected static void RequireRange(int value, int min, int max, string encoder, string option)
        {
            if (value < min || value > max)
                throw new InvalidOperationException(
                    $"{encoder}.{option} must be between {min} and {max}; got {value}.");
        }

        private BitmapCodecCapabilities Validate(IImagingBackend backend)
        {
            if (Frames.Count == 0)
                throw new InvalidOperationException(
                    $"{GetType().Name} has no frames to encode; add at least one to {nameof(Frames)}.");

            for (var i = 0; i < Frames.Count; i++)
            {
                var frame = Frames[i];

                if (frame.Pixels is null == frame.Frame is null)
                {
                    throw new InvalidOperationException(
                        $"A {nameof(BitmapEncoderFrame)} needs exactly one pixel source: set either " +
                        $"{nameof(BitmapEncoderFrame.Pixels)} or {nameof(BitmapEncoderFrame.Frame)}.");
                }

                if (frame.Origin != default && (Capabilities & BitmapCodecCapabilities.Animation) == 0)
                    throw new NotSupportedException($"{GetType().Name} does not support frame origins.");

                if (frame.Origin.X < 0 || frame.Origin.Y < 0)
                    throw new InvalidOperationException(
                        $"{nameof(BitmapEncoderFrame)}.{nameof(BitmapEncoderFrame.Origin)} must not be negative; got {frame.Origin}.");
            }

            ValidateOptions();

            var backendCapabilities = GetBackendCapabilities(backend);

            if (Frames.Count > 1)
                RequireCapability(backend, backendCapabilities, BitmapCodecCapabilities.MultiFrameEncode, "multi-frame encoding");

            if (Metadata is not null || HasFrameMetadata())
                RequireCapability(backend, backendCapabilities, BitmapCodecCapabilities.Metadata, "writing metadata");

            if (ColorProfile is not null || HasFrameColorProfile())
                RequireCapability(backend, backendCapabilities, BitmapCodecCapabilities.ColorProfile, "embedding a color profile");

            if (HasFramePalette() || DefaultFramePalette is not null)
                RequireCapability(backend, backendCapabilities, BitmapCodecCapabilities.Palette, "encoding a palette");

            // The WIC profile is a backend quality, not a property of the format, so only the
            // backend's codec decides.
            if (FormatProfile == BitmapFormatProfile.Wic &&
                (backendCapabilities is not { } caps || (caps & BitmapCodecCapabilities.WicFormatProfile) == 0))
            {
                throw new NotSupportedException(
                    $"The '{backend.Name}' imaging backend does not encode {GetType().Name} output with the WIC format profile.");
            }

            return backendCapabilities ?? BitmapCodecCapabilities.None;
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

        private bool HasFrameColorProfile()
        {
            for (var i = 0; i < Frames.Count; i++)
            {
                if (Frames[i].ColorProfile is not null)
                    return true;
            }

            return false;
        }

        private bool HasFramePalette()
        {
            for (var i = 0; i < Frames.Count; i++)
            {
                if (Frames[i].Palette is not null)
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
