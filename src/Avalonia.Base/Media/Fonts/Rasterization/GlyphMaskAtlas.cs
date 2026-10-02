using System;
using System.Collections.Generic;
using System.Threading;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// A glyph mask's place in a <see cref="GlyphMaskAtlas"/>: the page, the rectangle holding
    /// its coverage, and the mask's placement relative to its snapped pen pixel.
    /// </summary>
    internal readonly struct GlyphAtlasSlot
    {
        public GlyphAtlasSlot(GlyphAtlasPage page, int x, int y, int width, int height, int left, int top)
        {
            Page = page;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Left = left;
            Top = top;
        }

        /// <summary>The page holding the coverage; <c>null</c> for a glyph without ink.</summary>
        public GlyphAtlasPage? Page { get; }

        public int X { get; }

        public int Y { get; }

        public int Width { get; }

        public int Height { get; }

        public int Left { get; }

        public int Top { get; }

        public bool IsEmpty => Page is null;
    }

    /// <summary>
    /// One A8 page of a <see cref="GlyphMaskAtlas"/>: <see cref="GlyphMaskAtlas.PageWidth"/>
    /// columns, rows grown on demand. The pixels live in a pinned array, so a backend can wrap
    /// them as an image without a copy. Every entry on a page holds its coverage through the
    /// same correction, <see cref="Bucket"/>.
    /// </summary>
    internal sealed class GlyphAtlasPage
    {
        internal readonly List<(GlyphMaskKey Key, int Bucket)> Keys = new();
        internal readonly List<(int Y, int Height, int X)> Shelves = new();

        internal GlyphAtlasPage(int height, int bucket)
        {
            Pixels = GC.AllocateArray<byte>(GlyphMaskAtlas.PageWidth * height, pinned: true);
            Height = height;
            Bucket = bucket;
        }

        /// <summary>
        /// The <see cref="MaskGamma"/> luminance bucket whose coverage correction the stored
        /// coverage went through, or <see cref="GlyphMaskAtlas.Uncorrected"/>.
        /// </summary>
        public int Bucket { get; }

        /// <summary>Row-major coverage, <see cref="GlyphMaskAtlas.PageWidth"/> bytes per row.</summary>
        public byte[] Pixels { get; private set; }

        /// <summary>The allocated rows.</summary>
        public int Height { get; private set; }

        /// <summary>The rows taken by shelves.</summary>
        internal int UsedHeight { get; set; }

        /// <summary>Bumps with every write, so a backend knows when its image of the page is stale.</summary>
        public int Version { get; internal set; }

        /// <summary>The atlas tick of the last draw or build that used this page.</summary>
        public long LastUse { get; internal set; }

        /// <summary>Whether the atlas dropped this page; sprites on it must be rebuilt.</summary>
        public bool IsEvicted { get; internal set; }

        /// <summary>The backend's image of the page, valid while <see cref="RealizedVersion"/> matches.</summary>
        public IDisposable? Realized { get; set; }

        /// <summary>The <see cref="Version"/> <see cref="Realized"/> was made from.</summary>
        public int RealizedVersion { get; set; }

        internal void Grow(int height)
        {
            var grown = GC.AllocateArray<byte>(GlyphMaskAtlas.PageWidth * height, pinned: true);

            // A fresh array rather than a resize in place: a backend image of the old version
            // can still be waiting to be read by a pending GPU upload.
            Pixels.AsSpan(0, GlyphMaskAtlas.PageWidth * UsedHeight).CopyTo(grown);
            Pixels = grown;
            Height = height;
            Version++;
        }
    }

    /// <summary>
    /// The storage of transformed glyph masks on GPU contexts: one set of A8 pages per typeface,
    /// shelf-packed, drawn by the backend with one batched call per run. Every entry keeps one
    /// empty column to its right and one empty row below it, so a batch drawn with bilinear
    /// sampling under a scaling or rotating transform never reads a neighbour's coverage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Coverage is stored already corrected by the <see cref="MaskGamma"/> table of a
    /// luminance bucket, and pages are keyed by that bucket, so a draw needs no correction
    /// stage and bilinear sampling interpolates corrected values. The correction is not linear:
    /// applied after sampling, it would act on blended coverage, which for dark text thins
    /// every edge a stretched draw softens. Text of one colour takes one bucket; colour glyph
    /// layers, which must not be corrected, take <see cref="Uncorrected"/> pages.
    /// </para>
    /// <para>
    /// Entries cannot be freed one by one, so the budget is enforced a page at a time: when a
    /// new shelf would take the atlas over its budget, the page used longest ago is dropped
    /// whole. A page touched during the current draw (same tick) is never dropped, so building
    /// one run cannot evict the entries it placed a moment ago; if every page is in use the
    /// atlas grows past its budget instead.
    /// </para>
    /// </remarks>
    internal sealed class GlyphMaskAtlas
    {
        /// <summary>Page width in pixels.</summary>
        public const int PageWidth = 1024;

        /// <summary>
        /// Page height limit: the OpenGL ES 3.0 guaranteed texture dimension, so a page uploads
        /// as one texture on every GPU backend.
        /// </summary>
        public const int MaxPageHeight = 2048;

        /// <summary>The bucket of pages holding coverage as rasterized, without correction.</summary>
        public const int Uncorrected = -1;

        private const int RowQuantum = 64;

        private readonly object _lock = new();
        private readonly Dictionary<(GlyphMaskKey Key, int Bucket), GlyphAtlasSlot> _slots = new();
        private readonly List<GlyphAtlasPage> _pages = new();
        private readonly int _budget;
        private long _allocated;
        private long _clock;
        private long _evictions;

        public GlyphMaskAtlas(int budgetBytes)
        {
            _budget = Math.Max(budgetBytes, PageWidth * RowQuantum);
        }

        /// <summary>The byte budget of all pages together.</summary>
        public int BudgetBytes => _budget;

        /// <summary>Number of entries, including memoised no-ink glyphs.</summary>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _slots.Count;
                }
            }
        }

        /// <summary>Bytes of all page arrays.</summary>
        public long AllocatedBytes => Interlocked.Read(ref _allocated);

        /// <summary>Pages dropped to stay within the budget since construction.</summary>
        public long Evictions => Interlocked.Read(ref _evictions);

        /// <summary>A snapshot of the live pages; for diagnostics and tests.</summary>
        public GlyphAtlasPage[] GetPages()
        {
            lock (_lock)
            {
                return _pages.ToArray();
            }
        }

        /// <summary>Whether a mask of this size fits a page with its gutters.</summary>
        public static bool Fits(int width, int height) => width + 1 <= PageWidth && height + 1 <= MaxPageHeight;

        /// <summary>
        /// Starts a draw or build: pages stamped with the returned tick are protected from
        /// eviction until a later tick is taken.
        /// </summary>
        public long Tick() => Interlocked.Increment(ref _clock);

        /// <summary>Looks up an uncorrected entry and stamps its page with <paramref name="tick"/>.</summary>
        public bool TryGet(in GlyphMaskKey key, long tick, out GlyphAtlasSlot slot)
            => TryGet(key, Uncorrected, tick, out slot);

        /// <summary>
        /// Looks up the entry holding the coverage of <paramref name="key"/> corrected for
        /// <paramref name="bucket"/>, and stamps its page with <paramref name="tick"/>.
        /// </summary>
        public bool TryGet(in GlyphMaskKey key, int bucket, long tick, out GlyphAtlasSlot slot)
        {
            lock (_lock)
            {
                if (!_slots.TryGetValue((key, bucket), out slot))
                {
                    GlyphRasterDiagnostics.CountAtlasLookup(hit: false);
                    return false;
                }

                GlyphRasterDiagnostics.CountAtlasLookup(hit: true);

                if (slot.Page is { } page)
                {
                    page.LastUse = tick;
                }

                return true;
            }
        }

        /// <summary>
        /// Places <paramref name="mask"/> uncorrected under <paramref name="key"/>, or returns the
        /// entry a racing caller placed first. Returns <c>false</c> when the mask does not fit a page.
        /// </summary>
        public bool TryAdd(in GlyphMaskKey key, GlyphMask mask, long tick, out GlyphAtlasSlot slot)
            => TryAdd(key, Uncorrected, mask, tick, out slot);

        /// <summary>
        /// Places <paramref name="mask"/> under <paramref name="key"/> on a page of
        /// <paramref name="bucket"/>, its coverage corrected by that bucket's table, or returns
        /// the entry a racing caller placed first. Returns <c>false</c> when the mask does not
        /// fit a page.
        /// </summary>
        public bool TryAdd(in GlyphMaskKey key, int bucket, GlyphMask mask, long tick, out GlyphAtlasSlot slot)
        {
            if (!mask.IsEmpty && !Fits(mask.Width, mask.Height))
            {
                slot = default;
                return false;
            }

            lock (_lock)
            {
                if (_slots.TryGetValue((key, bucket), out slot))
                {
                    if (slot.Page is { } existing)
                    {
                        existing.LastUse = tick;
                    }

                    return true;
                }

                if (mask.IsEmpty)
                {
                    slot = default;
                    _slots.Add((key, bucket), slot);
                    return true;
                }

                var (page, x, y) = Place(mask.Width + 1, mask.Height + 1, bucket, tick);

                Write(page, mask, x, y);
                page.Keys.Add((key, bucket));
                page.LastUse = tick;

                slot = new GlyphAtlasSlot(page, x, y, mask.Width, mask.Height, mask.Left, mask.Top);
                _slots.Add((key, bucket), slot);

                return true;
            }
        }

        private (GlyphAtlasPage Page, int X, int Y) Place(int width, int height, int bucket, long tick)
        {
            // An open shelf of a similar height first, so short glyphs do not waste tall rows.
            foreach (var page in _pages)
            {
                if (page.Bucket != bucket)
                {
                    continue;
                }

                var shelves = page.Shelves;

                for (var s = 0; s < shelves.Count; s++)
                {
                    var shelf = shelves[s];

                    if (shelf.Height >= height && shelf.Height <= height + height / 4 + 2 &&
                        shelf.X + width <= PageWidth)
                    {
                        shelves[s] = (shelf.Y, shelf.Height, shelf.X + width);
                        return (page, shelf.X, shelf.Y);
                    }
                }
            }

            foreach (var page in _pages)
            {
                if (page.Bucket == bucket && page.UsedHeight + height <= MaxPageHeight)
                {
                    return (page, 0, OpenShelf(page, width, height, tick));
                }
            }

            var rows = RoundUp(height);

            MakeRoom((long)PageWidth * rows, tick);

            var fresh = new GlyphAtlasPage(rows, bucket);

            _pages.Add(fresh);
            Interlocked.Add(ref _allocated, fresh.Pixels.Length);

            return (fresh, 0, OpenShelf(fresh, width, height, tick));
        }

        private int OpenShelf(GlyphAtlasPage page, int width, int height, long tick)
        {
            var y = page.UsedHeight;

            // The page is about to take this entry, so the room made for its growth must
            // come from other pages.
            page.LastUse = tick;

            if (y + height > page.Height)
            {
                var rows = Math.Min(MaxPageHeight, RoundUp(y + height));
                var growth = (long)PageWidth * (rows - page.Height);

                MakeRoom(growth, tick);

                var before = page.Pixels.Length;

                page.Grow(rows);
                Interlocked.Add(ref _allocated, page.Pixels.Length - before);
            }

            page.Shelves.Add((y, height, width));
            page.UsedHeight = y + height;

            return y;
        }

        private static int RoundUp(int rows) => (rows + RowQuantum - 1) / RowQuantum * RowQuantum;

        /// <summary>Drops the least recently used pages until <paramref name="bytes"/> more fit the budget.</summary>
        private void MakeRoom(long bytes, long tick)
        {
            while (_allocated + bytes > _budget)
            {
                GlyphAtlasPage? victim = null;

                foreach (var page in _pages)
                {
                    if (page.LastUse < tick && (victim is null || page.LastUse < victim.LastUse))
                    {
                        victim = page;
                    }
                }

                if (victim is null)
                {
                    return;
                }

                Evict(victim);
            }
        }

        private void Evict(GlyphAtlasPage page)
        {
            foreach (var key in page.Keys)
            {
                _slots.Remove(key);
            }

            _pages.Remove(page);
            Interlocked.Add(ref _allocated, -page.Pixels.Length);
            Interlocked.Increment(ref _evictions);

            page.IsEvicted = true;
            page.Realized?.Dispose();
            page.Realized = null;
        }

        private static void Write(GlyphAtlasPage page, GlyphMask mask, int x, int y)
        {
            var pixels = page.Pixels;

            for (var row = 0; row < mask.Height; row++)
            {
                var source = mask.Alpha.AsSpan(row * mask.Width, mask.Width);
                var target = pixels.AsSpan((y + row) * PageWidth + x, mask.Width);

                if (page.Bucket == Uncorrected)
                {
                    source.CopyTo(target);
                }
                else
                {
                    Correct(source, target, page.Bucket);
                }
            }

            page.Version++;
            GlyphRasterDiagnostics.CountAtlasPlacement();
        }

        /// <summary>Copies coverage through the <see cref="MaskGamma"/> table of <paramref name="bucket"/>.</summary>
        public static void Correct(ReadOnlySpan<byte> source, Span<byte> target, int bucket)
        {
            var table = MaskGamma.GetTable(bucket);

            for (var i = 0; i < source.Length; i++)
            {
                target[i] = table[source[i]];
            }
        }
    }
}
