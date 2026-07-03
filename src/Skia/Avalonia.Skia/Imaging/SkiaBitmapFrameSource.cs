using System;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Internal;
using SkiaSharp;

namespace Avalonia.Skia.Imaging
{
    /// <summary>
    /// A frame decoded through SKCodec into pooled native memory, with the decode plan
    /// applied - natively where SKCodec can scale, through the shared pixel pipeline
    /// otherwise. Header data is available immediately; pixels decode on the first Lock.
    /// </summary>
    internal sealed class SkiaBitmapFrameSource : IBitmapFrameSource, ISupportsFusedDecode
    {
        private readonly SkiaBitmapDecoder _owner;
        private readonly BitmapDecodeOptions? _options;
        private readonly object _sync = new();
        private readonly PixelSize _rawSize;
        private readonly PixelSize _orientedSize;
        private readonly PixelOrientation _orientation;
        private readonly PixelRect _region;
        private readonly bool _hasRegion;
        private readonly AlphaFormat _decodeAlpha;
        private SharedBitmapMemory? _pixels;
        private int _rowBytes;
        private bool _disposed;

        public SkiaBitmapFrameSource(SkiaBitmapDecoder owner, BitmapDecodeOptions? options)
        {
            _owner = owner;
            _options = options;

            var info = owner.Codec.Info;

            _rawSize = new PixelSize(info.Width, info.Height);
            _orientation = options?.RespectExifOrientation ?? true
                ? ToPixelOrientation(owner.Codec.EncodedOrigin)
                : PixelOrientation.Normal;
            _orientedSize = FusedPixelPipeline.GetOrientedSize(_rawSize, _orientation);

            // The plan is expressed in oriented (display) space.
            _region = ClampRegion(options?.SourceRegion, _orientedSize, out _hasRegion);
            _decodeAlpha = info.AlphaType == SKAlphaType.Opaque ? AlphaFormat.Opaque : AlphaFormat.Premul;

            PixelSize = ResolveTargetSize(options?.TargetSize, _region.Size);
            PixelFormat = options?.TargetFormat ?? PixelFormats.Bgra8888;
            AlphaFormat = options?.TargetAlphaFormat ?? _decodeAlpha;
        }

        public PixelSize PixelSize { get; }

        // SKCodec exposes no DPI.
        public Vector Dpi => new(96, 96);

        public PixelFormat PixelFormat { get; }

        public AlphaFormat AlphaFormat { get; }

        public FusedDecodeParts FusedParts =>
            UsesFusedScale(out _) ? FusedDecodeParts.Scale : FusedDecodeParts.None;

        public PixelSize GetNearestDecodeSize(PixelSize target)
        {
            // Target and result are in oriented space; the codec scales the raw image.
            var scale = Math.Min(1f, Math.Max(
                (float)target.Width / _orientedSize.Width,
                (float)target.Height / _orientedSize.Height));

            var nearest = _owner.Codec.GetScaledDimensions(scale);

            return FusedPixelPipeline.GetOrientedSize(new PixelSize(nearest.Width, nearest.Height), _orientation);
        }

        public ILockedFramebuffer Lock()
        {
            lock (_sync)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(SkiaBitmapFrameSource));

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

