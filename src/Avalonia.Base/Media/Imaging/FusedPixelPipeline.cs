using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading;
using Avalonia.Platform;
using Avalonia.Platform.Internal;

namespace Avalonia.Media.Imaging;

/// <summary>
/// The software part of a decode plan, fully resolved: the crop window is already
/// clamped to the source bounds, the target size is absolute and the formats are the
/// ones the destination stores. <see cref="SourceRegion"/> and <see cref="TargetSize"/>
/// are in oriented (display) space: for the transposing orientations the oriented
/// source swaps width and height relative to the raw framebuffer.
/// </summary>
/// <param name="SourceRegion">The crop window in oriented source pixels; the whole oriented frame when there is no crop.</param>
/// <param name="TargetSize">The output size; equals the region size when there is no scaling.</param>
/// <param name="TargetFormat">The pixel format the destination stores.</param>
/// <param name="TargetAlphaFormat">The alpha format the destination stores.</param>
/// <param name="Interpolation">The interpolation used when resampling is required.</param>
/// <param name="Orientation">The transform that turns the raw source into the oriented image.</param>
internal readonly record struct FusedPlanExecution(
    PixelRect SourceRegion,
    PixelSize TargetSize,
    PixelFormat TargetFormat,
    AlphaFormat TargetAlphaFormat,
    BitmapInterpolationMode Interpolation,
    PixelOrientation Orientation = PixelOrientation.Normal);

/// <summary>
/// Executes the software stage of a decode plan in one pass over the source rows:
/// orientation, crop, resample, then pixel and alpha format conversion. The working
/// set is bounded by a few rows; a full-frame staging buffer is never allocated.
/// </summary>
internal static unsafe class FusedPixelPipeline
{
    private const double CoverageEpsilon = 1e-9;

    // Cancellation is observed between rows at this granularity.
    private const int CancellationCheckRows = 16;

    private static readonly Vector128<float> s_half = Vector128.Create(0.5f);
    private static readonly Vector128<float> s_maxChannel = Vector128.Create(255f);

    private enum FilterKind
    {
        Nearest,
        Bilinear,
        Box
    }

    /// <summary>
    /// Executes the plan against a locked source framebuffer, writing the result to
    /// <paramref name="destAddress"/>.
    /// </summary>
    /// <remarks>
    /// The source and destination memory must not overlap: rows are streamed, so the
    /// destination is partially written while source rows are still being read.
    /// </remarks>
    public static void Run(ILockedFramebuffer source, in FusedPlanExecution plan,
        IntPtr destAddress, int destRowBytes, IBitmapMemoryAllocator? allocator = null,
        CancellationToken cancellation = default)
    {
        if (source is null)
            throw new ArgumentNullException(nameof(source));
        if (destAddress == IntPtr.Zero)
            throw new ArgumentException("A valid destination address is required.", nameof(destAddress));
        if (plan.Orientation < PixelOrientation.Normal || plan.Orientation > PixelOrientation.Rotate270)
            throw new ArgumentException($"Orientation {plan.Orientation} is not a valid EXIF orientation.", nameof(plan));

        var region = plan.SourceRegion;
        var target = plan.TargetSize;
        var orientedSize = GetOrientedSize(source.Size, plan.Orientation);

        if (region.X < 0 || region.Y < 0 ||
            region.Right > orientedSize.Width || region.Bottom > orientedSize.Height)
            throw new ArgumentException("The source region must lie within the oriented source bounds.", nameof(plan));

        if (region.Width <= 0 || region.Height <= 0 || target.Width <= 0 || target.Height <= 0)
            return;

        if (destRowBytes < PixelFormatArithmetic.GetMinRowBytes(plan.TargetFormat, target.Width))
            throw new ArgumentOutOfRangeException(nameof(destRowBytes));

        cancellation.ThrowIfCancellationRequested();

        allocator ??= BitmapMemoryPool.Shared;

        var context = new PipelineContext(source, plan, cancellation);

        using var tiles = context.Tiles;

        tiles?.Rent(allocator);

        if (target == region.Size)
        {
            RunIdentity(context, plan, destAddress, destRowBytes, allocator);
        }
        else
        {
            switch (GetFilter(plan.Interpolation, region.Size, target))
            {
                case FilterKind.Nearest:
                    RunNearest(context, plan, destAddress, destRowBytes, allocator);
                    break;
                case FilterKind.Bilinear:
                    RunBilinear(context, plan, destAddress, destRowBytes, allocator);
                    break;
                default:
                    RunBox(context, plan, destAddress, destRowBytes, allocator);
                    break;
            }
        }
    }

