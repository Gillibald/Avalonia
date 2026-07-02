using System;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Internal;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ISImage = SixLabors.ImageSharp.Image;

namespace Avalonia.Imaging.ImageSharp
{
    /// <summary>
    /// A frame transferred from the decoder's ImageSharp image into pooled native
    /// memory, with the decode plan applied - at load time where the codec fused the
    /// target size, through the shared pixel pipeline otherwise. Header data is
    /// available immediately; the transfer is paid on the first Lock.
    /// </summary>
    internal class ImageSharpBitmapFrameSource : IBitmapFrameSource, ISupportsFusedDecode
    {
        private readonly ImageSharpBitmapDecoder _owner;
        private readonly int _frameIndex;
        private readonly BitmapDecodeOptions? _options;
        private readonly object _sync = new();
        private readonly PixelSize _orientedSize;
        private readonly PixelSize _decodedSize;
        private readonly PixelRect _region;
        private readonly bool _hasRegion;
        private readonly PixelFormat _decodeFormat;
        private readonly AlphaFormat _decodeAlpha;
        private SharedBitmapMemory? _pixels;
        private int _rowBytes;
        private bool _disposed;

        private ImageSharpBitmapFrameSource(ImageSharpBitmapDecoder owner, int frameIndex,
            BitmapDecodeOptions? options)
        {
            _owner = owner;
            _frameIndex = frameIndex;
            _options = options;

            // The loaded image is already display-oriented (a stored EXIF orientation
            // is fused at load), so the plan needs no oriented-space arithmetic here.
            var image = owner.Image;

            _orientedSize = owner.OrientedSize;
            _decodedSize = new PixelSize(image.Width, image.Height);
            _region = ClampRegion(options?.SourceRegion, _orientedSize, out _hasRegion);
            _decodeFormat = TryMapPixelFormat(image) ?? PixelFormats.Rgba8888;
            _decodeAlpha = image.PixelType.AlphaRepresentation switch
            {
                PixelAlphaRepresentation.None => AlphaFormat.Opaque,
                PixelAlphaRepresentation.Associated => AlphaFormat.Premul,
                _ => AlphaFormat.Unpremul,
            };

            PixelSize = ResolveTargetSize(options?.TargetSize, _region.Size);
            PixelFormat = options?.TargetFormat ?? _decodeFormat;
            AlphaFormat = options?.TargetAlphaFormat ?? _decodeAlpha;
            Dpi = ImageSharpImagingBackend.TryGetDpi(image.Metadata) ?? new Vector(96, 96);
        }

        public PixelSize PixelSize { get; }

        public Vector Dpi { get; }

        public PixelFormat PixelFormat { get; }

        public AlphaFormat AlphaFormat { get; }

        public FusedDecodeParts FusedParts =>
            (_owner.UsedDecodeScale ? FusedDecodeParts.Scale : FusedDecodeParts.None) |
            (_owner.AppliedOrientation != PixelOrientation.Normal
                ? FusedDecodeParts.Orientation
                : FusedDecodeParts.None);

        public PixelSize GetNearestDecodeSize(PixelSize target)
        {
            // Target and result are in oriented space. The decoder resizes to the exact
            // target while loading, but only shrinking and only for formats where that
            // reduces the decode itself.
            if ((_owner.Codec.Capabilities & BitmapCodecCapabilities.FusedDecode) == 0 ||
                target.Width <= 0 || target.Height <= 0 ||
                target.Width > _orientedSize.Width || target.Height > _orientedSize.Height)
            {
                return _orientedSize;
            }

            return target;
        }

        /// <summary>
        /// Creates a frame source exposing the metadata and color-profile capabilities
        /// the frame actually carries.
        /// </summary>
        public static ImageSharpBitmapFrameSource Create(ImageSharpBitmapDecoder owner, int frameIndex,
            BitmapDecodeOptions? options)
        {
            var metadata = CreateMetadata(owner, frameIndex);
            var iccProfile = FindIccProfileBytes(owner, frameIndex);

            return (metadata, iccProfile) switch
            {
                (null, null) => new ImageSharpBitmapFrameSource(owner, frameIndex, options),
                ({ } m, null) => new FrameWithMetadata(owner, frameIndex, options, m),
                (null, { } icc) => new FrameWithColorProfile(owner, frameIndex, options, icc),
                ({ } m, { } icc) => new FrameWithMetadataAndColorProfile(owner, frameIndex, options, m, icc),
            };
        }

        public ILockedFramebuffer Lock()
        {
            lock (_sync)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(ImageSharpBitmapFrameSource));

                if (_pixels is null)
                    Decode();

