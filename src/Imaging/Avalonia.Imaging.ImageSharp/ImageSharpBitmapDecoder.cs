using System;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Color = Avalonia.Media.Color;
using ISColor = SixLabors.ImageSharp.Color;
using ISImage = SixLabors.ImageSharp.Image;

namespace Avalonia.Imaging.ImageSharp
{
    /// <summary>
    /// Decoder over an eagerly loaded ImageSharp image. Frames are random access; the
    /// image stays alive until both the decoder and every frame produced by it are
    /// disposed.
    /// </summary>
    internal sealed class ImageSharpBitmapDecoder : IBitmapDecoder, ISeekableFrameDecoder, IPaletteDecoder,
        IMetadataStoreSource
    {
        private readonly ISImage _image;
        private readonly BitmapDecodeOptions? _options;
        private readonly object _lock = new();
        private int _outstandingFrames;
        private bool _disposeRequested;
        private bool _disposed;
        private int _cursor;

        public ImageSharpBitmapDecoder(ISImage image, ImageSharpBitmapCodecInfo codecInfo,
            BitmapImageInfo headerInfo, BitmapDecodeOptions? options,
            IBitmapMemoryAllocator allocator, PixelOrientation appliedOrientation,
            bool usedDecodeScale)
        {
            _image = image;
            _options = options;
            Codec = codecInfo;
            Info = headerInfo;
            Allocator = allocator;
            AppliedOrientation = appliedOrientation;
            UsedDecodeScale = usedDecodeScale;
            FrameCount = image.Frames.Count;
            GlobalPalette = codecInfo.ContainerFormat == BitmapContainerFormats.Gif
                ? ToPalette(image.Metadata.GetGifMetadata().GlobalColorTable)
                : null;
            Metadata = new ImageSharpMetadataStore(null, null, null, null, isReadOnly: true,
                blocks: codecInfo.ContainerFormat == BitmapContainerFormats.Gif ? GifBlocks.ForContainer(image) : null);
        }

        public BitmapPalette? GlobalPalette { get; }

        /// <summary>
        /// Gets the container-level metadata: the GIF logical screen, application and comment
        /// blocks. Other formats keep their metadata on the frames, so their store is empty.
        /// </summary>
        public IBitmapMetadataStore Metadata { get; }

        public IBitmapCodecInfo CodecInfo => Codec;

        private static BitmapPalette? ToPalette(ReadOnlyMemory<ISColor>? table)
        {
            if (table is not { Length: > 0 } colors)
                return null;

            var entries = new Color[colors.Length];

            for (var i = 0; i < entries.Length; i++)
            {
                var pixel = colors.Span[i].ToPixel<Rgba32>();

                entries[i] = Color.FromArgb(pixel.A, pixel.R, pixel.G, pixel.B);
            }

            return new BitmapPalette(entries);
        }

        public BitmapImageInfo Info { get; }

        /// <inheritdoc cref="ISeekableFrameDecoder.FrameCount" />
        public int FrameCount { get; }

        int? IBitmapDecoder.FrameCount => FrameCount;

        internal ImageSharpBitmapCodecInfo Codec { get; }

        internal ISImage Image => _image;

        internal IBitmapMemoryAllocator Allocator { get; }

        /// <summary>
        /// Gets the raw source size before the decode plan, as declared by the header.
        /// </summary>
        internal PixelSize NativeSize => Info.PixelSize;

        /// <summary>
        /// Gets the EXIF orientation fused into the loaded image; Normal when the file
        /// carries none or the plan disabled it. The image pixels are already oriented.
        /// </summary>
        internal PixelOrientation AppliedOrientation { get; }

        /// <summary>
        /// Gets the display-space source size (<see cref="NativeSize"/> with
        /// <see cref="AppliedOrientation"/> applied); the decode plan is expressed in it.
        /// </summary>
        internal PixelSize OrientedSize =>
            FusedPixelPipeline.GetOrientedSize(NativeSize, AppliedOrientation);

        /// <summary>
        /// Gets whether the image was loaded with a decode-time target size, so it is
        /// already scaled relative to <see cref="OrientedSize"/>.
        /// </summary>
        internal bool UsedDecodeScale { get; }

        public IBitmapFrameSource? ReadNextFrame()
        {
            lock (_lock)
            {
                if (_disposed || _disposeRequested)
                    throw new ObjectDisposedException(nameof(ImageSharpBitmapDecoder));

                if (_cursor >= FrameCount)
                    return null;

                var frame = ImageSharpBitmapFrameSource.Create(this, _cursor, _options);

                _cursor++;
                _outstandingFrames++;

                return frame;
            }
        }

        public IBitmapFrameSource GetFrame(int index)
        {
            lock (_lock)
            {
                if (_disposed || _disposeRequested)
                    throw new ObjectDisposedException(nameof(ImageSharpBitmapDecoder));

                if (index < 0 || index >= FrameCount)
                    throw new ArgumentOutOfRangeException(nameof(index));

                var frame = ImageSharpBitmapFrameSource.Create(this, index, _options);

                _outstandingFrames++;

                return frame;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed || _disposeRequested)
                    return;

                _disposeRequested = true;

                if (_outstandingFrames == 0)
                    DisposeCore();
            }
        }

        internal void ReleaseFrame()
        {
            lock (_lock)
            {
                _outstandingFrames--;

                if (_outstandingFrames == 0 && _disposeRequested && !_disposed)
                    DisposeCore();
            }
        }

        private void DisposeCore()
        {
            _disposed = true;
            _image.Dispose();
        }
    }
}