    /// <summary>
    /// Gets the source size in oriented (display) space.
    /// </summary>
    public static PixelSize GetOrientedSize(PixelSize rawSize, PixelOrientation orientation)
        => IsColumnMajor(orientation) ? new PixelSize(rawSize.Height, rawSize.Width) : rawSize;

    // The transposing orientations turn raw columns into oriented rows.
    private static bool IsColumnMajor(PixelOrientation orientation)
        => orientation is PixelOrientation.Transpose or PixelOrientation.Rotate90
            or PixelOrientation.Transverse or PixelOrientation.Rotate270;

    // None and LowQuality sample nearest. MediumQuality and HighQuality use bilinear
    // when any axis grows and the streaming box filter otherwise; Unspecified follows
    // the HighQuality default of the decode options. Choosing the filter for the whole
    // operation keeps the box filter on scale factors of at least one per axis.
    private static FilterKind GetFilter(BitmapInterpolationMode mode, PixelSize sourceSize, PixelSize targetSize)
    {
        if (mode == BitmapInterpolationMode.None || mode == BitmapInterpolationMode.LowQuality)
            return FilterKind.Nearest;

        return targetSize.Width > sourceSize.Width || targetSize.Height > sourceSize.Height ?
            FilterKind.Bilinear :
            FilterKind.Box;
    }

    // Everything the row producers share, computed once per run. The mapping from
    // oriented coordinates back to raw coordinates decomposes into three switches:
    // whether oriented rows follow raw rows or raw columns (ColumnMajor), whether the
    // raw major index runs opposite to the oriented row index (FlipMajor), and whether
    // the produced row must be reversed (Mirror).
    private readonly struct PipelineContext
    {
        public PipelineContext(ILockedFramebuffer source, in FusedPlanExecution plan, CancellationToken cancellation)
        {
            Source = source;
            Region = plan.SourceRegion;
            Cancellation = cancellation;

            // Formats without an alpha channel always read back as opaque pixels,
            // matching the alpha normalization the Bitmap constructor applies before
            // transcoding.
            SourceAlpha = source.Format.HasAlpha ? source.AlphaFormat : AlphaFormat.Opaque;

            // Filtering mixes neighboring pixels; on unassociated alpha that bleeds the
            // color of fully transparent pixels into their neighbors, so the filters weight
            // colors by alpha. They do so on the float samples: premultiplied bytes would
            // lose the color of translucent pixels (128 at alpha 128 premultiplies to 64
            // and comes back as 127).
            Premultiply = SourceAlpha == AlphaFormat.Unpremul;
            UnpremultiplyOutput = Premultiply && plan.TargetAlphaFormat != AlphaFormat.Premul;
            FilterAlpha = Premultiply && !UnpremultiplyOutput ? AlphaFormat.Premul : SourceAlpha;

            var bitsPerPixel = source.Format.BitsPerPixel;

            BytesPerPixel = bitsPerPixel % 8 == 0 ? bitsPerPixel / 8 : 0;

            ColumnMajor = IsColumnMajor(plan.Orientation);

            FlipMajor = plan.Orientation is PixelOrientation.Rotate180 or PixelOrientation.FlipVertical
                or PixelOrientation.Rotate270 or PixelOrientation.Transverse;

            Mirror = plan.Orientation is PixelOrientation.FlipHorizontal or PixelOrientation.Rotate180
                or PixelOrientation.Rotate90 or PixelOrientation.Transverse;

            var minorLength = ColumnMajor ? source.Size.Height : source.Size.Width;

            MinorStart = Mirror ? minorLength - Region.X - Region.Width : Region.X;

            if (ColumnMajor)
            {
                // Sub-byte columns are produced by decoding each raw row up to the
                // needed column, so the staging tail must hold that prefix.
                var maxColumn = FlipMajor ? source.Size.Width - 1 - Region.Y : Region.Y + Region.Height - 1;

                StagingWidth = Region.Width + (BytesPerPixel > 0 ? 0 : maxColumn + 1);
                Tiles = BytesPerPixel > 0 ? new ColumnTiles(Region.Width) : null;
            }
            else
            {
                // Sub-byte rows cannot be addressed mid-row, so cropped reads start at
                // the row origin and skip the crop prefix in the staging row.
                StagingWidth = BytesPerPixel > 0 ? Region.Width : MinorStart + Region.Width;
            }
        }

        public ILockedFramebuffer Source { get; }
        public PixelRect Region { get; }
        public CancellationToken Cancellation { get; }
        public AlphaFormat SourceAlpha { get; }
        /// <summary>Gets whether the filters weight colors by alpha before mixing them.</summary>
        public bool Premultiply { get; }

        /// <summary>Gets whether the filtered colors are divided by alpha again before rounding.</summary>
        public bool UnpremultiplyOutput { get; }

        /// <summary>Gets the alpha format of the filtered rows.</summary>
        public AlphaFormat FilterAlpha { get; }
        public int BytesPerPixel { get; }
        public bool ColumnMajor { get; }
        public bool FlipMajor { get; }
        public bool Mirror { get; }
        public int MinorStart { get; }
        public int StagingWidth { get; }
        public ColumnTiles? Tiles { get; }
    }

