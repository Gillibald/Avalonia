using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Avalonia.Media.Imaging;
using Avalonia.PixelMan;
using Avalonia.Platform;

namespace Avalonia.Imaging.PixelMan
{
    /// <summary>
    /// Encodes through PixelMan. Each frame wraps the pixels the encoder resolved, without
    /// a copy; palettes, frame origins, timing and colour profiles travel with the frames.
    /// </summary>
    /// <remarks>
    /// Only GIF has file-level metadata in PixelMan, so for the other formats the encoder's
    /// metadata is written as the first frame's.
    /// </remarks>
    internal sealed class PixelManBitmapEncoderImpl : IBitmapEncoderImpl
    {
        private readonly PixelManCodecInfo _codec;

        public PixelManBitmapEncoderImpl(PixelManCodecInfo codec)
        {
            _codec = codec;
        }

        public void Encode(BitmapEncoder encoder, Stream stream, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var owners = new List<IDisposable>();
            var frames = new List<PmBitmapFrame>();
            var isGif = _codec.Kind == PmCodecKind.Gif;

            try
            {
                for (var i = 0; i < encoder.Frames.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var frame = CreateFrame(encoder, i, owners);

                    frames.Add(frame);
                    ApplyFrameSettings(encoder, i, frame, isGif);
                }

                var options = CreateOptions(encoder);
                var container = isGif && encoder.Metadata is { } metadata
                    ? PixelManMetadataStore.CopyOf(metadata.Store, _codec.ContainerFormat).Metadata
                    : null;

                if (frames.Count == 1 && !isGif)
                {
                    frames[0].Encode(stream, _codec.Kind, options, container, cancellationToken);
                    return;
                }

                (int, int)? screen = encoder is GifBitmapEncoder gif
                    ? (gif.GetScreenSize().Width, gif.GetScreenSize().Height)
                    : null;

                using var image = PmImageEncoder.Create(stream, _codec.Kind, options, container, screen, cancellationToken);

                foreach (var frame in frames)
                    image.AddFrame(frame, cancellationToken);

                image.Commit(cancellationToken);
            }
            catch (Exception exception) when (PixelManFormats.Translate(exception) is var translated && translated != exception)
            {
                throw translated;
            }
            finally
            {
                // The frames borrow the views' memory, so they go before the views do.
                foreach (var frame in frames)
                    frame.Dispose();

                for (var i = owners.Count - 1; i >= 0; i--)
                    owners[i].Dispose();
            }
        }

        private PmBitmapFrame CreateFrame(BitmapEncoder encoder, int index, List<IDisposable> owners)
        {
            ILockedFramebuffer view = Own(encoder.LockFramePixels(index, out var palette), owners);

            // PixelMan has no transform-on-encode, so the shared software path applies it.
            if (EncoderTransform.Apply(view, encoder.Transform) is { } transformed)
                view = Own(transformed.Lock(), owners);

            if (PixelManFormats.ToPixelMan(view.Format, view.AlphaFormat) is not { } format)
            {
                var converted = PixelBuffer.CopyFrom(view, palette).Convert(PixelFormats.Bgra8888, AlphaFormat.Unpremul);

                view = Own(converted.Lock(), owners);
                format = PmPixelFormat.Bgra32;
                palette = null;
            }

            var pmPalette = palette is not null && format.IsIndexed ? Own(PixelManFormats.ToPixelMan(palette), owners) : null;
            var stride = (nuint)view.RowBytes;

            var frame = PmBitmapFrame.CreateFromOwned(view.Address, format, (uint)view.Size.Width, (uint)view.Size.Height,
                stride, stride * (nuint)view.Size.Height, pmPalette, static () => { });

            if (view.Dpi.X > 0 && view.Dpi.Y > 0)
                frame.Dpi = (view.Dpi.X, view.Dpi.Y);

            return frame;
        }

        private void ApplyFrameSettings(BitmapEncoder encoder, int index, PmBitmapFrame frame, bool isGif)
        {
            var source = encoder.Frames[index];

            if ((source.ColorProfile ?? encoder.ColorProfile) is { } profile)
                frame.IccProfile = profile.ToArray();

            // Frames take their own metadata; for formats without file-level metadata the
            // encoder's metadata belongs to the first frame.
            var metadata = source.Metadata ?? (!isGif && index == 0 ? encoder.Metadata : null);
            var store = metadata is not null
                ? PixelManMetadataStore.CopyOf(metadata.Store, _codec.ContainerFormat)
                : isGif ? new PixelManMetadataStore(PmMetadata.Create(), _codec.ContainerFormat, null, false) : null;

            if (store is null)
                return;

            if (isGif)
            {
                var bag = store.Metadata;

                bag.FrameDelay = (ushort)Math.Round(source.Delay.TotalMilliseconds / 10, MidpointRounding.AwayFromZero);
                bag.DisposalMethod = (byte)source.Disposal;
                bag.FrameRectangle = new PmGifFrameRectangle((ushort)source.Origin.X, (ushort)source.Origin.Y,
                    (ushort)frame.Width, (ushort)frame.Height);
            }

            frame.SetMetadata(store.Metadata);
        }

        private static PmEncodeOptions CreateOptions(BitmapEncoder encoder)
        {
            var options = new PmEncodeOptions(PmCompatibilityMode.Normal);

            switch (encoder)
            {
                case PngBitmapEncoder png:
                    options.PngCompressionLevel = (uint)Math.Clamp(png.CompressionLevel, 0, 9);
                    options.PngInterlace = png.Interlace == PngInterlaceMode.Adam7;
                    options.PngFilter = png.Filter switch
                    {
                        PngFilterMode.None => PmPngFilter.None,
                        PngFilterMode.Sub => PmPngFilter.Sub,
                        PngFilterMode.Up => PmPngFilter.Up,
                        PngFilterMode.Average => PmPngFilter.Average,
                        PngFilterMode.Paeth => PmPngFilter.Paeth,
                        _ => PmPngFilter.Auto,
                    };
                    break;
                case JpegBitmapEncoder jpeg:
                    options.JpegQuality = (uint)Math.Clamp(jpeg.Quality, 1, 100);
                    break;
                case TiffBitmapEncoder tiff:
                    options.TiffCompression = tiff.Compression switch
                    {
                        TiffCompression.None => PmTiffCompression.None,
                        TiffCompression.Lzw => PmTiffCompression.Lzw,
                        TiffCompression.Deflate => PmTiffCompression.Zip,
                        _ => throw new NotSupportedException(
                            $"The '{PixelManFormats.BackendName}' imaging backend does not write {tiff.Compression} TIFF compression."),
                    };
                    break;
                case GifBitmapEncoder gif:
                    options.GifLoopCount = (ushort)gif.LoopCount;
                    break;
            }

            return options;
        }

        private static T Own<T>(T value, List<IDisposable> owners) where T : IDisposable
        {
            owners.Add(value);
            return value;
        }
    }
}
