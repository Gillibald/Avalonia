using System;
using Avalonia.Media.Imaging;
using Avalonia.PixelMan;
using Avalonia.Platform;
using Avalonia.Platform.Internal;

namespace Avalonia.Imaging.PixelMan
{
    /// <summary>
    /// A frame PixelMan has decoded into its stored layout. The first Lock copies it into
    /// pooled memory, through the shared pixel pipeline when the decode plan crops, scales,
    /// orients or converts it; a frame PixelMan decoded into a pool rental whose layout is
    /// already the plan's output is installed as it is. EXIF orientation is applied by the
    /// pipeline: PixelMan never rotates.
    /// </summary>
    internal class PixelManBitmapFrameSource : IBitmapFrameSource, IColorProfileSource, IMetadataStoreSource
    {
        private readonly PmBitmapFrame _frame;
        private readonly BitmapDecodeOptions? _options;
        private readonly IBitmapMemoryAllocator _allocator;
        private readonly object _sync = new();
        private readonly PixelSize _rawSize;
        private readonly PixelOrientation _orientation;
        private readonly PixelRect _region;
        private readonly bool _hasRegion;
        private readonly bool _mapped;
        private readonly PixelFormat _decodeFormat;
        private readonly AlphaFormat _decodeAlpha;
        private SharedBitmapMemory? _pixels;
        private int _rowBytes;
        private bool _disposed;

        protected PixelManBitmapFrameSource(PmBitmapFrame frame, PixelManMetadataStore metadata,
            BitmapDecodeOptions? options, IBitmapMemoryAllocator allocator, PixelOrientation orientation)
        {
            _frame = frame;
            _options = options;
            _allocator = allocator;
            _rawSize = new PixelSize((int)frame.Width, (int)frame.Height);
            _orientation = orientation;
            _mapped = PixelManFormats.TryMap(frame.Format, out _decodeFormat, out _decodeAlpha);

            if (!_mapped)
                PixelManFormats.TryMap(PixelManFormats.GetFallback(frame.Format), out _decodeFormat, out _decodeAlpha);

            var orientedSize = FusedPixelPipeline.GetOrientedSize(_rawSize, orientation);

            _region = ClampRegion(options?.SourceRegion, orientedSize, out _hasRegion);

            PixelSize = ResolveTargetSize(options?.TargetSize, _region.Size);

            var identity = !_hasRegion && orientation == PixelOrientation.Normal && PixelSize == _rawSize;

            // An indexed frame keeps its indices only when nothing resamples or reorders it;
            // any plan works on colours, so the pipeline expands the palette first.
            PixelFormat = options?.TargetFormat ?? (_decodeFormat.IsIndexed && !identity ? PixelFormats.Bgra8888 : _decodeFormat);
            AlphaFormat = options?.TargetAlphaFormat ??
                (PixelFormat.IsIndexed || PixelFormat == _decodeFormat ? _decodeAlpha
                    : PixelFormat.HasAlpha ? AlphaFormat.Unpremul : AlphaFormat.Opaque);

            Dpi = frame.Dpi is { } dpi ? new Vector(dpi.Item1, dpi.Item2) : new Vector(96, 96);
            Metadata = metadata;
            IccProfile = frame.IccProfile ?? Array.Empty<byte>();
        }

        public PixelSize PixelSize { get; }

        public Vector Dpi { get; }

        public PixelFormat PixelFormat { get; }

        public AlphaFormat AlphaFormat { get; }

        public IBitmapMetadataStore Metadata { get; }

        public ReadOnlyMemory<byte> IccProfile { get; }

        protected PmBitmapFrame Frame => _frame;

        /// <summary>
        /// Creates the frame source with the capabilities the frame's output has: a palette
        /// for indexed output, animation data for GIF frames.
        /// </summary>
        public static PixelManBitmapFrameSource Create(PmBitmapFrame frame, PixelManCodecInfo codec,
            BitmapDecodeOptions? options, IBitmapMemoryAllocator allocator)
        {
            var metadata = frame.GetMetadata();
            var orientation = PixelOrientation.Normal;

            if ((options?.RespectExifOrientation ?? true) && metadata.Orientation is { } value and >= 1 and <= 8)
                orientation = (PixelOrientation)value;

            var store = new PixelManMetadataStore(metadata, codec.ContainerFormat, frame.IccProfile, isReadOnly: true);

            PixelManBitmapFrameSource source = new PixelManBitmapFrameSource(frame, store, options, allocator, orientation);

            if (!source.PixelFormat.IsIndexed || frame.GetPalette() is not { } palette)
                return codec.Kind == PmCodecKind.Gif ? new PixelManGifColorFrameSource(frame, store, options, allocator) : source;

            var converted = PixelManFormats.ToPalette(palette);

            palette.Dispose();

            return codec.Kind == PmCodecKind.Gif
                ? new PixelManGifFrameSource(frame, store, options, allocator, converted)
                : new PixelManIndexedFrameSource(frame, store, options, allocator, converted);
        }