    // Oriented rows that come from raw columns, decoded a tile of columns at a time: every
    // raw row is read once per tile over contiguous memory, instead of once per oriented
    // pixel at a stride, which touched a new cache line for every pixel.
    private sealed class ColumnTiles : IDisposable
    {
        public const int TileRows = 16;

        private readonly Rgba8888Pixel[] _scratch = new Rgba8888Pixel[TileRows];
        private readonly int _width;
        private IBitmapMemory? _memory;
        private int _first = -1;
        private int _count;

        public ColumnTiles(int width)
        {
            _width = width;
        }

        private Span<Rgba8888Pixel> Rows => new((void*)_memory!.Address, _width * TileRows);

        // The tile takes pool memory: a tile of a wide frame exceeds the large object
        // threshold, and a managed array would be a large-object allocation per decode.
        public void Rent(IBitmapMemoryAllocator allocator)
            => _memory ??= allocator.Rent((long)_width * TileRows * sizeof(Rgba8888Pixel));

        public void Dispose()
        {
            _memory?.Dispose();
            _memory = null;
        }

        public void Read(scoped in PipelineContext context, int rowIndex, Span<Rgba8888Pixel> row)
        {
            if (rowIndex < _first || rowIndex >= _first + _count)
                Load(context, rowIndex);

            Rows.Slice((rowIndex - _first) * _width, _width).CopyTo(row);
        }

        private void Load(scoped in PipelineContext context, int firstRow)
        {
            var source = context.Source;

            _first = firstRow;
            _count = Math.Min(TileRows, context.Region.Height - firstRow);

            // Oriented rows firstRow.. map to raw columns that run forwards, or backwards
            // when the major axis is flipped; either way they are contiguous in a raw row.
            var firstMajor = context.Region.Y + firstRow;
            var firstColumn = context.FlipMajor ? source.Size.Width - firstMajor - _count : firstMajor;
            var pixels = _scratch.AsSpan(0, _count);
            var rows = Rows;

            for (var i = 0; i < _width; i++)
            {
                var address = source.Address + (nint)(context.MinorStart + i) * source.RowBytes +
                    (nint)firstColumn * context.BytesPerPixel;

                PixelFormatTranscoder.ReadRow(address, source.Format, pixels);

                for (var k = 0; k < _count; k++)
                {
                    var tileRow = context.FlipMajor ? _count - 1 - k : k;

                    rows[tileRow * _width + i] = pixels[k];
                }
            }
        }
    }

