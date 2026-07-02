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
        private readonly PixelSize _nativeSize;
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

            _nativeSize = new PixelSize(info.Width, info.Height);
            _region = ClampRegion(options?.SourceRegion, _nativeSize, out _hasRegion);
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
            var scale = Math.Min(1f, Math.Max(
                (float)target.Width / _nativeSize.Width,
                (float)target.Height / _nativeSize.Height));

            var nearest = _owner.Codec.GetScaledDimensions(scale);

            return new PixelSize(nearest.Width, nearest.Height);
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
            _options?.Cancellation.ThrowIfCancellationRequested();

            var allocator = _owner.Allocator;
            var decodeSize = UsesFusedScale(out var nearest) ? nearest : _nativeSize;

            var decodeInfo = new SKImageInfo(decodeSize.Width, decodeSize.Height,
                SKColorType.Bgra8888, _decodeAlpha.ToSkAlphaType());
            var decodeRowBytes = decodeInfo.RowBytes;

            var decodeMemory = allocator.Rent((long)decodeRowBytes * decodeSize.Height);

            try
            {
                var result = _owner.Codec.GetPixels(decodeInfo, decodeMemory.Address);

                if (result != SKCodecResult.Success)
                {
                    throw new InvalidDataException(
                        $"Decoding failed: {result}.");
                }

                _options?.Cancellation.ThrowIfCancellationRequested();

                var isPlanOutput = !_hasRegion &&
                    decodeSize == PixelSize &&
                    PixelFormat == PixelFormats.Bgra8888 &&
                    AlphaFormat == _decodeAlpha;

                if (isPlanOutput)
                {
                    _pixels = new SharedBitmapMemory(decodeMemory);
                    _rowBytes = decodeRowBytes;

                    return;
                }

                var targetRowBytes = (PixelSize.Width * PixelFormat.BitsPerPixel + 7) / 8;
                var targetMemory = allocator.Rent((long)targetRowBytes * PixelSize.Height);

                try
                {
                    var source = new LockedFramebuffer(decodeMemory.Address, decodeSize,
                        decodeRowBytes, Dpi, PixelFormats.Bgra8888, _decodeAlpha, null);

                    // A region is decoded at native size, so its coordinates are valid;
                    // without a region the plan covers the whole (possibly scaled) decode.
                    var planRegion = _hasRegion ? _region : new PixelRect(decodeSize);

                    var plan = new FusedPlanExecution(planRegion, PixelSize, PixelFormat,
                        AlphaFormat, _options?.Interpolation ?? BitmapInterpolationMode.HighQuality);

                    FusedPixelPipeline.Run(source, plan, targetMemory.Address, targetRowBytes, allocator);

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

        private bool UsesFusedScale(out PixelSize nearest)
        {
            nearest = _nativeSize;

            if (_hasRegion || _options?.TargetSize is null || PixelSize == _nativeSize)
                return false;

            if ((SkiaCodecCatalog.FromEncodedFormat(_owner.Codec.EncodedFormat)?.Capabilities
                 & BitmapCodecCapabilities.FusedDecode) == 0)
                return false;

            var candidate = GetNearestDecodeSize(PixelSize);

            // Only decode reduced when the nearest size still covers the target on both
            // axes; the pipeline shrinks the remainder, it should not enlarge.
            if (candidate.Width < PixelSize.Width || candidate.Height < PixelSize.Height ||
                candidate == _nativeSize)
                return false;

            nearest = candidate;

            return true;
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