                return _pixels!.CreateView(PixelSize, _rowBytes, Dpi, PixelFormat, AlphaFormat);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _pixels?.Release();
                _pixels = null;
            }

            _owner.ReleaseFrame();
        }

        internal static PixelRect ClampRegion(PixelRect? requested, PixelSize native, out bool hasRegion)
        {
            if (requested is not { } region)
            {
                hasRegion = false;
                return new PixelRect(native);
            }

            var clamped = new PixelRect(native).Intersect(region);

            if (clamped.Width <= 0 || clamped.Height <= 0)
                throw new ArgumentException("The source region does not intersect the image.");

            hasRegion = clamped != new PixelRect(native);

            return clamped;
        }

        internal static PixelSize ResolveTargetSize(PixelSize? requested, PixelSize source)
        {
            if (requested is not { } target || (target.Width <= 0 && target.Height <= 0))
                return source;

            var width = target.Width;
            var height = target.Height;

            if (width <= 0)
                width = Math.Max(1, (int)Math.Round((double)source.Width * height / source.Height));

            if (height <= 0)
                height = Math.Max(1, (int)Math.Round((double)source.Height * width / source.Width));

            return new PixelSize(width, height);
        }

        private void Decode()
        {
            _options?.Cancellation.ThrowIfCancellationRequested();

            var allocator = _owner.Allocator;
            var targetRowBytes = PixelFormatHelper.GetMinRowBytes(PixelFormat, PixelSize.Width);
            var targetMemory = allocator.Rent((long)targetRowBytes * PixelSize.Height);

            try
            {
                TransferPixels(targetMemory.Address, targetRowBytes, allocator);

                _options?.Cancellation.ThrowIfCancellationRequested();

                _pixels = new SharedBitmapMemory(targetMemory);
                _rowBytes = targetRowBytes;
            }
            catch
            {
                targetMemory.Dispose();
                throw;
            }
        }

        private void TransferPixels(IntPtr destination, int destRowBytes, IBitmapMemoryAllocator allocator)
        {
            switch (_owner.Image)
            {
                case Image<Bgra32> image:
                    Transfer(image.Frames[_frameIndex], destination, destRowBytes, allocator);
                    break;
                case Image<Rgba32> image:
                    Transfer(image.Frames[_frameIndex], destination, destRowBytes, allocator);
                    break;
                case Image<Rgb24> image:
                    Transfer(image.Frames[_frameIndex], destination, destRowBytes, allocator);
                    break;
                case Image<Bgr24> image:
                    Transfer(image.Frames[_frameIndex], destination, destRowBytes, allocator);
                    break;
                case Image<L8> image:
                    Transfer(image.Frames[_frameIndex], destination, destRowBytes, allocator);
                    break;
                case Image<L16> image:
                    Transfer(image.Frames[_frameIndex], destination, destRowBytes, allocator);
                    break;
                case Image<Rgba64> image:
                    Transfer(image.Frames[_frameIndex], destination, destRowBytes, allocator);
                    break;
                default:
                    // Exotic pixel types funnel through a canonical clone.
                    using (var clone = _owner.Image.CloneAs<Rgba32>(ImageSharpImagingBackend.ImagingConfiguration))
                        Transfer(clone.Frames[_frameIndex], destination, destRowBytes, allocator);
                    break;
            }
        }

        private unsafe void Transfer<TPixel>(ImageFrame<TPixel> frame, IntPtr destination,
            int destRowBytes, IBitmapMemoryAllocator allocator)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            var isPlanOutput = !_hasRegion &&
                _decodedSize == PixelSize &&
                PixelFormat == _decodeFormat &&
                AlphaFormat == _decodeAlpha;

            if (isPlanOutput)
            {
                var byteCount = (int)checked((long)destRowBytes * PixelSize.Height);

                frame.CopyPixelDataTo(new Span<byte>((void*)destination, byteCount));

                return;
            }

            var sourceRowBytes = frame.Width * sizeof(TPixel);

            if (frame.DangerousTryGetSinglePixelMemory(out var memory))
            {
                using var pinned = memory.Pin();

                RunPipeline(new IntPtr(pinned.Pointer), sourceRowBytes, destination, destRowBytes, allocator);
            }
            else
            {
                // The frame exceeded the contiguous buffer limit: stage a contiguous
                // copy, then run the plan over it.
                var sourceByteCount = checked((long)sourceRowBytes * frame.Height);
                var staging = allocator.Rent(sourceByteCount);

                try
                {
                    frame.CopyPixelDataTo(new Span<byte>((void*)staging.Address, (int)sourceByteCount));

                    RunPipeline(staging.Address, sourceRowBytes, destination, destRowBytes, allocator);
                }
                finally
                {
                    staging.Dispose();
                }
            }
        }

        private void RunPipeline(IntPtr sourceAddress, int sourceRowBytes, IntPtr destination,
            int destRowBytes, IBitmapMemoryAllocator allocator)
        {
            var source = new LockedFramebuffer(sourceAddress, _decodedSize, sourceRowBytes,
                Dpi, _decodeFormat, _decodeAlpha, null);

            // The decoded image is already display-oriented, so the plan orientation
            // stays Normal. A region is only decoded at full oriented size, so its
            // coordinates are valid; without a region the plan covers the whole
            // (possibly scaled) decode.
            var planRegion = _hasRegion ? _region : new PixelRect(_decodedSize);

            var plan = new FusedPlanExecution(planRegion, PixelSize, PixelFormat, AlphaFormat,
                _options?.Interpolation ?? BitmapInterpolationMode.HighQuality);

            FusedPixelPipeline.Run(source, plan, destination, destRowBytes, allocator,
                _options?.Cancellation ?? default);
        }

        private static PixelFormat? TryMapPixelFormat(ISImage image) => image switch
        {
            Image<Bgra32> => PixelFormats.Bgra8888,
            Image<Rgba32> => PixelFormats.Rgba8888,
            Image<Rgb24> => PixelFormats.Rgb24,
            Image<Bgr24> => PixelFormats.Bgr24,
            Image<L8> => PixelFormats.Gray8,
            Image<L16> => PixelFormats.Gray16,
            Image<Rgba64> => PixelFormats.Rgba64,
            _ => null,
        };

        private static BitmapMetadata? CreateMetadata(ImageSharpBitmapDecoder owner, int frameIndex)
        {
            if ((owner.Codec.Capabilities & BitmapCodecCapabilities.Metadata) == 0)
                return null;

            var image = owner.Image;

            if (owner.Codec.ContainerFormat == BitmapContainerFormats.Png)
            {
                var pngMetadata = image.Metadata.GetPngMetadata();

                return pngMetadata.TextData.Count > 0
                    ? new ImageSharpPngTextMetadata(pngMetadata.TextData)
                    : null;
            }

            var frameMetadata = image.Frames[frameIndex].Metadata;
            var exif = frameMetadata.ExifProfile ?? image.Metadata.ExifProfile;
            var xmp = frameMetadata.XmpProfile ?? image.Metadata.XmpProfile;
            var icc = frameMetadata.IccProfile ?? image.Metadata.IccProfile;

            if (exif is null && xmp is null && icc is null)
                return null;

            // Deep copies detach the metadata from the decoder's image lifetime.
            return new ImageSharpExifMetadata(owner.Codec.FormatName,
                exif?.DeepClone(), xmp?.DeepClone(), icc?.DeepClone());
        }

        private static byte[]? FindIccProfileBytes(ImageSharpBitmapDecoder owner, int frameIndex)
        {
            if ((owner.Codec.Capabilities & BitmapCodecCapabilities.ColorProfile) == 0)
                return null;

            var image = owner.Image;
            var profile = image.Frames[frameIndex].Metadata.IccProfile ?? image.Metadata.IccProfile;

            return profile?.ToByteArray();
        }

        private sealed class FrameWithMetadata : ImageSharpBitmapFrameSource, IMetadataSource
        {
            public FrameWithMetadata(ImageSharpBitmapDecoder owner, int frameIndex,
                BitmapDecodeOptions? options, BitmapMetadata metadata)
                : base(owner, frameIndex, options)
            {
                Metadata = metadata;
            }

            public BitmapMetadata Metadata { get; }
        }

        private sealed class FrameWithColorProfile : ImageSharpBitmapFrameSource, IColorProfileSource
        {
            public FrameWithColorProfile(ImageSharpBitmapDecoder owner, int frameIndex,
                BitmapDecodeOptions? options, byte[] iccProfile)
                : base(owner, frameIndex, options)
            {
                IccProfile = iccProfile;
            }

            public ReadOnlyMemory<byte> IccProfile { get; }
        }

        private sealed class FrameWithMetadataAndColorProfile : ImageSharpBitmapFrameSource,
            IMetadataSource, IColorProfileSource
        {
            public FrameWithMetadataAndColorProfile(ImageSharpBitmapDecoder owner, int frameIndex,
                BitmapDecodeOptions? options, BitmapMetadata metadata, byte[] iccProfile)
                : base(owner, frameIndex, options)
            {
                Metadata = metadata;
                IccProfile = iccProfile;
            }

            public BitmapMetadata Metadata { get; }

            public ReadOnlyMemory<byte> IccProfile { get; }
        }
    }
}