    // Produces oriented-space row rowIndex of the crop window as canonical Rgba8888
    // pixels, reading the appropriate raw row, reversed raw row or strided raw column.
    // The staging span must hold StagingWidth pixels.
    private static Span<Rgba8888Pixel> ReadOrientedRow(scoped in PipelineContext context, int rowIndex, Span<Rgba8888Pixel> staging)
    {
        var source = context.Source;
        var width = context.Region.Width;
        var majorIndex = context.Region.Y + rowIndex;

        Span<Rgba8888Pixel> row;

        if (!context.ColumnMajor)
        {
            var rawRow = context.FlipMajor ? source.Size.Height - 1 - majorIndex : majorIndex;
            var rowAddress = source.Address + (nint)rawRow * source.RowBytes;

            if (context.BytesPerPixel > 0)
            {
                row = staging.Slice(0, width);

                PixelFormatTranscoder.ReadRow(rowAddress + (nint)context.MinorStart * context.BytesPerPixel, source.Format, row);
            }
            else
            {
                var prefixed = staging.Slice(0, context.MinorStart + width);

                PixelFormatTranscoder.ReadRow(rowAddress, source.Format, prefixed);

                row = prefixed.Slice(context.MinorStart);
            }
        }
        else
        {
            var rawColumn = context.FlipMajor ? source.Size.Width - 1 - majorIndex : majorIndex;

            row = staging.Slice(0, width);

            if (context.Tiles is { } tiles)
            {
                tiles.Read(context, rowIndex, row);
            }
            else
            {
                // Sub-byte formats cannot be addressed mid-row: decode the raw row up to
                // the needed column and keep its last pixel.
                var scratch = staging.Slice(width, rawColumn + 1);

                for (var i = 0; i < width; i++)
                {
                    var address = source.Address + (nint)(context.MinorStart + i) * source.RowBytes;

                    PixelFormatTranscoder.ReadRow(address, source.Format, scratch);

                    row[i] = scratch[rawColumn];
                }
            }
        }

        // Reversing whole pixels as integers lets the runtime vectorize the reverse.
        if (context.Mirror)
            MemoryMarshal.Cast<Rgba8888Pixel, uint>(row).Reverse();

        return row;
    }

    private static Span<Rgba8888Pixel> ReadFilteredRow(scoped in PipelineContext context, int rowIndex, Span<Rgba8888Pixel> staging)
        => ReadOrientedRow(context, rowIndex, staging);

