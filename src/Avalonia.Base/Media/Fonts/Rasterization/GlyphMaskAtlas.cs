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
    /// them as an image without a copy.
    /// </summary>
    internal sealed class GlyphAtlasPage
    {
        internal readonly List<GlyphMaskKey> Keys = new();
        internal readonly List<(int Y, int Height, int X)> Shelves = new();

        internal GlyphAtlasPage(int height)
        {
            Pixels = GC.AllocateArray<byte>(GlyphMaskAtlas.PageWidth * height, pinned: true);
            Height = height;
        }

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
    /// Entries cannot be freed one by one, so the budget is enforced a page at a time: when a
    /// new shelf would take the atlas over its budget, the page used longest ago is dropped
    /// whole. A page touched during the current draw (same tick) is never dropped, so building
    /// one run cannot evict the entries it placed a moment ago; if every page is in use the
    /// atlas grows past its budget instead.
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

        private const int RowQuantum = 64;

        private readonly object _lock = new();
        private readonly Dictionary<GlyphMaskKey, GlyphAtlasSlot> _slots = new();
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

        /// <summary>Looks up an entry and stamps its page with <paramref name="tick"/>.</summary>
        public bool TryGet(in GlyphMaskKey key, long tick, out GlyphAtlasSlot slot)
        {
            slot = default;
            return false;
        }

        /// <summary>
        /// Places <paramref name="mask"/> under <paramref name="key"/>, or returns the entry a
        /// racing caller placed first. Returns <c>false</c> when the mask does not fit a page.
        /// </summary>
        public bool TryAdd(in GlyphMaskKey key, GlyphMask mask, long tick, out GlyphAtlasSlot slot)
        {
            slot = default;
            return false;
        }
    }
}