        public ILockedFramebuffer Lock()
        {
            lock (_sync)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(PixelManBitmapFrameSource));

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

            _frame.Dispose();
        }

        private void Decode()
        {
            _options?.CancellationToken.ThrowIfCancellationRequested();

#if PIXELMAN_FRAME_ALLOCATOR
            if (IsIdentityPlan && TryAdoptFrame())
                return;
#endif

            var targetRowBytes = PixelFormatArithmetic.GetMinRowBytes(PixelFormat, PixelSize.Width);
            var target = _allocator.Rent((long)targetRowBytes * PixelSize.Height);

            try
            {
                RunPlan(target.Address, targetRowBytes);

                _options?.CancellationToken.ThrowIfCancellationRequested();

                _pixels = new SharedBitmapMemory(target);
                _rowBytes = targetRowBytes;
            }
            catch
            {
                target.Dispose();
                throw;
            }
        }

        /// <summary>Whether the decoded layout is already the plan's output.</summary>
        private bool IsIdentityPlan =>
            _mapped && !_hasRegion && _orientation == PixelOrientation.Normal && PixelSize == _rawSize &&
            PixelFormat == _decodeFormat && (AlphaFormat == _decodeAlpha || !PixelFormat.HasAlpha);

#if PIXELMAN_FRAME_ALLOCATOR
        // The frame lives in a pool rental, so installing it skips the copy and pool
        // accounting still covers it. The lock keeps PixelMan's frame, and with it the
        // rental, alive until the last view is released.
        private bool TryAdoptFrame()
        {
            if (_allocator is not PixelManFrameAllocator frames)
                return false;

            var pixels = _frame.Lock(PmLockMode.ReadWrite);

            if (!frames.TryClaim(pixels.Scan0, out var rental))
            {
                pixels.Dispose();
                return false;
            }

            _pixels = new SharedBitmapMemory(new FrameMemory(rental, pixels));
            _rowBytes = checked((int)pixels.Stride);
            return true;
        }

        private sealed class FrameMemory(IBitmapMemory rental, PmBitmapLock pixels) : IBitmapMemory
        {
            public IntPtr Address => rental.Address;

            public long ByteCount => rental.ByteCount;

            public void Dispose() => pixels.Dispose();
        }
#endif

        private void RunPlan(IntPtr destination, int destRowBytes)
        {
            // The decoded layout is the plan's output: PixelMan copies it into the pooled
            // destination, so pool accounting covers every installed frame.
            if (IsIdentityPlan)
            {
                unsafe
                {
                    _frame.CopyPixels(_frame.Format, 0, 0, 0, 0,
                        new Span<byte>((void*)destination, checked(destRowBytes * PixelSize.Height)), (nuint)destRowBytes,
                        _options?.CancellationToken ?? default);
                }

                return;
            }

            var plan = new FusedPlanExecution(_region, PixelSize, PixelFormat, AlphaFormat,
                _options?.Interpolation ?? BitmapInterpolationMode.HighQuality, _orientation);

            // Indexed frames and layouts Avalonia has no format for are converted by PixelMan
            // first; the pipeline then works on the converted copy.
            if (!_mapped || _decodeFormat.IsIndexed)
            {
                var format = _mapped ? PmPixelFormat.Bgra32 : PixelManFormats.GetFallback(_frame.Format);

                PixelManFormats.TryMap(format, out var stagedFormat, out var stagedAlpha);

                var rowBytes = PixelFormatArithmetic.GetMinRowBytes(stagedFormat, _rawSize.Width);

                using var staging = _allocator.Rent((long)rowBytes * _rawSize.Height);

                unsafe
                {
                    _frame.CopyPixels(format, 0, 0, 0, 0,
                        new Span<byte>((void*)staging.Address, checked(rowBytes * _rawSize.Height)), (nuint)rowBytes,
                        _options?.CancellationToken ?? default);
                }

                RunPipeline(new LockedFramebuffer(staging.Address, _rawSize, rowBytes, Dpi, stagedFormat, stagedAlpha, null),
                    plan, destination, destRowBytes);
                return;
            }

            using var locked = _frame.Lock(PmLockMode.Read);

            RunPipeline(new LockedFramebuffer(locked.Scan0, _rawSize, (int)locked.Stride, Dpi, _decodeFormat, _decodeAlpha, null),
                plan, destination, destRowBytes);
        }