    private static void RunIdentity(in PipelineContext context, in FusedPlanExecution plan,
        IntPtr destAddress, int destRowBytes, IBitmapMemoryAllocator allocator)
    {
        var source = context.Source;
        var region = context.Region;

        // No transform and no conversion requested: copy the rows verbatim instead of
        // round-tripping every pixel through the canonical format.
        if (plan.Orientation == PixelOrientation.Normal &&
            source.Format == plan.TargetFormat &&
            context.BytesPerPixel > 0 &&
            (context.SourceAlpha == plan.TargetAlphaFormat || !source.Format.HasAlpha))
        {
            var rowBytes = PixelFormatArithmetic.GetMinRowBytes(source.Format, region.Width);

            for (var y = 0; y < region.Height; y++)
            {
                if (y % CancellationCheckRows == 0)
                    context.Cancellation.ThrowIfCancellationRequested();

                var sourceRow = source.Address + (nint)(region.Y + y) * source.RowBytes + (nint)region.X * context.BytesPerPixel;
                var destRow = destAddress + (nint)y * destRowBytes;

                Buffer.MemoryCopy((void*)sourceRow, (void*)destRow, rowBytes, rowBytes);
            }

            return;
        }

        // A conversion between 8-bit RGB layouts with nothing to reorient runs the direct
        // kernels, as the install-time transcode does, instead of the canonical row; the
        // rows go in bands so cancellation is still observed.
        if (plan.Orientation == PixelOrientation.Normal && context.BytesPerPixel > 0)
        {
            var origin = source.Address + (nint)region.Y * source.RowBytes + (nint)region.X * context.BytesPerPixel;
            var direct = true;

            for (var y = 0; direct && y < region.Height; y += CancellationCheckRows)
            {
                context.Cancellation.ThrowIfCancellationRequested();

                var rows = Math.Min(CancellationCheckRows, region.Height - y);

                direct = PixelFormatTranscoder.TryTranscodeDirect(origin + (nint)y * source.RowBytes,
                    new PixelSize(region.Width, rows), source.RowBytes, source.Format, context.SourceAlpha,
                    destAddress + (nint)y * destRowBytes, destRowBytes, plan.TargetFormat, plan.TargetAlphaFormat);
            }

            if (direct)
                return;
        }

        using var readStaging = allocator.Rent(context.StagingWidth * sizeof(Rgba8888Pixel));

        var staging = new Span<Rgba8888Pixel>((void*)readStaging.Address, context.StagingWidth);

        for (var y = 0; y < region.Height; y++)
        {
            if (y % CancellationCheckRows == 0)
                context.Cancellation.ThrowIfCancellationRequested();

            var row = ReadOrientedRow(context, y, staging);

            PixelFormatTranscoder.WriteRow(row, destAddress + (nint)y * destRowBytes, plan.TargetFormat, plan.TargetAlphaFormat, context.SourceAlpha);
        }
    }

    private static void RunNearest(in PipelineContext context, in FusedPlanExecution plan,
        IntPtr destAddress, int destRowBytes, IBitmapMemoryAllocator allocator)
    {
        var srcWidth = context.Region.Width;
        var srcHeight = context.Region.Height;
        var destWidth = plan.TargetSize.Width;
        var destHeight = plan.TargetSize.Height;

        // Nearest sampling copies whole pixels, so no premultiplication is needed.
        var columnMap = new int[destWidth];

        for (var x = 0; x < destWidth; x++)
        {
            columnMap[x] = Math.Min(srcWidth - 1, (int)((x + 0.5) * srcWidth / destWidth));
        }

        using var readStaging = allocator.Rent(context.StagingWidth * sizeof(Rgba8888Pixel));
        using var destStaging = allocator.Rent(destWidth * sizeof(Rgba8888Pixel));

        var staging = new Span<Rgba8888Pixel>((void*)readStaging.Address, context.StagingWidth);
        var destRow = new Span<Rgba8888Pixel>((void*)destStaging.Address, destWidth);

        var loadedRow = -1;
        Span<Rgba8888Pixel> row = default;

        for (var y = 0; y < destHeight; y++)
        {
            if (y % CancellationCheckRows == 0)
                context.Cancellation.ThrowIfCancellationRequested();

            var sourceY = Math.Min(srcHeight - 1, (int)((y + 0.5) * srcHeight / destHeight));

            if (sourceY != loadedRow)
            {
                row = ReadOrientedRow(context, sourceY, staging);
                loadedRow = sourceY;
            }

            for (var x = 0; x < destWidth; x++)
            {
                destRow[x] = row[columnMap[x]];
            }

            PixelFormatTranscoder.WriteRow(destRow, destAddress + (nint)y * destRowBytes, plan.TargetFormat, plan.TargetAlphaFormat, context.SourceAlpha);
        }
    }