        private void Decode()
        {
            _options?.CancellationToken.ThrowIfCancellationRequested();

            // Bounds peak native memory when many frames decode in parallel.
            using var slot = DecodeConcurrencyLimiter.Enter(_options?.CancellationToken ?? default);

            var allocator = _owner.Allocator;
            var decodeRawSize = UsesFusedScale(out var nearestRaw) ? nearestRaw : _rawSize;

            var decodeInfo = new SKImageInfo(decodeRawSize.Width, decodeRawSize.Height,
                SKColorType.Bgra8888, _decodeAlpha.ToSkAlphaType());
            var decodeRowBytes = decodeInfo.RowBytes;

            var decodeMemory = allocator.Rent((long)decodeRowBytes * decodeRawSize.Height);

            try
            {
                var result = _owner.Codec.GetPixels(decodeInfo, decodeMemory.Address);

                if (result != SKCodecResult.Success)
                {
                    throw new InvalidDataException(
                        $"Decoding failed: {result}.");
                }

                _options?.CancellationToken.ThrowIfCancellationRequested();

                var isPlanOutput = !_hasRegion &&
                    _orientation == PixelOrientation.Normal &&
                    decodeRawSize == PixelSize &&
                    PixelFormat == PixelFormats.Bgra8888 &&
                    AlphaFormat == _decodeAlpha;

                if (isPlanOutput)
                {
                    _pixels = new SharedBitmapMemory(decodeMemory);
                    _rowBytes = decodeRowBytes;

                    return;
                }

                var targetRowBytes = PixelFormatHelper.GetMinRowBytes(PixelFormat, PixelSize.Width);
                var targetMemory = allocator.Rent((long)targetRowBytes * PixelSize.Height);

                try
                {
                    var source = new LockedFramebuffer(decodeMemory.Address, decodeRawSize,
                        decodeRowBytes, Dpi, PixelFormats.Bgra8888, _decodeAlpha, null);

                    // The plan region is in oriented space: with an explicit region the
                    // decode ran at raw full size (its oriented space is _orientedSize);
                    // without one the plan covers the whole, possibly scaled, decode.
                    var planRegion = _hasRegion
                        ? _region
                        : new PixelRect(FusedPixelPipeline.GetOrientedSize(decodeRawSize, _orientation));

                    var plan = new FusedPlanExecution(planRegion, PixelSize, PixelFormat,
                        AlphaFormat, _options?.Interpolation ?? BitmapInterpolationMode.HighQuality,
                        _orientation);

                    FusedPixelPipeline.Run(source, plan, targetMemory.Address, targetRowBytes,
                        allocator, _options?.CancellationToken ?? default);

                    _pixels = new SharedBitmapMemory(targetMemory);
                    _rowBytes = targetRowBytes;
                }
                catch
                {
                    targetMemory.Dispose();
                    throw;
                }
            }
            finally
            {
                // When the decode buffer did not become the frame's pixels, return it.
                if (_pixels is null || _rowBytes != decodeRowBytes || _pixels.Address != decodeMemory.Address)
                    decodeMemory.Dispose();
            }
        }

        private bool UsesFusedScale(out PixelSize nearestRaw)
        {
            nearestRaw = _rawSize;

            if (_hasRegion || _options?.TargetSize is null || PixelSize == _orientedSize)
                return false;

            if ((SkiaCodecCatalog.FromEncodedFormat(_owner.Codec.EncodedFormat)?.Capabilities
                 & BitmapCodecCapabilities.FusedDecode) == 0)
                return false;

            var scale = Math.Min(1f, Math.Max(
                (float)PixelSize.Width / _orientedSize.Width,
                (float)PixelSize.Height / _orientedSize.Height));

            var candidateRaw = _owner.Codec.GetScaledDimensions(scale);
            var candidateOriented = FusedPixelPipeline.GetOrientedSize(
                new PixelSize(candidateRaw.Width, candidateRaw.Height), _orientation);

            // Only decode reduced when the nearest size still covers the target on both
            // axes; the pipeline shrinks the remainder, it should not enlarge.
            if (candidateOriented.Width < PixelSize.Width || candidateOriented.Height < PixelSize.Height ||
                new PixelSize(candidateRaw.Width, candidateRaw.Height) == _rawSize)
                return false;

            nearestRaw = new PixelSize(candidateRaw.Width, candidateRaw.Height);

            return true;
        }

        private static PixelOrientation ToPixelOrientation(SKEncodedOrigin origin)
        {
            // SKEncodedOrigin values match the EXIF orientation numbering 1-8 exactly.
            var value = (int)origin;

            return value is >= 1 and <= 8 ? (PixelOrientation)value : PixelOrientation.Normal;
        }

        private static PixelRect ClampRegion(PixelRect? requested, PixelSize native, out bool hasRegion)
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

        private static PixelSize ResolveTargetSize(PixelSize? requested, PixelSize source)
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
    }
}