        private void RunPipeline(ILockedFramebuffer source, in FusedPlanExecution plan, IntPtr destination, int destRowBytes)
            => FusedPixelPipeline.Run(source, plan, destination, destRowBytes, _allocator, _options?.CancellationToken ?? default);

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

    internal class PixelManIndexedFrameSource : PixelManBitmapFrameSource, IIndexedFrame
    {
        public PixelManIndexedFrameSource(PmBitmapFrame frame, PixelManMetadataStore metadata,
            BitmapDecodeOptions? options, IBitmapMemoryAllocator allocator, BitmapPalette palette)
            : base(frame, metadata, options, allocator, PixelOrientation.Normal)
        {
            Palette = palette;
        }

        public BitmapPalette Palette { get; }

        // Indexed output is only produced for the identity plan, so the frame's pixels are the indices.
        public ILockedFramebuffer LockIndexed() => Lock();
    }

    /// <summary>
    /// A raw GIF frame: its own rectangle of the logical screen with timing and disposal.
    /// Compositing is left to the caller.
    /// </summary>
    internal sealed class PixelManGifFrameSource : PixelManIndexedFrameSource, IAnimatedFrame
    {
        public PixelManGifFrameSource(PmBitmapFrame frame, PixelManMetadataStore metadata,
            BitmapDecodeOptions? options, IBitmapMemoryAllocator allocator, BitmapPalette palette)
            : base(frame, metadata, options, allocator, palette)
        {
            Animation = GifAnimation.Read(frame, metadata);
        }

        public GifAnimation Animation { get; }

        public TimeSpan Delay => Animation.Delay;

        public FrameDisposalMethod Disposal => Animation.Disposal;

        public PixelPoint Origin => Animation.Origin;

        public FrameBlendMode Blend => FrameBlendMode.Over;
    }

    /// <summary>
    /// A GIF frame whose decode plan resamples or converts it, so it no longer carries
    /// indices; it keeps the frame's animation data.
    /// </summary>
    internal sealed class PixelManGifColorFrameSource : PixelManBitmapFrameSource, IAnimatedFrame
    {
        public PixelManGifColorFrameSource(PmBitmapFrame frame, PixelManMetadataStore metadata,
            BitmapDecodeOptions? options, IBitmapMemoryAllocator allocator)
            : base(frame, metadata, options, allocator, PixelOrientation.Normal)
        {
            Animation = GifAnimation.Read(frame, metadata);
        }

        public GifAnimation Animation { get; }

        public TimeSpan Delay => Animation.Delay;

        public FrameDisposalMethod Disposal => Animation.Disposal;

        public PixelPoint Origin => Animation.Origin;

        public FrameBlendMode Blend => FrameBlendMode.Over;
    }

    /// <summary>
    /// The animation data of a raw GIF frame. Transparent GIF pixels leave the previous
    /// frame visible, so frames blend over it.
    /// </summary>
    internal readonly record struct GifAnimation(TimeSpan Delay, FrameDisposalMethod Disposal, PixelPoint Origin)
    {
        public static GifAnimation Read(PmBitmapFrame frame, PixelManMetadataStore metadata)
        {
            var bag = metadata.Metadata;

            // GIF delays are in hundredths of a second.
            return new GifAnimation(
                TimeSpan.FromMilliseconds((bag.FrameDelay ?? 0) * 10.0),
                bag.DisposalMethod is { } disposal and <= 3 ? (FrameDisposalMethod)disposal : FrameDisposalMethod.Unspecified,
                frame.GetFrameOffset() is { } offset ? new PixelPoint(offset.Left, offset.Top) : default);
        }
    }
}