    private static void RunBilinear(in PipelineContext context, in FusedPlanExecution plan,
        IntPtr destAddress, int destRowBytes, IBitmapMemoryAllocator allocator)
    {
        var srcWidth = context.Region.Width;
        var srcHeight = context.Region.Height;
        var destWidth = plan.TargetSize.Width;
        var destHeight = plan.TargetSize.Height;

        var leftMap = new int[destWidth];
        var rightMap = new int[destWidth];
        var fractionMap = new float[destWidth];

        for (var x = 0; x < destWidth; x++)
        {
            var position = MapToSource(x, srcWidth, destWidth);
            var left = (int)position;

            leftMap[x] = left;
            rightMap[x] = Math.Min(left + 1, srcWidth - 1);
            fractionMap[x] = (float)(position - left);
        }

        using var stagingA = allocator.Rent(context.StagingWidth * sizeof(Rgba8888Pixel));
        using var stagingB = allocator.Rent(context.StagingWidth * sizeof(Rgba8888Pixel));
        using var destStaging = allocator.Rent(destWidth * sizeof(Rgba8888Pixel));

        var topBuffer = new Span<Rgba8888Pixel>((void*)stagingA.Address, context.StagingWidth);
        var bottomBuffer = new Span<Rgba8888Pixel>((void*)stagingB.Address, context.StagingWidth);
        var destRow = new Span<Rgba8888Pixel>((void*)destStaging.Address, destWidth);

        var loadedTop = -1;
        var loadedBottom = -1;
        Span<Rgba8888Pixel> topRow = default;
        Span<Rgba8888Pixel> bottomRow = default;

        for (var y = 0; y < destHeight; y++)
        {
            if (y % CancellationCheckRows == 0)
                context.Cancellation.ThrowIfCancellationRequested();

            var position = MapToSource(y, srcHeight, destHeight);
            var topIndex = (int)position;
            var bottomIndex = Math.Min(topIndex + 1, srcHeight - 1);
            var fy = (float)(position - topIndex);

            // The top tap advances monotonically, so the previous bottom row can slide
            // into the top slot and at most one new row is read per destination row.
            if (loadedTop != topIndex)
            {
                if (loadedBottom == topIndex)
                {
                    var swapBuffer = topBuffer;
                    topBuffer = bottomBuffer;
                    bottomBuffer = swapBuffer;

                    var swapRow = topRow;
                    topRow = bottomRow;
                    bottomRow = swapRow;

                    (loadedTop, loadedBottom) = (loadedBottom, loadedTop);
                }
                else
                {
                    topRow = ReadFilteredRow(context, topIndex, topBuffer);
                    loadedTop = topIndex;
                }
            }

            if (bottomIndex != topIndex && loadedBottom != bottomIndex)
            {
                bottomRow = ReadFilteredRow(context, bottomIndex, bottomBuffer);
                loadedBottom = bottomIndex;
            }

            var top = topRow;
            var bottom = bottomIndex == topIndex ? topRow : bottomRow;

            var verticalWeight = Vector128.Create(fy);

            var premultiply = context.Premultiply;
            var unpremultiply = context.UnpremultiplyOutput;

            for (var x = 0; x < destWidth; x++)
            {
                var horizontalWeight = Vector128.Create(fractionMap[x]);
                var topLeft = ToFilterVector(top[leftMap[x]], premultiply);
                var bottomLeft = ToFilterVector(bottom[leftMap[x]], premultiply);
                var upper = topLeft + (ToFilterVector(top[rightMap[x]], premultiply) - topLeft) * horizontalWeight;
                var lower = bottomLeft + (ToFilterVector(bottom[rightMap[x]], premultiply) - bottomLeft) * horizontalWeight;

                destRow[x] = ToFilteredPixel(upper + (lower - upper) * verticalWeight, unpremultiply);
            }

            PixelFormatTranscoder.WriteRow(destRow, destAddress + (nint)y * destRowBytes, plan.TargetFormat, plan.TargetAlphaFormat, context.FilterAlpha);
        }
    }

    // Center-aligned sample mapping, clamped to the source range.
    private static double MapToSource(int index, int sourceLength, int targetLength)
    {
        var position = (index + 0.5) * sourceLength / targetLength - 0.5;

        if (position < 0)
            return 0;

        if (position > sourceLength - 1)
            return sourceLength - 1;

        return position;
    }

