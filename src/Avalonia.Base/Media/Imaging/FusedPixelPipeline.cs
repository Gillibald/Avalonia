using System;
using Avalonia.Platform;
using Avalonia.Platform.Internal;

namespace Avalonia.Media.Imaging;

/// <summary>
/// The software part of a decode plan, fully resolved: the crop window is already
/// clamped to the source bounds, the target size is absolute and the formats are the
/// ones the destination stores.
/// </summary>
/// <param name="SourceRegion">The crop window in source pixels; the whole frame when there is no crop.</param>
/// <param name="TargetSize">The output size; equals the region size when there is no scaling.</param>
/// <param name="TargetFormat">The pixel format the destination stores.</param>
/// <param name="TargetAlphaFormat">The alpha format the destination stores.</param>
/// <param name="Interpolation">The interpolation used when resampling is required.</param>
internal readonly record struct FusedPlanExecution(
    PixelRect SourceRegion,
    PixelSize TargetSize,
    PixelFormat TargetFormat,
    AlphaFormat TargetAlphaFormat,
    BitmapInterpolationMode Interpolation);

/// <summary>
/// Executes the software stage of a decode plan in one pass over the source rows:
/// crop, resample, then pixel and alpha format conversion. The working set is bounded
/// by a few rows; a full-frame staging buffer is never allocated.
/// </summary>
internal static unsafe class FusedPixelPipeline
{
    private const double CoverageEpsilon = 1e-9;

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
        IntPtr destAddress, int destRowBytes, IBitmapMemoryAllocator? allocator = null)
    {
        if (source is null)
            throw new ArgumentNullException(nameof(source));
        if (destAddress == IntPtr.Zero)
            throw new ArgumentException("A valid destination address is required.", nameof(destAddress));

        var region = plan.SourceRegion;
        var target = plan.TargetSize;

        if (region.X < 0 || region.Y < 0 ||
            region.Right > source.Size.Width || region.Bottom > source.Size.Height)
            throw new ArgumentException("The source region must lie within the source bounds.", nameof(plan));

        if (region.Width <= 0 || region.Height <= 0 || target.Width <= 0 || target.Height <= 0)
            return;

        if (destRowBytes < (target.Width * plan.TargetFormat.BitsPerPixel + 7) / 8)
            throw new ArgumentOutOfRangeException(nameof(destRowBytes));

        allocator ??= BitmapMemoryPool.Shared;

        if (target == region.Size)
        {
            RunIdentity(source, region, plan, destAddress, destRowBytes, allocator);
        }
        else
        {
            switch (GetFilter(plan.Interpolation, region.Size, target))
            {
                case FilterKind.Nearest:
                    RunNearest(source, region, plan, destAddress, destRowBytes, allocator);
                    break;
                case FilterKind.Bilinear:
                    RunBilinear(source, region, plan, destAddress, destRowBytes, allocator);
                    break;
                default:
                    RunBox(source, region, plan, destAddress, destRowBytes, allocator);
                    break;
            }
        }
    }

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

    // Formats narrower than a byte cannot be addressed mid-row, so cropped reads of
    // such formats start at the row origin and skip the crop prefix in the staging row.
    private readonly struct SourceRowLayout
    {
        public SourceRowLayout(PixelFormat format, PixelRect region)
        {
            var bitsPerPixel = format.BitsPerPixel;

            if (bitsPerPixel % 8 == 0)
            {
                RowByteOffset = region.X * (bitsPerPixel / 8);
                PixelOffset = 0;
                ReadWidth = region.Width;
            }
            else
            {
                RowByteOffset = 0;
                PixelOffset = region.X;
                ReadWidth = region.X + region.Width;
            }
        }

        public int RowByteOffset { get; }
        public int PixelOffset { get; }
        public int ReadWidth { get; }
    }

    private static Span<Rgba8888Pixel> ReadSourceRow(
        ILockedFramebuffer source, scoped in PixelRect region, scoped in SourceRowLayout layout, int rowIndex, Span<Rgba8888Pixel> buffer)
    {
        var address = source.Address + (nint)(region.Y + rowIndex) * source.RowBytes + layout.RowByteOffset;

        PixelFormatTranscoder.ReadRow(address, source.Format, buffer);

        return buffer.Slice(layout.PixelOffset, region.Width);
    }

    // Formats without an alpha channel always read back as opaque pixels, matching the
    // alpha normalization the Bitmap constructor applies before transcoding.
    private static AlphaFormat GetEffectiveSourceAlpha(ILockedFramebuffer source)
        => source.Format.HasAlpha ? source.AlphaFormat : AlphaFormat.Opaque;

    private static void PremultiplyRow(Span<Rgba8888Pixel> row)
    {
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = PixelFormatWriter.GetConvertedPixel(row[i], AlphaFormat.Unpremul, AlphaFormat.Premul);
        }
    }

    private static void RunIdentity(ILockedFramebuffer source, PixelRect region, in FusedPlanExecution plan,
        IntPtr destAddress, int destRowBytes, IBitmapMemoryAllocator allocator)
    {
        var sourceAlpha = GetEffectiveSourceAlpha(source);
        var bitsPerPixel = source.Format.BitsPerPixel;

        // No conversion requested: copy the rows verbatim instead of round-tripping
        // every pixel through the canonical format.
        if (source.Format == plan.TargetFormat &&
            bitsPerPixel % 8 == 0 &&
            (sourceAlpha == plan.TargetAlphaFormat || !source.Format.HasAlpha))
        {
            var bytesPerPixel = bitsPerPixel / 8;
            var rowBytes = region.Width * bytesPerPixel;

            for (var y = 0; y < region.Height; y++)
            {
                var sourceRow = source.Address + (nint)(region.Y + y) * source.RowBytes + region.X * bytesPerPixel;
                var destRow = destAddress + (nint)y * destRowBytes;

                Buffer.MemoryCopy((void*)sourceRow, (void*)destRow, rowBytes, rowBytes);
            }

            return;
        }

        var layout = new SourceRowLayout(source.Format, region);

        using var readStaging = allocator.Rent(layout.ReadWidth * sizeof(Rgba8888Pixel));

        var readBuffer = new Span<Rgba8888Pixel>((void*)readStaging.Address, layout.ReadWidth);

        for (var y = 0; y < region.Height; y++)
        {
            var row = ReadSourceRow(source, region, layout, y, readBuffer);

            PixelFormatTranscoder.WriteRow(row, destAddress + (nint)y * destRowBytes, plan.TargetFormat, plan.TargetAlphaFormat, sourceAlpha);
        }
    }

    private static void RunNearest(ILockedFramebuffer source, PixelRect region, in FusedPlanExecution plan,
        IntPtr destAddress, int destRowBytes, IBitmapMemoryAllocator allocator)
    {
        var layout = new SourceRowLayout(source.Format, region);
        var sourceAlpha = GetEffectiveSourceAlpha(source);
        var srcWidth = region.Width;
        var srcHeight = region.Height;
        var destWidth = plan.TargetSize.Width;
        var destHeight = plan.TargetSize.Height;

        // Nearest sampling copies whole pixels, so no premultiplication is needed.
        var columnMap = new int[destWidth];

        for (var x = 0; x < destWidth; x++)
        {
            columnMap[x] = Math.Min(srcWidth - 1, (int)((x + 0.5) * srcWidth / destWidth));
        }

        using var readStaging = allocator.Rent(layout.ReadWidth * sizeof(Rgba8888Pixel));
        using var destStaging = allocator.Rent(destWidth * sizeof(Rgba8888Pixel));

        var readBuffer = new Span<Rgba8888Pixel>((void*)readStaging.Address, layout.ReadWidth);
        var destRow = new Span<Rgba8888Pixel>((void*)destStaging.Address, destWidth);

        var loadedRow = -1;
        Span<Rgba8888Pixel> row = default;

        for (var y = 0; y < destHeight; y++)
        {
            var sourceY = Math.Min(srcHeight - 1, (int)((y + 0.5) * srcHeight / destHeight));

            if (sourceY != loadedRow)
            {
                row = ReadSourceRow(source, region, layout, sourceY, readBuffer);
                loadedRow = sourceY;
            }

            for (var x = 0; x < destWidth; x++)
            {
                destRow[x] = row[columnMap[x]];
            }

            PixelFormatTranscoder.WriteRow(destRow, destAddress + (nint)y * destRowBytes, plan.TargetFormat, plan.TargetAlphaFormat, sourceAlpha);
        }
    }

    private static void RunBilinear(ILockedFramebuffer source, PixelRect region, in FusedPlanExecution plan,
        IntPtr destAddress, int destRowBytes, IBitmapMemoryAllocator allocator)
    {
        var layout = new SourceRowLayout(source.Format, region);
        var sourceAlpha = GetEffectiveSourceAlpha(source);
        var srcWidth = region.Width;
        var srcHeight = region.Height;
        var destWidth = plan.TargetSize.Width;
        var destHeight = plan.TargetSize.Height;

        // Interpolation mixes neighboring pixels; on unassociated alpha that bleeds the
        // color of fully transparent pixels into their neighbors, so rows are filtered
        // premultiplied and converted to the requested alpha on write.
        var premultiply = sourceAlpha == AlphaFormat.Unpremul;
        var rowAlpha = premultiply ? AlphaFormat.Premul : sourceAlpha;

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

        using var stagingA = allocator.Rent(layout.ReadWidth * sizeof(Rgba8888Pixel));
        using var stagingB = allocator.Rent(layout.ReadWidth * sizeof(Rgba8888Pixel));
        using var destStaging = allocator.Rent(destWidth * sizeof(Rgba8888Pixel));

        var bufferA = new Span<Rgba8888Pixel>((void*)stagingA.Address, layout.ReadWidth);
        var bufferB = new Span<Rgba8888Pixel>((void*)stagingB.Address, layout.ReadWidth);
        var destRow = new Span<Rgba8888Pixel>((void*)destStaging.Address, destWidth);

        var loadedA = -1;
        var loadedB = -1;
        Span<Rgba8888Pixel> rowA = default;
        Span<Rgba8888Pixel> rowB = default;

        for (var y = 0; y < destHeight; y++)
        {
            var position = MapToSource(y, srcHeight, destHeight);
            var topIndex = (int)position;
            var bottomIndex = Math.Min(topIndex + 1, srcHeight - 1);
            var fy = (float)(position - topIndex);

            Span<Rgba8888Pixel> top;

            if (loadedA == topIndex)
            {
                top = rowA;
            }
            else if (loadedB == topIndex)
            {
                top = rowB;
            }
            else if (loadedA == bottomIndex)
            {
                rowB = ReadFilteredRow(source, region, layout, topIndex, bufferB, premultiply);
                loadedB = topIndex;
                top = rowB;
            }
            else
            {
                rowA = ReadFilteredRow(source, region, layout, topIndex, bufferA, premultiply);
                loadedA = topIndex;
                top = rowA;
            }

            Span<Rgba8888Pixel> bottom;

            if (bottomIndex == topIndex)
            {
                bottom = top;
            }
            else if (loadedA == bottomIndex)
            {
                bottom = rowA;
            }
            else if (loadedB == bottomIndex)
            {
                bottom = rowB;
            }
            else if (loadedA == topIndex)
            {
                rowB = ReadFilteredRow(source, region, layout, bottomIndex, bufferB, premultiply);
                loadedB = bottomIndex;
                bottom = rowB;
            }
            else
            {
                rowA = ReadFilteredRow(source, region, layout, bottomIndex, bufferA, premultiply);
                loadedA = bottomIndex;
                bottom = rowA;
            }

            for (var x = 0; x < destWidth; x++)
            {
                var fx = fractionMap[x];
                var topLeft = top[leftMap[x]];
                var topRight = top[rightMap[x]];
                var bottomLeft = bottom[leftMap[x]];
                var bottomRight = bottom[rightMap[x]];

                destRow[x] = new Rgba8888Pixel(
                    Blend(topLeft.R, topRight.R, bottomLeft.R, bottomRight.R, fx, fy),
                    Blend(topLeft.G, topRight.G, bottomLeft.G, bottomRight.G, fx, fy),
                    Blend(topLeft.B, topRight.B, bottomLeft.B, bottomRight.B, fx, fy),
                    Blend(topLeft.A, topRight.A, bottomLeft.A, bottomRight.A, fx, fy));
            }

            PixelFormatTranscoder.WriteRow(destRow, destAddress + (nint)y * destRowBytes, plan.TargetFormat, plan.TargetAlphaFormat, rowAlpha);
        }
    }

    private static Span<Rgba8888Pixel> ReadFilteredRow(
        ILockedFramebuffer source, scoped in PixelRect region, scoped in SourceRowLayout layout, int rowIndex,
        Span<Rgba8888Pixel> buffer, bool premultiply)
    {
        var row = ReadSourceRow(source, region, layout, rowIndex, buffer);

        if (premultiply)
            PremultiplyRow(row);

        return row;
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

    private static byte Blend(byte topLeft, byte topRight, byte bottomLeft, byte bottomRight, float fx, float fy)
    {
        var top = topLeft + (topRight - topLeft) * fx;
        var bottom = bottomLeft + (bottomRight - bottomLeft) * fx;

        return (byte)(top + (bottom - top) * fy + 0.5f);
    }

    private static void RunBox(ILockedFramebuffer source, PixelRect region, in FusedPlanExecution plan,
        IntPtr destAddress, int destRowBytes, IBitmapMemoryAllocator allocator)
    {
        var layout = new SourceRowLayout(source.Format, region);
        var sourceAlpha = GetEffectiveSourceAlpha(source);
        var srcWidth = region.Width;
        var srcHeight = region.Height;
        var destWidth = plan.TargetSize.Width;
        var destHeight = plan.TargetSize.Height;

        // Averaging unassociated alpha bleeds the color of fully transparent pixels into
        // their neighbors, so accumulation happens on premultiplied rows and the result
        // converts to the requested alpha on write.
        var premultiply = sourceAlpha == AlphaFormat.Unpremul;
        var rowAlpha = premultiply ? AlphaFormat.Premul : sourceAlpha;

        var scaleY = srcHeight / (double)destHeight;

        BuildCoverage(srcWidth, destWidth, out var segmentOffsets, out var segmentColumns, out var segmentWeights, out var columnWeightSums);

        using var readStaging = allocator.Rent(layout.ReadWidth * sizeof(Rgba8888Pixel));
        using var horizontalStaging = allocator.Rent(destWidth * 4 * sizeof(double));
        using var currentStaging = allocator.Rent(destWidth * 4 * sizeof(double));
        using var nextStaging = allocator.Rent(destWidth * 4 * sizeof(double));
        using var destStaging = allocator.Rent(destWidth * sizeof(Rgba8888Pixel));

        var readBuffer = new Span<Rgba8888Pixel>((void*)readStaging.Address, layout.ReadWidth);
        var horizontal = new Span<double>((void*)horizontalStaging.Address, destWidth * 4);
        var current = new Span<double>((void*)currentStaging.Address, destWidth * 4);
        var next = new Span<double>((void*)nextStaging.Address, destWidth * 4);
        var destRow = new Span<Rgba8888Pixel>((void*)destStaging.Address, destWidth);

        current.Clear();
        next.Clear();

        var currentWeight = 0d;
        var nextWeight = 0d;
        var destY = 0;

        for (var sourceY = 0; sourceY < srcHeight && destY < destHeight; sourceY++)
        {
            var row = ReadFilteredRow(source, region, layout, sourceY, readBuffer, premultiply);

            // Reduce the source row horizontally into raw weighted channel sums.
            horizontal.Clear();

            for (var x = 0; x < destWidth; x++)
            {
                var accumulatorIndex = x * 4;

                for (var segment = segmentOffsets[x]; segment < segmentOffsets[x + 1]; segment++)
                {
                    var pixel = row[segmentColumns[segment]];
                    var weight = segmentWeights[segment];

                    horizontal[accumulatorIndex] += pixel.R * weight;
                    horizontal[accumulatorIndex + 1] += pixel.G * weight;
                    horizontal[accumulatorIndex + 2] += pixel.B * weight;
                    horizontal[accumulatorIndex + 3] += pixel.A * weight;
                }
            }

            // Distribute the reduced row over the destination rows it covers. With a
            // vertical scale factor of at least one it covers at most two of them.
            var rowTop = (double)sourceY;
            var rowBottom = sourceY + 1d;
            var currentBottom = (destY + 1) * scaleY;
            var currentOverlap = Math.Min(rowBottom, currentBottom) - Math.Max(rowTop, destY * scaleY);

            if (currentOverlap > 0)
            {
                Accumulate(current, horizontal, currentOverlap);
                currentWeight += currentOverlap;
            }

            if (rowBottom > currentBottom && destY + 1 < destHeight)
            {
                var nextOverlap = Math.Min(rowBottom, currentBottom + scaleY) - currentBottom;

                if (nextOverlap > 0)
                {
                    Accumulate(next, horizontal, nextOverlap);
                    nextWeight += nextOverlap;
                }
            }

            // Emit the destination row once its source span is complete; the last source
            // row also flushes a row left incomplete by floating point shortfall.
            if (rowBottom >= currentBottom - CoverageEpsilon || sourceY == srcHeight - 1)
            {
                EmitBoxRow(current, currentWeight, columnWeightSums, destRow);

                PixelFormatTranscoder.WriteRow(destRow, destAddress + (nint)destY * destRowBytes, plan.TargetFormat, plan.TargetAlphaFormat, rowAlpha);

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

    private static void Accumulate(Span<double> accumulator, ReadOnlySpan<double> row, double weight)
    {
        for (var i = 0; i < accumulator.Length; i++)
        {
            accumulator[i] += row[i] * weight;
        }
    }

    private static void EmitBoxRow(ReadOnlySpan<double> accumulator, double rowWeight, double[] columnWeightSums, Span<Rgba8888Pixel> destRow)
    {
        for (var x = 0; x < destRow.Length; x++)
        {
            var total = columnWeightSums[x] * rowWeight;

            if (total <= 0)
            {
                destRow[x] = default;
                continue;
            }

            var index = x * 4;

            destRow[x] = new Rgba8888Pixel(
                (byte)(accumulator[index] / total + 0.5),
                (byte)(accumulator[index + 1] / total + 0.5),
                (byte)(accumulator[index + 2] / total + 0.5),
                (byte)(accumulator[index + 3] / total + 0.5));
        }
    }

    // Builds a CSR-style coverage table: for each destination column the covered source
    // columns with their (possibly fractional) weights, plus the per-column weight sum
    // used for normalization.
    private static void BuildCoverage(int sourceLength, int targetLength,
        out int[] segmentOffsets, out int[] segmentColumns, out double[] segmentWeights, out double[] weightSums)
    {
        var scale = sourceLength / (double)targetLength;

        segmentOffsets = new int[targetLength + 1];

        for (var x = 0; x < targetLength; x++)
        {
            GetCoverage(x, scale, sourceLength, out var first, out var last);

            segmentOffsets[x + 1] = segmentOffsets[x] + last - first + 1;
        }

        segmentColumns = new int[segmentOffsets[targetLength]];
        segmentWeights = new double[segmentOffsets[targetLength]];
        weightSums = new double[targetLength];

        for (var x = 0; x < targetLength; x++)
        {
            GetCoverage(x, scale, sourceLength, out var first, out var last);

            var start = x * scale;
            var end = start + scale;
            var offset = segmentOffsets[x];

            for (var column = first; column <= last; column++)
            {
                var weight = Math.Max(0, Math.Min(end, column + 1) - Math.Max(start, column));

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
