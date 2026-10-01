using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>The instruction set <see cref="GlyphRasterizer"/> accumulates and resolves coverage with.</summary>
    internal enum GlyphRasterizerPath : byte
    {
        Scalar,

        /// <summary>Four lanes, SSE4.1.</summary>
        Vector128,

        /// <summary>Eight lanes, AVX2.</summary>
        Vector256,
    }

    internal static partial class GlyphRasterizer
    {
        [ThreadStatic]
        private static CrossingQueue? t_crossings;

        /// <summary>Queues every segment for the vector walk and deposit.</summary>
        private readonly struct QueuedSegments : ISegmentSink
        {
            public QueuedSegments(CrossingQueue queue, bool wide)
            {
                Queue = queue;
                Wide = wide;
            }

            public CrossingQueue Queue { get; }

            public bool Wide { get; }

            public void Add(float x0, float y0, float x1, float y1, Span<float> cells, int width, int height)
                => Queue.PushSegment(x0, y0, x1, y1, cells, width, height, Wide);
        }

        /// <summary>
        /// The line segments of a path and their scanline crossings, in the order the scalar
        /// path walks and deposits them. The vector walk sets up several segments at once and
        /// computes several rows of a segment at once; the vector deposit computes the cell
        /// contributions of several crossings at once. Each lane performs the scalar path's
        /// operations, and the crossings reach the cells one by one in queue order, so every
        /// cell receives the same values in the same order and holds the same sum.
        /// </summary>
        private sealed class CrossingQueue
        {
            private const int Capacity = 512;

            private const int SegmentCapacity = 256;

            private const int MaxLanes = 8;

            // Rows are stored a whole vector at a time, so the arrays have room for one past
            // the last crossing.
            private readonly float[] _xa = new float[Capacity + MaxLanes];
            private readonly float[] _xb = new float[Capacity + MaxLanes];
            private readonly float[] _area = new float[Capacity + MaxLanes];
            private readonly int[] _rowBase = new int[Capacity + MaxLanes];
            private int _count;

            private readonly float[] _segmentX0 = new float[SegmentCapacity];
            private readonly float[] _segmentY0 = new float[SegmentCapacity];
            private readonly float[] _segmentX1 = new float[SegmentCapacity];
            private readonly float[] _segmentY1 = new float[SegmentCapacity];
            private int _segments;

            // One chunk of set-up segments: the upper end (clipped to the mask's first row), the
            // lower end's y (clipped to its last), the slope, the direction and the rows swept.
            private readonly float[] _startX = new float[MaxLanes];
            private readonly float[] _startY = new float[MaxLanes];
            private readonly float[] _endY = new float[MaxLanes];
            private readonly float[] _slope = new float[MaxLanes];
            private readonly float[] _direction = new float[MaxLanes];
            private readonly int[] _firstRow = new int[MaxLanes];
            private readonly int[] _endRow = new int[MaxLanes];

            // A crossing deposits into its row's first cell when it lies left of the mask or is
            // clipped there, then into two cells when it is near vertical, or two per covered
            // column when it covers at most two columns. A crossing over more columns keeps its
            // clipped extent for the deposit across its columns.
            private const int Slots = 5;

            /// <summary>
            /// Cells past the mask that take the deposits a crossing does not use, one per slot
            /// and lane, so every lane adds all of its slots without a branch and no two lanes'
            /// unused slots share a cell.
            /// </summary>
            public const int SinkCells = Slots * MaxLanes;

            // One chunk's cell indices and values, slot by slot.
            private readonly int[] _indices = new int[Slots * MaxLanes];
            private readonly float[] _values = new float[Slots * MaxLanes];
            private readonly float[] _from = new float[MaxLanes];
            private readonly float[] _to = new float[MaxLanes];
            private readonly float[] _shared = new float[MaxLanes];
            private readonly float[] _invRun = new float[MaxLanes];
            private readonly int[] _column0 = new int[MaxLanes];
            private readonly int[] _column1 = new int[MaxLanes];

            public void PushSegment(float x0, float y0, float x1, float y1, Span<float> cells, int width, int height,
                bool wide)
            {
                if (_segments == SegmentCapacity)
                {
                    WalkSegments(cells, width, height, wide);
                }

                _segmentX0[_segments] = x0;
                _segmentY0[_segments] = y0;
                _segmentX1[_segments] = x1;
                _segmentY1[_segments] = y1;
                _segments++;
            }

            /// <summary>
            /// Walks and deposits everything queued into <paramref name="cells"/>, the mask's
            /// cells followed by <see cref="SinkCells"/> more, and empties the queues.
            /// </summary>
            public void Finish(Span<float> cells, int width, int height, bool wide)
            {
                WalkSegments(cells, width, height, wide);
                Flush(cells, width, height, wide);
            }

            private void WalkSegments(Span<float> cells, int width, int height, bool wide)
            {
                var i = 0;

                if (wide)
                {
                    for (; i + 8 <= _segments; i += 8)
                    {
                        EmitRows(cells, width, height, SetUp256(i, height), 8, wide);
                    }
                }

                for (; i + 4 <= _segments; i += 4)
                {
                    EmitRows(cells, width, height, SetUp128(i, height), 4, wide);
                }

                for (; i < _segments; i++)
                {
                    AddSegment(_segmentX0[i], _segmentY0[i], _segmentX1[i], _segmentY1[i], cells, width, height, wide);
                }

                _segments = 0;
            }

            /// <summary>
            /// The scalar segment set-up for eight segments from <paramref name="start"/>: drop
            /// horizontal, non-finite and off-mask segments, orient each downwards, take its
            /// slope, clip it to the mask's rows and find the rows it sweeps. Returns a bit per
            /// segment that sweeps rows.
            /// </summary>
            private uint SetUp256(int start, int height)
            {
                var x0 = Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_segmentX0), (nuint)start);
                var y0 = Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_segmentY0), (nuint)start);
                var x1 = Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_segmentX1), (nuint)start);
                var y1 = Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_segmentY1), (nuint)start);

                var zero = Vector256<float>.Zero;
                var bottom = Vector256.Create((float)height);

                var finite = Vector256.Equals(x0 - x0, zero) & Vector256.Equals(y0 - y0, zero) &
                             Vector256.Equals(x1 - x1, zero) & Vector256.Equals(y1 - y1, zero);
                var down = Vector256.LessThan(y0, y1);
                var startX = Vector256.ConditionalSelect(down, x0, x1);
                var startY = Vector256.ConditionalSelect(down, y0, y1);
                var endX = Vector256.ConditionalSelect(down, x1, x0);
                var endY = Vector256.ConditionalSelect(down, y1, y0);
                var direction = Vector256.ConditionalSelect(down, Vector256.Create(1f), Vector256.Create(-1f));
                var swept = finite & ~Vector256.Equals(y0, y1) & Vector256.GreaterThan(endY, zero) &
                            Vector256.LessThan(startY, bottom);

                var slope = (endX - startX) / (endY - startY);
                var clipTop = Vector256.LessThan(startY, zero);

                startX = Vector256.ConditionalSelect(clipTop, startX + slope * -startY, startX);
                startY = Vector256.ConditionalSelect(clipTop, zero, startY);
                endY = Vector256.ConditionalSelect(Vector256.GreaterThan(endY, bottom), bottom, endY);

                startX.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_startX));
                startY.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_startY));
                endY.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_endY));
                slope.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_slope));
                direction.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_direction));
                Avx.ConvertToVector256Int32WithTruncation(startY)
                    .StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_firstRow));
                Avx2.Min(Avx.ConvertToVector256Int32WithTruncation(Avx.Ceiling(endY)), Vector256.Create(height))
                    .StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_endRow));

                return swept.ExtractMostSignificantBits();
            }

            /// <summary>The <see cref="SetUp256"/> of four segments.</summary>
            private uint SetUp128(int start, int height)
            {
                var x0 = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_segmentX0), (nuint)start);
                var y0 = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_segmentY0), (nuint)start);
                var x1 = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_segmentX1), (nuint)start);
                var y1 = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_segmentY1), (nuint)start);

                var zero = Vector128<float>.Zero;
                var bottom = Vector128.Create((float)height);

                var finite = Vector128.Equals(x0 - x0, zero) & Vector128.Equals(y0 - y0, zero) &
                             Vector128.Equals(x1 - x1, zero) & Vector128.Equals(y1 - y1, zero);
                var down = Vector128.LessThan(y0, y1);
                var startX = Vector128.ConditionalSelect(down, x0, x1);
                var startY = Vector128.ConditionalSelect(down, y0, y1);
                var endX = Vector128.ConditionalSelect(down, x1, x0);
                var endY = Vector128.ConditionalSelect(down, y1, y0);
                var direction = Vector128.ConditionalSelect(down, Vector128.Create(1f), Vector128.Create(-1f));
                var swept = finite & ~Vector128.Equals(y0, y1) & Vector128.GreaterThan(endY, zero) &
                            Vector128.LessThan(startY, bottom);

                var slope = (endX - startX) / (endY - startY);
                var clipTop = Vector128.LessThan(startY, zero);

                startX = Vector128.ConditionalSelect(clipTop, startX + slope * -startY, startX);
                startY = Vector128.ConditionalSelect(clipTop, zero, startY);
                endY = Vector128.ConditionalSelect(Vector128.GreaterThan(endY, bottom), bottom, endY);

                startX.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_startX));
                startY.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_startY));
                endY.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_endY));
                slope.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_slope));
                direction.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_direction));
                Sse2.ConvertToVector128Int32WithTruncation(startY)
                    .StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_firstRow));
                Sse41.Min(Sse2.ConvertToVector128Int32WithTruncation(Sse41.Ceiling(endY)), Vector128.Create(height))
                    .StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_endRow));

                return swept.ExtractMostSignificantBits();
            }

            /// <summary>
            /// Queues the crossings of a chunk of set-up segments, segment by segment and row by
            /// row, several rows of a segment at a time.
            /// </summary>
            private void EmitRows(Span<float> cells, int width, int height, uint swept, int segments, bool wide)
            {
                var lanes = wide ? 8 : 4;

                for (var segment = 0; segment < segments; segment++)
                {
                    if ((swept & (1u << segment)) == 0)
                    {
                        continue;
                    }

                    var x0 = _startX[segment];
                    var y0 = _startY[segment];
                    var y1 = _endY[segment];
                    var dxdy = _slope[segment];
                    var dir = _direction[segment];
                    var end = _endRow[segment];

                    for (var row = _firstRow[segment]; row < end; row += lanes)
                    {
                        if (_count + lanes > Capacity)
                        {
                            Flush(cells, width, height, wide);
                        }

                        var rows = Math.Min(lanes, end - row);
                        var queued = wide
                            ? TryQueueRows256(x0, y0, y1, dxdy, dir, row, rows, width)
                            : TryQueueRows128(x0, y0, y1, dxdy, dir, row, rows, width);

                        if (!queued)
                        {
                            QueueRows(x0, y0, y1, dxdy, dir, row, row + rows, width);
                        }
                    }
                }
            }

            /// <summary>
            /// Queues the crossings of up to eight rows of a segment from <paramref name="row"/>,
            /// the rows of the scalar walk. Returns <c>false</c>, queueing nothing, when one of
            /// them sweeps no height, which the scalar walk skips.
            /// </summary>
            private bool TryQueueRows256(float x0, float y0, float y1, float dxdy, float dir, int row, int rows,
                int width)
            {
                var index = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7);
                var current = Vector256.Create(row) + index;
                var top = Avx.ConvertToVector256Single(current);
                var bottom = Avx.ConvertToVector256Single(current + Vector256.Create(1));
                var start = Vector256.Create(y0);
                var end = Vector256.Create(y1);

                // The scalar MathF.Max and MathF.Min: y0 is finite and not below zero and the row
                // bounds are whole numbers, so only the two zeros could tell the comparisons
                // apart, and both pick the positive one.
                var ya = Vector256.ConditionalSelect(Vector256.GreaterThan(start, top), start, top);
                var yb = Vector256.ConditionalSelect(Vector256.LessThan(end, bottom), end, bottom);
                var dy = yb - ya;
                var used = Avx2.CompareGreaterThan(Vector256.Create(rows), index);

                if ((Vector256.AndNot(used, Vector256.GreaterThan(dy, Vector256<float>.Zero).AsInt32())
                        .ExtractMostSignificantBits()) != 0)
                {
                    return false;
                }

                var x = Vector256.Create(x0);
                var slope = Vector256.Create(dxdy);

                (x + slope * (ya - start)).StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_xa), (nuint)_count);
                (x + slope * (yb - start)).StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_xb), (nuint)_count);
                (Vector256.Create(dir) * dy).StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_area), (nuint)_count);
                Avx2.MultiplyLow(current, Vector256.Create(width))
                    .StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_rowBase), (nuint)_count);

                _count += rows;

                return true;
            }

            /// <summary>The <see cref="TryQueueRows256"/> of up to four rows.</summary>
            private bool TryQueueRows128(float x0, float y0, float y1, float dxdy, float dir, int row, int rows,
                int width)
            {
                var index = Vector128.Create(0, 1, 2, 3);
                var current = Vector128.Create(row) + index;
                var top = Sse2.ConvertToVector128Single(current);
                var bottom = Sse2.ConvertToVector128Single(current + Vector128.Create(1));
                var start = Vector128.Create(y0);
                var end = Vector128.Create(y1);

                var ya = Vector128.ConditionalSelect(Vector128.GreaterThan(start, top), start, top);
                var yb = Vector128.ConditionalSelect(Vector128.LessThan(end, bottom), end, bottom);
                var dy = yb - ya;
                var used = Sse2.CompareGreaterThan(Vector128.Create(rows), index);

                if ((Vector128.AndNot(used, Vector128.GreaterThan(dy, Vector128<float>.Zero).AsInt32())
                        .ExtractMostSignificantBits()) != 0)
                {
                    return false;
                }

                var x = Vector128.Create(x0);
                var slope = Vector128.Create(dxdy);

                (x + slope * (ya - start)).StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_xa), (nuint)_count);
                (x + slope * (yb - start)).StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_xb), (nuint)_count);
                (Vector128.Create(dir) * dy).StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_area), (nuint)_count);
                Sse41.MultiplyLow(current, Vector128.Create(width))
                    .StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_rowBase), (nuint)_count);

                _count += rows;

                return true;
            }

            /// <summary>The scalar walk's rows of a set-up segment from <paramref name="row"/> to <paramref name="end"/>.</summary>
            private void QueueRows(float x0, float y0, float y1, float dxdy, float dir, int row, int end, int width)
            {
                for (var iy = row; iy < end; iy++)
                {
                    float top = iy;
                    float bottom = iy + 1;
                    var ya = y0 > top ? y0 : top;
                    var yb = y1 < bottom ? y1 : bottom;
                    var dy = yb - ya;

                    if (dy <= 0f)
                    {
                        continue;
                    }

                    _xa[_count] = x0 + dxdy * (ya - y0);
                    _xb[_count] = x0 + dxdy * (yb - y0);
                    _area[_count] = dir * dy;
                    _rowBase[_count] = iy * width;
                    _count++;
                }
            }

            /// <summary>The scalar path's segment walk, queueing crossings instead of depositing them.</summary>
            private void AddSegment(float x0, float y0, float x1, float y1, Span<float> cells, int width, int height,
                bool wide)
            {
                if (y0 == y1 || !float.IsFinite(x0) || !float.IsFinite(y0) || !float.IsFinite(x1) || !float.IsFinite(y1))
                {
                    return;
                }

                float dir;

                if (y0 < y1)
                {
                    dir = 1f;
                }
                else
                {
                    (x0, x1) = (x1, x0);
                    (y0, y1) = (y1, y0);
                    dir = -1f;
                }

                if (y1 <= 0f || y0 >= height)
                {
                    return;
                }

                var dxdy = (x1 - x0) / (y1 - y0);

                if (y0 < 0f)
                {
                    x0 += dxdy * -y0;
                    y0 = 0f;
                }

                if (y1 > height)
                {
                    x1 = x0 + dxdy * (height - y0);
                    y1 = height;
                }

                var iy0 = (int)y0;
                var iyEnd = (int)MathF.Ceiling(y1);

                if (iyEnd > height)
                {
                    iyEnd = height;
                }

                for (var iy = iy0; iy < iyEnd; iy++)
                {
                    // The scalar path's MathF.Max and MathF.Min: y0 is finite and not below zero
                    // and the row bounds are whole numbers, so only the two zeros could tell the
                    // comparisons apart, and both pick the positive one.
                    float top = iy;
                    float bottom = iy + 1;
                    var ya = y0 > top ? y0 : top;
                    var yb = y1 < bottom ? y1 : bottom;
                    var dy = yb - ya;

                    if (dy <= 0f)
                    {
                        continue;
                    }

                    if (_count == Capacity)
                    {
                        Flush(cells, width, height, wide);
                    }

                    _xa[_count] = x0 + dxdy * (ya - y0);
                    _xb[_count] = x0 + dxdy * (yb - y0);
                    _area[_count] = dir * dy;
                    _rowBase[_count] = iy * width;
                    _count++;
                }
            }

            /// <summary>Deposits the queued crossings and empties the crossing queue.</summary>
            private void Flush(Span<float> cells, int width, int height, bool wide)
            {
                var sink = width * height;

                var i = 0;

                if (wide)
                {
                    for (; i + 8 <= _count; i += 8)
                    {
                        Deposit256(cells, width, sink, i);
                    }
                }

                for (; i + 4 <= _count; i += 4)
                {
                    Deposit128(cells, width, sink, i, wide);
                }

                for (; i < _count; i++)
                {
                    AccumulateRow(cells, _rowBase[i], width, _xa[i], _xb[i], _area[i]);
                }

                _count = 0;
            }

            /// <summary>
            /// The deposits of eight crossings from <paramref name="start"/>. Each lane follows
            /// the scalar <see cref="AccumulateRow"/>: order the crossing's ends, drop it right of
            /// the mask, deposit it on the row's first cell left of the mask, clip it to the mask
            /// (depositing the clipped-off part on the first cell), then split a near-vertical
            /// crossing between two cells by its position, or give each covered column its area
            /// share by the column's covered midpoint. A lane with non-finite inputs deposits
            /// through the scalar code.
            /// </summary>
            private void Deposit256(Span<float> cells, int width, int sink, int start)
            {
                var xa = Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_xa), (nuint)start);
                var xb = Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_xb), (nuint)start);
                var area = Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_area), (nuint)start);

                var zero = Vector256<float>.Zero;
                var one = Vector256.Create(1f);
                var half = Vector256.Create(0.5f);
                var right = Vector256.Create((float)width);
                var columns = Vector256.Create(width);
                var lastColumn = Vector256.Create(width - 1);
                var oneColumn = Vector256.Create(1);

                var finite = Vector256.Equals(xa - xa, zero) & Vector256.Equals(xb - xb, zero) &
                             Vector256.Equals(area - area, zero);

                var swap = Vector256.GreaterThan(xa, xb);
                var lo = Vector256.ConditionalSelect(swap, xb, xa);
                var hi = Vector256.ConditionalSelect(swap, xa, xb);

                var leftOfMask = Vector256.LessThanOrEqual(hi, zero);
                var inside = Vector256.AndNot(Vector256.LessThan(lo, right), leftOfMask);

                var clipLeft = inside & Vector256.LessThan(lo, zero);
                var clipRight = inside & Vector256.GreaterThan(hi, right);
                var f = zero;
                var clippedArea = area;
                var from = lo;
                var shared = area;

                // Only crossings at the mask's edges clip, so most chunks skip the divisions.
                if (clipLeft.ExtractMostSignificantBits() != 0)
                {
                    f = -lo / (hi - lo);
                    clippedArea = Vector256.ConditionalSelect(clipLeft, area * (one - f), area);
                    from = Vector256.ConditionalSelect(clipLeft, zero, lo);
                    shared = clippedArea;
                }

                if (clipRight.ExtractMostSignificantBits() != 0)
                {
                    shared = Vector256.ConditionalSelect(clipRight, clippedArea * ((right - from) / (hi - from)),
                        clippedArea);
                }

                var to = Vector256.ConditionalSelect(clipRight, right, hi);
                var run = to - from;
                var vertical = inside & Vector256.LessThan(run, Vector256.Create(1f / 1024f));
                var general = Vector256.AndNot(inside, vertical);

                var x = half * (from + to);
                var column = Avx2.Min(Avx.ConvertToVector256Int32WithTruncation(x), lastColumn);
                var fraction = x - Avx.ConvertToVector256Single(column);
                var columnNext = column + oneColumn;
                var verticalNext = Avx2.CompareGreaterThan(columns, columnNext).AsSingle();

                var invRun = one / run;
                var column0 = Avx.ConvertToVector256Int32WithTruncation(from);
                var column1 = Avx2.Min(Avx.ConvertToVector256Int32WithTruncation(to), lastColumn);
                var column0Next = column0 + oneColumn;
                var column0After = column0Next + oneColumn;
                var spansTwo = Avx2.CompareGreaterThan(column1, column0).AsSingle();
                var spansMore = Avx2.CompareGreaterThan(column1, column0Next).AsSingle();

                var left0 = Avx.ConvertToVector256Single(column0);
                var left1 = Avx.ConvertToVector256Single(column0Next);
                var left2 = Avx.ConvertToVector256Single(column0After);

                Column256(from, to, shared, invRun, left0, left1, half, one, out var a0, out var b0, out var covered0);
                Column256(from, to, shared, invRun, left1, left2, half, one, out var a1, out var b1, out var covered1);

                var fallback = Vector256.AndNot(Vector256<float>.AllBitsSet, finite);
                var across = Vector256.AndNot(general & spansMore, fallback);
                var two = Vector256.AndNot(general, spansMore);

                var firstSlot = Vector256.AndNot(leftOfMask | clipLeft, fallback);
                var slot1 = Vector256.AndNot(vertical | (two & covered0), fallback);
                var slot2 = Vector256.AndNot((vertical & verticalNext) |
                    (two & covered0 & Avx2.CompareGreaterThan(columns, column0Next).AsSingle()), fallback);
                var slot3 = Vector256.AndNot(two & spansTwo & covered1, fallback);
                var slot4 = slot3 & Avx2.CompareGreaterThan(columns, column0After).AsSingle();

                var rowBase = Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_rowBase), (nuint)start);
                var unused = Vector256.Create(sink) + Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7);
                var special = fallback | across;

                ref var indices = ref MemoryMarshal.GetArrayDataReference(_indices);
                ref var values = ref MemoryMarshal.GetArrayDataReference(_values);

                Vector256.ConditionalSelect(firstSlot.AsInt32(), rowBase, unused).StoreUnsafe(ref indices);
                Vector256.ConditionalSelect(firstSlot, Vector256.ConditionalSelect(leftOfMask, area, area * f), zero)
                    .StoreUnsafe(ref values);

                slot1 = Vector256.AndNot(slot1, special);
                slot2 = Vector256.AndNot(slot2, special);
                slot3 = Vector256.AndNot(slot3, special);
                slot4 = Vector256.AndNot(slot4, special);

                Vector256.ConditionalSelect(slot1.AsInt32(),
                        rowBase + Vector256.ConditionalSelect(vertical.AsInt32(), column, column0),
                        unused + Vector256.Create(8))
                    .StoreUnsafe(ref indices, 8);
                Vector256.ConditionalSelect(slot1, Vector256.ConditionalSelect(vertical, shared * (one - fraction), a0), zero)
                    .StoreUnsafe(ref values, 8);
                Vector256.ConditionalSelect(slot2.AsInt32(),
                        rowBase + Vector256.ConditionalSelect(vertical.AsInt32(), columnNext, column0Next),
                        unused + Vector256.Create(16))
                    .StoreUnsafe(ref indices, 16);
                Vector256.ConditionalSelect(slot2, Vector256.ConditionalSelect(vertical, shared * fraction, b0), zero)
                    .StoreUnsafe(ref values, 16);
                Vector256.ConditionalSelect(slot3.AsInt32(), rowBase + column0Next, unused + Vector256.Create(24))
                    .StoreUnsafe(ref indices, 24);
                Vector256.ConditionalSelect(slot3, a1, zero).StoreUnsafe(ref values, 24);
                Vector256.ConditionalSelect(slot4.AsInt32(), rowBase + column0After, unused + Vector256.Create(32))
                    .StoreUnsafe(ref indices, 32);
                Vector256.ConditionalSelect(slot4, b1, zero).StoreUnsafe(ref values, 32);

                var acrossBits = across.ExtractMostSignificantBits();

                if (acrossBits != 0)
                {
                    from.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_from));
                    to.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_to));
                    shared.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_shared));
                    invRun.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_invRun));
                    column0.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_column0));
                    column1.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_column1));
                }

                Apply(cells, width, start, 8, fallback.ExtractMostSignificantBits(), acrossBits, wide: true);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void Column256(Vector256<float> from, Vector256<float> to, Vector256<float> shared,
                Vector256<float> invRun, Vector256<float> left, Vector256<float> next, Vector256<float> half,
                Vector256<float> one, out Vector256<float> current, out Vector256<float> following,
                out Vector256<float> covered)
            {
                // The scalar MathF.Max and MathF.Min: the crossing lies within the mask and the
                // column bounds are whole numbers, so only the two zeros could tell the
                // comparisons apart, and both pick the positive one.
                var cx0 = Vector256.ConditionalSelect(Vector256.GreaterThan(from, left), from, left);
                var cx1 = Vector256.ConditionalSelect(Vector256.LessThan(to, next), to, next);
                var w01 = cx1 - cx0;
                var subArea = shared * (w01 * invRun);
                var xMid = half * (cx0 + cx1) - left;

                covered = Vector256.GreaterThan(w01, Vector256<float>.Zero);
                current = subArea * (one - xMid);
                following = subArea * xMid;
            }

            /// <summary>The <see cref="Deposit256"/> of four crossings.</summary>
            private void Deposit128(Span<float> cells, int width, int sink, int start, bool wide)
            {
                var xa = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_xa), (nuint)start);
                var xb = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_xb), (nuint)start);
                var area = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_area), (nuint)start);

                var zero = Vector128<float>.Zero;
                var one = Vector128.Create(1f);
                var half = Vector128.Create(0.5f);
                var right = Vector128.Create((float)width);
                var columns = Vector128.Create(width);
                var lastColumn = Vector128.Create(width - 1);
                var oneColumn = Vector128.Create(1);

                var finite = Vector128.Equals(xa - xa, zero) & Vector128.Equals(xb - xb, zero) &
                             Vector128.Equals(area - area, zero);

                var swap = Vector128.GreaterThan(xa, xb);
                var lo = Vector128.ConditionalSelect(swap, xb, xa);
                var hi = Vector128.ConditionalSelect(swap, xa, xb);

                var leftOfMask = Vector128.LessThanOrEqual(hi, zero);
                var inside = Vector128.AndNot(Vector128.LessThan(lo, right), leftOfMask);

                var clipLeft = inside & Vector128.LessThan(lo, zero);
                var clipRight = inside & Vector128.GreaterThan(hi, right);
                var f = zero;
                var clippedArea = area;
                var from = lo;
                var shared = area;

                // Only crossings at the mask's edges clip, so most chunks skip the divisions.
                if (clipLeft.ExtractMostSignificantBits() != 0)
                {
                    f = -lo / (hi - lo);
                    clippedArea = Vector128.ConditionalSelect(clipLeft, area * (one - f), area);
                    from = Vector128.ConditionalSelect(clipLeft, zero, lo);
                    shared = clippedArea;
                }

                if (clipRight.ExtractMostSignificantBits() != 0)
                {
                    shared = Vector128.ConditionalSelect(clipRight, clippedArea * ((right - from) / (hi - from)),
                        clippedArea);
                }

                var to = Vector128.ConditionalSelect(clipRight, right, hi);
                var run = to - from;
                var vertical = inside & Vector128.LessThan(run, Vector128.Create(1f / 1024f));
                var general = Vector128.AndNot(inside, vertical);

                var x = half * (from + to);
                var column = Sse41.Min(Sse2.ConvertToVector128Int32WithTruncation(x), lastColumn);
                var fraction = x - Sse2.ConvertToVector128Single(column);
                var columnNext = column + oneColumn;
                var verticalNext = Sse2.CompareGreaterThan(columns, columnNext).AsSingle();

                var invRun = one / run;
                var column0 = Sse2.ConvertToVector128Int32WithTruncation(from);
                var column1 = Sse41.Min(Sse2.ConvertToVector128Int32WithTruncation(to), lastColumn);
                var column0Next = column0 + oneColumn;
                var column0After = column0Next + oneColumn;
                var spansTwo = Sse2.CompareGreaterThan(column1, column0).AsSingle();
                var spansMore = Sse2.CompareGreaterThan(column1, column0Next).AsSingle();

                var left0 = Sse2.ConvertToVector128Single(column0);
                var left1 = Sse2.ConvertToVector128Single(column0Next);
                var left2 = Sse2.ConvertToVector128Single(column0After);

                Column128(from, to, shared, invRun, left0, left1, half, one, out var a0, out var b0, out var covered0);
                Column128(from, to, shared, invRun, left1, left2, half, one, out var a1, out var b1, out var covered1);

                var fallback = Vector128.AndNot(Vector128<float>.AllBitsSet, finite);
                var across = Vector128.AndNot(general & spansMore, fallback);
                var two = Vector128.AndNot(general, spansMore);

                var firstSlot = Vector128.AndNot(leftOfMask | clipLeft, fallback);
                var slot1 = Vector128.AndNot(vertical | (two & covered0), fallback);
                var slot2 = Vector128.AndNot((vertical & verticalNext) |
                    (two & covered0 & Sse2.CompareGreaterThan(columns, column0Next).AsSingle()), fallback);
                var slot3 = Vector128.AndNot(two & spansTwo & covered1, fallback);
                var slot4 = slot3 & Sse2.CompareGreaterThan(columns, column0After).AsSingle();

                var rowBase = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_rowBase), (nuint)start);
                var unused = Vector128.Create(sink) + Vector128.Create(0, 1, 2, 3);
                var special = fallback | across;

                ref var indices = ref MemoryMarshal.GetArrayDataReference(_indices);
                ref var values = ref MemoryMarshal.GetArrayDataReference(_values);

                Vector128.ConditionalSelect(firstSlot.AsInt32(), rowBase, unused).StoreUnsafe(ref indices);
                Vector128.ConditionalSelect(firstSlot, Vector128.ConditionalSelect(leftOfMask, area, area * f), zero)
                    .StoreUnsafe(ref values);

                slot1 = Vector128.AndNot(slot1, special);
                slot2 = Vector128.AndNot(slot2, special);
                slot3 = Vector128.AndNot(slot3, special);
                slot4 = Vector128.AndNot(slot4, special);

                Vector128.ConditionalSelect(slot1.AsInt32(),
                        rowBase + Vector128.ConditionalSelect(vertical.AsInt32(), column, column0),
                        unused + Vector128.Create(4))
                    .StoreUnsafe(ref indices, 4);
                Vector128.ConditionalSelect(slot1, Vector128.ConditionalSelect(vertical, shared * (one - fraction), a0), zero)
                    .StoreUnsafe(ref values, 4);
                Vector128.ConditionalSelect(slot2.AsInt32(),
                        rowBase + Vector128.ConditionalSelect(vertical.AsInt32(), columnNext, column0Next),
                        unused + Vector128.Create(8))
                    .StoreUnsafe(ref indices, 8);
                Vector128.ConditionalSelect(slot2, Vector128.ConditionalSelect(vertical, shared * fraction, b0), zero)
                    .StoreUnsafe(ref values, 8);
                Vector128.ConditionalSelect(slot3.AsInt32(), rowBase + column0Next, unused + Vector128.Create(12))
                    .StoreUnsafe(ref indices, 12);
                Vector128.ConditionalSelect(slot3, a1, zero).StoreUnsafe(ref values, 12);
                Vector128.ConditionalSelect(slot4.AsInt32(), rowBase + column0After, unused + Vector128.Create(16))
                    .StoreUnsafe(ref indices, 16);
                Vector128.ConditionalSelect(slot4, b1, zero).StoreUnsafe(ref values, 16);

                var acrossBits = across.ExtractMostSignificantBits();

                if (acrossBits != 0)
                {
                    from.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_from));
                    to.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_to));
                    shared.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_shared));
                    invRun.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_invRun));
                    column0.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_column0));
                    column1.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_column1));
                }

                Apply(cells, width, start, 4, fallback.ExtractMostSignificantBits(), acrossBits, wide);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void Column128(Vector128<float> from, Vector128<float> to, Vector128<float> shared,
                Vector128<float> invRun, Vector128<float> left, Vector128<float> next, Vector128<float> half,
                Vector128<float> one, out Vector128<float> current, out Vector128<float> following,
                out Vector128<float> covered)
            {
                var cx0 = Vector128.ConditionalSelect(Vector128.GreaterThan(from, left), from, left);
                var cx1 = Vector128.ConditionalSelect(Vector128.LessThan(to, next), to, next);
                var w01 = cx1 - cx0;
                var subArea = shared * (w01 * invRun);
                var xMid = half * (cx0 + cx1) - left;

                covered = Vector128.GreaterThan(w01, Vector128<float>.Zero);
                current = subArea * (one - xMid);
                following = subArea * xMid;
            }

            /// <summary>
            /// Adds a chunk's deposits to the cells crossing by crossing in queue order, each
            /// crossing's deposits in the scalar order. A lane with non-finite inputs deposits
            /// through the scalar code, one covering more than two columns across its columns.
            /// </summary>
            private void Apply(Span<float> cells, int width, int start, int lanes, uint fallback, uint across,
                bool wide)
            {
                var indices = _indices;
                var values = _values;

                for (var lane = 0; lane < lanes; lane++)
                {
                    var bit = 1u << lane;

                    if (((fallback | across) & bit) != 0)
                    {
                        var index = start + lane;

                        if ((fallback & bit) != 0)
                        {
                            AccumulateRow(cells, _rowBase[index], width, _xa[index], _xb[index], _area[index]);
                            continue;
                        }

                        cells[indices[lane]] += values[lane];
                        DepositAcross(cells.Slice(_rowBase[index], width), _from[lane], _to[lane], _shared[lane],
                            _invRun[lane], _column0[lane], _column1[lane], wide);
                        continue;
                    }

                    cells[indices[lane]] += values[lane];
                    cells[indices[lanes + lane]] += values[lanes + lane];
                    cells[indices[2 * lanes + lane]] += values[2 * lanes + lane];
                    cells[indices[3 * lanes + lane]] += values[3 * lanes + lane];
                    cells[indices[4 * lanes + lane]] += values[4 * lanes + lane];
                }
            }
        }

        /// <summary>
        /// The scalar column loop of <see cref="AccumulateRow"/> for a crossing over three or
        /// more columns of <paramref name="row"/>, clipped to [<paramref name="from"/>,
        /// <paramref name="to"/>]. Column <c>ix</c> adds its share to cells <c>ix</c> and
        /// <c>ix + 1</c>, so each cell takes its left neighbour's second share, then its own
        /// first one. The columns strictly between the first and the last are fully covered,
        /// where both shares are half the column's area: the cells between them add that half
        /// twice, several cells at a time.
        /// </summary>
        private static void DepositAcross(Span<float> row, float from, float to, float shared, float invRun,
            int column0, int column1, bool wide)
        {
            var width = row.Length;

            ColumnShares(from, to, shared, invRun, column0, out var a, out var b, out var covered);

            if (covered)
            {
                row[column0] += a;
                row[column0 + 1] += b;
            }

            // The first middle column's shares, the same for every middle column.
            ColumnShares(from, to, shared, invRun, column0 + 1, out var half, out _, out _);

            row[column0 + 1] += half;

            var cell = column0 + 2;
            var end = column1;

            if (wide)
            {
                var halves = Vector256.Create(half);

                for (; cell + 8 <= end; cell += 8)
                {
                    ref var target = ref row[cell];
                    var sum = Vector256.LoadUnsafe(ref target) + halves;

                    (sum + halves).StoreUnsafe(ref target);
                }
            }

            var halves128 = Vector128.Create(half);

            for (; cell + 4 <= end; cell += 4)
            {
                ref var target = ref row[cell];
                var sum = Vector128.LoadUnsafe(ref target) + halves128;

                (sum + halves128).StoreUnsafe(ref target);
            }

            for (; cell < end; cell++)
            {
                row[cell] = row[cell] + half + half;
            }

            row[column1] += half;

            ColumnShares(from, to, shared, invRun, column1, out a, out b, out covered);

            if (covered)
            {
                row[column1] += a;

                if (column1 + 1 < width)
                {
                    row[column1 + 1] += b;
                }
            }
        }

        /// <summary>One iteration of the scalar column loop of <see cref="AccumulateRow"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ColumnShares(float from, float to, float shared, float invRun, int column,
            out float current, out float following, out bool covered)
        {
            float left = column;
            float next = column + 1;
            var cx0 = from > left ? from : left;
            var cx1 = to < next ? to : next;
            var w01 = cx1 - cx0;
            var subArea = shared * (w01 * invRun);
            var xMid = 0.5f * (cx0 + cx1) - left;

            covered = w01 > 0f;
            current = subArea * (1f - xMid);
            following = subArea * xMid;
        }

        // Turns a 4x4 block of bytes stored column by column into one stored row by row.
        private static readonly Vector128<byte> s_transposeBytes =
            Vector128.Create((byte)0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15);

        /// <summary>
        /// The scalar <see cref="Resolve"/> for several rows at once: each row's running sum
        /// sits in its own lane and takes the row's cells in column order, so it adds the same
        /// values in the same order. Blocks of cells are transposed into columns on the way in,
        /// and the coverage bytes back into rows on the way out.
        /// </summary>
        private static unsafe void ResolveVectorized(Span<float> cells, Span<byte> destination, int width, int height,
            int stride, bool evenOdd, bool aliased, bool wide)
        {
            fixed (float* cellsPointer = cells)
            fixed (byte* destinationPointer = destination)
            {
                var y = 0;

                if (wide)
                {
                    for (; y + 8 <= height; y += 8)
                    {
                        ResolveRows8(cellsPointer, destinationPointer, width, y, stride, evenOdd, aliased);
                    }
                }

                for (; y + 4 <= height; y += 4)
                {
                    ResolveRows4(cellsPointer, destinationPointer, width, y, stride, evenOdd, aliased);
                }

                for (; y < height; y++)
                {
                    ResolveTail(cellsPointer + y * width, destinationPointer + y * stride, 0, width, 0f, evenOdd,
                        aliased);
                }
            }
        }

        private static unsafe void ResolveRows8(float* cells, byte* destination, int width, int y, int stride,
            bool evenOdd, bool aliased)
        {
            var sum = Vector256<float>.Zero;
            var mask = Vector256.Create(s_transposeBytes, s_transposeBytes);
            var x = 0;

            for (; x + 8 <= width; x += 8)
            {
                var row = cells + y * width + x;

                var r0 = Avx.LoadVector256(row);
                var r1 = Avx.LoadVector256(row + width);
                var r2 = Avx.LoadVector256(row + 2 * width);
                var r3 = Avx.LoadVector256(row + 3 * width);
                var r4 = Avx.LoadVector256(row + 4 * width);
                var r5 = Avx.LoadVector256(row + 5 * width);
                var r6 = Avx.LoadVector256(row + 6 * width);
                var r7 = Avx.LoadVector256(row + 7 * width);

                var t0 = Avx.UnpackLow(r0, r1);
                var t1 = Avx.UnpackHigh(r0, r1);
                var t2 = Avx.UnpackLow(r2, r3);
                var t3 = Avx.UnpackHigh(r2, r3);
                var t4 = Avx.UnpackLow(r4, r5);
                var t5 = Avx.UnpackHigh(r4, r5);
                var t6 = Avx.UnpackLow(r6, r7);
                var t7 = Avx.UnpackHigh(r6, r7);

                var u0 = Avx.Shuffle(t0, t2, 0x44);
                var u1 = Avx.Shuffle(t0, t2, 0xEE);
                var u2 = Avx.Shuffle(t1, t3, 0x44);
                var u3 = Avx.Shuffle(t1, t3, 0xEE);
                var u4 = Avx.Shuffle(t4, t6, 0x44);
                var u5 = Avx.Shuffle(t4, t6, 0xEE);
                var u6 = Avx.Shuffle(t5, t7, 0x44);
                var u7 = Avx.Shuffle(t5, t7, 0xEE);

                // Column j of the block, one row per lane.
                var c0 = Avx.Permute2x128(u0, u4, 0x20);
                var c1 = Avx.Permute2x128(u1, u5, 0x20);
                var c2 = Avx.Permute2x128(u2, u6, 0x20);
                var c3 = Avx.Permute2x128(u3, u7, 0x20);
                var c4 = Avx.Permute2x128(u0, u4, 0x31);
                var c5 = Avx.Permute2x128(u1, u5, 0x31);
                var c6 = Avx.Permute2x128(u2, u6, 0x31);
                var c7 = Avx.Permute2x128(u3, u7, 0x31);

                sum += c0;
                var o0 = Coverage256(sum, evenOdd, aliased);
                sum += c1;
                var o1 = Coverage256(sum, evenOdd, aliased);
                sum += c2;
                var o2 = Coverage256(sum, evenOdd, aliased);
                sum += c3;
                var o3 = Coverage256(sum, evenOdd, aliased);
                sum += c4;
                var o4 = Coverage256(sum, evenOdd, aliased);
                sum += c5;
                var o5 = Coverage256(sum, evenOdd, aliased);
                sum += c6;
                var o6 = Coverage256(sum, evenOdd, aliased);
                sum += c7;
                var o7 = Coverage256(sum, evenOdd, aliased);

                // Packing keeps the 128-bit halves apart: columns 0-3 of rows 0-3 in the low
                // half, of rows 4-7 in the high one, stored column by column until the shuffle.
                var low = Avx2.Shuffle(Avx2.PackUnsignedSaturate(Avx2.PackSignedSaturate(o0, o1),
                    Avx2.PackSignedSaturate(o2, o3)), mask).AsInt32();
                var high = Avx2.Shuffle(Avx2.PackUnsignedSaturate(Avx2.PackSignedSaturate(o4, o5),
                    Avx2.PackSignedSaturate(o6, o7)), mask).AsInt32();

                // Each row's columns 0-3 next to its columns 4-7: rows 0, 1, 4, 5 and 2, 3, 6, 7.
                var even = Avx2.UnpackLow(low, high).AsUInt64();
                var odd = Avx2.UnpackHigh(low, high).AsUInt64();
                var target = destination + y * stride + x;

                Unsafe.WriteUnaligned(target, even.GetElement(0));
                Unsafe.WriteUnaligned(target + stride, even.GetElement(1));
                Unsafe.WriteUnaligned(target + 2 * stride, odd.GetElement(0));
                Unsafe.WriteUnaligned(target + 3 * stride, odd.GetElement(1));
                Unsafe.WriteUnaligned(target + 4 * stride, even.GetElement(2));
                Unsafe.WriteUnaligned(target + 5 * stride, even.GetElement(3));
                Unsafe.WriteUnaligned(target + 6 * stride, odd.GetElement(2));
                Unsafe.WriteUnaligned(target + 7 * stride, odd.GetElement(3));
            }

            if (x < width)
            {
                for (var r = 0; r < 8; r++)
                {
                    ResolveTail(cells + (y + r) * width, destination + (y + r) * stride, x, width, sum.GetElement(r),
                        evenOdd, aliased);
                }
            }
        }

        private static unsafe void ResolveRows4(float* cells, byte* destination, int width, int y, int stride,
            bool evenOdd, bool aliased)
        {
            var sum = Vector128<float>.Zero;
            var x = 0;

            for (; x + 4 <= width; x += 4)
            {
                var row = cells + y * width + x;

                var r0 = Sse.LoadVector128(row);
                var r1 = Sse.LoadVector128(row + width);
                var r2 = Sse.LoadVector128(row + 2 * width);
                var r3 = Sse.LoadVector128(row + 3 * width);

                var t0 = Sse.UnpackLow(r0, r1);
                var t1 = Sse.UnpackHigh(r0, r1);
                var t2 = Sse.UnpackLow(r2, r3);
                var t3 = Sse.UnpackHigh(r2, r3);

                sum += Sse.MoveLowToHigh(t0, t2);
                var o0 = Coverage128(sum, evenOdd, aliased);
                sum += Sse.MoveHighToLow(t2, t0);
                var o1 = Coverage128(sum, evenOdd, aliased);
                sum += Sse.MoveLowToHigh(t1, t3);
                var o2 = Coverage128(sum, evenOdd, aliased);
                sum += Sse.MoveHighToLow(t3, t1);
                var o3 = Coverage128(sum, evenOdd, aliased);

                var bytes = Ssse3.Shuffle(Sse2.PackUnsignedSaturate(Sse2.PackSignedSaturate(o0, o1),
                    Sse2.PackSignedSaturate(o2, o3)), s_transposeBytes).AsUInt32();
                var target = destination + y * stride + x;

                Unsafe.WriteUnaligned(target, bytes.GetElement(0));
                Unsafe.WriteUnaligned(target + stride, bytes.GetElement(1));
                Unsafe.WriteUnaligned(target + 2 * stride, bytes.GetElement(2));
                Unsafe.WriteUnaligned(target + 3 * stride, bytes.GetElement(3));
            }

            if (x < width)
            {
                for (var r = 0; r < 4; r++)
                {
                    ResolveTail(cells + (y + r) * width, destination + (y + r) * stride, x, width, sum.GetElement(r),
                        evenOdd, aliased);
                }
            }
        }

        /// <summary>The scalar <see cref="Resolve"/> of one row from column <paramref name="x"/>, continuing <paramref name="sum"/>.</summary>
        private static unsafe void ResolveTail(float* row, byte* destination, int x, int width, float sum,
            bool evenOdd, bool aliased)
        {
            for (; x < width; x++)
            {
                sum += row[x];

                float coverage;

                if (evenOdd)
                {
                    var t = sum - 2f * MathF.Round(sum * 0.5f);
                    coverage = MathF.Abs(t);
                }
                else
                {
                    coverage = MathF.Min(MathF.Abs(sum), 1f);
                }

                destination[x] = aliased
                    ? coverage >= 0.5f ? (byte)255 : (byte)0
                    : (byte)(coverage * 255f + 0.5f);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<int> Coverage256(Vector256<float> sum, bool evenOdd, bool aliased)
        {
            // The sums are finite, so the native minimum and rounding to nearest even are the
            // scalar MathF.Min and MathF.Round.
            var coverage = evenOdd
                ? Vector256.Abs(sum - Vector256.Create(2f) * Avx.RoundToNearestInteger(sum * Vector256.Create(0.5f)))
                : Avx.Min(Vector256.Abs(sum), Vector256.Create(1f));

            return aliased
                ? Vector256.GreaterThanOrEqual(coverage, Vector256.Create(0.5f)).AsInt32() & Vector256.Create(255)
                : Avx.ConvertToVector256Int32WithTruncation(coverage * Vector256.Create(255f) + Vector256.Create(0.5f));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<int> Coverage128(Vector128<float> sum, bool evenOdd, bool aliased)
        {
            var coverage = evenOdd
                ? Vector128.Abs(sum - Vector128.Create(2f) * Sse41.RoundToNearestInteger(sum * Vector128.Create(0.5f)))
                : Sse.Min(Vector128.Abs(sum), Vector128.Create(1f));

            return aliased
                ? Vector128.GreaterThanOrEqual(coverage, Vector128.Create(0.5f)).AsInt32() & Vector128.Create(255)
                : Sse2.ConvertToVector128Int32WithTruncation(coverage * Vector128.Create(255f) + Vector128.Create(0.5f));
        }
    }
}