    // One pixel is one vector of four float channels. The filters use plain multiplies and
    // adds, never fused multiply-add, so the result is the same on every instruction set
    // and in the software fallback.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> ToVector(Rgba8888Pixel pixel)
    {
        var bytes = Vector128.CreateScalarUnsafe(Unsafe.As<Rgba8888Pixel, uint>(ref pixel)).AsByte();

        return Vector128.ConvertToSingle(Vector128.WidenLower(Vector128.WidenLower(bytes)).AsInt32());
    }

    // A sample as the filters mix it: with its color weighted by its alpha when the source
    // alpha is unassociated.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> ToFilterVector(Rgba8888Pixel pixel, bool premultiply)
    {
        var channels = ToVector(pixel);

        if (!premultiply)
            return channels;

        var weight = channels.GetElement(3) * (1f / 255f);

        return channels * Vector128.Create(weight, weight, weight, 1f);
    }

    // Rounds a filtered sample to a pixel, dividing the color by alpha first when the output
    // is unassociated. A sample whose alpha rounds to zero becomes transparent black.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Rgba8888Pixel ToFilteredPixel(Vector128<float> channels, bool unpremultiply)
    {
        if (unpremultiply)
        {
            var alpha = channels.GetElement(3);

            if (alpha < 0.5f)
                return default;

            var scale = 255f / alpha;

            channels *= Vector128.Create(scale, scale, scale, 1f);
        }

        return ToPixel(channels + s_half);
    }

    // Truncates, so callers add one half to round.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Rgba8888Pixel ToPixel(Vector128<float> channels)
    {
        var integers = Vector128.ConvertToInt32(Vector128.Min(channels, s_maxChannel));
        var words = Vector128.Narrow(integers, integers);
        var packed = Vector128.Narrow(words, words).AsUInt32().ToScalar();

        return Unsafe.As<uint, Rgba8888Pixel>(ref packed);
    }

    private static void RunBox(in PipelineContext context, in FusedPlanExecution plan,
        IntPtr destAddress, int destRowBytes, IBitmapMemoryAllocator allocator)
    {
        var srcWidth = context.Region.Width;
        var srcHeight = context.Region.Height;
        var destWidth = plan.TargetSize.Width;
        var destHeight = plan.TargetSize.Height;

        var scaleY = srcHeight / (double)destHeight;

        BuildCoverage(srcWidth, destWidth, out var segmentOffsets, out var segmentColumns, out var segmentWeights, out var columnWeightSums);

        using var readStaging = allocator.Rent(context.StagingWidth * sizeof(Rgba8888Pixel));
        using var horizontalStaging = allocator.Rent(destWidth * sizeof(Vector128<float>));
        using var currentStaging = allocator.Rent(destWidth * sizeof(Vector128<float>));
        using var nextStaging = allocator.Rent(destWidth * sizeof(Vector128<float>));
        using var destStaging = allocator.Rent(destWidth * sizeof(Rgba8888Pixel));

        var staging = new Span<Rgba8888Pixel>((void*)readStaging.Address, context.StagingWidth);
        var horizontal = new Span<Vector128<float>>((void*)horizontalStaging.Address, destWidth);
        var current = new Span<Vector128<float>>((void*)currentStaging.Address, destWidth);
        var next = new Span<Vector128<float>>((void*)nextStaging.Address, destWidth);
        var destRow = new Span<Rgba8888Pixel>((void*)destStaging.Address, destWidth);

        current.Clear();
        next.Clear();

        var currentWeight = 0d;
        var nextWeight = 0d;
        var destY = 0;

        for (var sourceY = 0; sourceY < srcHeight && destY < destHeight; sourceY++)
        {
            if (sourceY % CancellationCheckRows == 0)
                context.Cancellation.ThrowIfCancellationRequested();

            var row = ReadFilteredRow(context, sourceY, staging);

            // Reduce the source row horizontally into raw weighted channel sums.
            for (var x = 0; x < destWidth; x++)
            {
                var sum = Vector128<float>.Zero;

                for (var segment = segmentOffsets[x]; segment < segmentOffsets[x + 1]; segment++)
                    sum += ToFilterVector(row[segmentColumns[segment]], context.Premultiply) * segmentWeights[segment];

                horizontal[x] = sum;
            }

            // Distribute the reduced row over the destination rows it covers. With a
            // vertical scale factor of at least one it covers at most two of them.
            var rowTop = (double)sourceY;
            var rowBottom = sourceY + 1d;
            var currentBottom = (destY + 1) * scaleY;
            var currentOverlap = Math.Min(rowBottom, currentBottom) - Math.Max(rowTop, destY * scaleY);

            if (currentOverlap > 0)
            {
                Accumulate(current, horizontal, (float)currentOverlap);
                currentWeight += currentOverlap;
            }

            if (rowBottom > currentBottom && destY + 1 < destHeight)
            {
                var nextOverlap = Math.Min(rowBottom, currentBottom + scaleY) - currentBottom;

                if (nextOverlap > 0)
                {
                    Accumulate(next, horizontal, (float)nextOverlap);
                    nextWeight += nextOverlap;
                }
            }

            // Emit the destination row once its source span is complete; the last source
            // row also flushes a row left incomplete by floating point shortfall.
            if (rowBottom >= currentBottom - CoverageEpsilon || sourceY == srcHeight - 1)
            {
                EmitBoxRow(current, (float)currentWeight, columnWeightSums, destRow, context.UnpremultiplyOutput);

                PixelFormatTranscoder.WriteRow(destRow, destAddress + (nint)destY * destRowBytes, plan.TargetFormat, plan.TargetAlphaFormat, context.FilterAlpha);

                var completed = current;
                current = next;
                next = completed;
                next.Clear();

                currentWeight = nextWeight;
                nextWeight = 0;
                destY++;
            }
        }
    }

    private static void Accumulate(Span<Vector128<float>> accumulator, ReadOnlySpan<Vector128<float>> row, float weight)
    {
        for (var i = 0; i < accumulator.Length; i++)
        {
            accumulator[i] += row[i] * weight;
        }
    }

    private static void EmitBoxRow(ReadOnlySpan<Vector128<float>> accumulator, float rowWeight, float[] columnWeightSums,
        Span<Rgba8888Pixel> destRow, bool unpremultiply)
    {
        for (var x = 0; x < destRow.Length; x++)
        {
            var total = columnWeightSums[x] * rowWeight;

            destRow[x] = total > 0 ? ToFilteredPixel(accumulator[x] / total, unpremultiply) : default;
        }
    }

    // Builds a CSR-style coverage table: for each destination column the covered source
    // columns with their (possibly fractional) weights, plus the per-column weight sum
    // used for normalization.
    private static void BuildCoverage(int sourceLength, int targetLength,
        out int[] segmentOffsets, out int[] segmentColumns, out float[] segmentWeights, out float[] weightSums)
    {
        var scale = sourceLength / (double)targetLength;

        segmentOffsets = new int[targetLength + 1];

        for (var x = 0; x < targetLength; x++)
        {
            GetCoverage(x, scale, sourceLength, out var first, out var last);

            segmentOffsets[x + 1] = segmentOffsets[x] + last - first + 1;
        }

        segmentColumns = new int[segmentOffsets[targetLength]];
        segmentWeights = new float[segmentOffsets[targetLength]];
        weightSums = new float[targetLength];

        for (var x = 0; x < targetLength; x++)
        {
            GetCoverage(x, scale, sourceLength, out var first, out var last);

            var start = x * scale;
            var end = start + scale;
            var offset = segmentOffsets[x];

            for (var column = first; column <= last; column++)
            {
                var weight = (float)Math.Max(0, Math.Min(end, column + 1) - Math.Max(start, column));

                segmentColumns[offset] = column;
                segmentWeights[offset] = weight;
                weightSums[x] += weight;
                offset++;
            }
        }
    }

    private static void GetCoverage(int index, double scale, int sourceLength, out int first, out int last)
    {
        var start = index * scale;
        var end = start + scale;

        first = Math.Max(0, (int)start);
        last = Math.Min(sourceLength - 1, (int)Math.Ceiling(end - CoverageEpsilon) - 1);

        if (last < first)
            last = first;
    }
}
