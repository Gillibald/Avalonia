using System;
using System.Collections.Generic;
using System.Threading;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// One RGBA page of an <see cref="LcdRunAtlas"/>: <see cref="LcdRunAtlas.PageWidth"/>
    /// pixels per row, rows grown on demand, in a pinned array a backend can wrap as an image
    /// without a copy.
    /// </summary>
    internal sealed class LcdAtlasPage
    {
        internal readonly List<(int Y, int Height, int X)> Shelves = new();

        internal LcdAtlasPage(int height)
        {
            Pixels = GC.AllocateArray<byte>(LcdRunAtlas.PageWidth * 4 * height, pinned: true);
            Height = height;
        }

        /// <summary>Row-major RGBA, <see cref="LcdRunAtlas.PageWidth"/> pixels per row.</summary>
        public byte[] Pixels { get; private set; }

        /// <summary>The allocated rows.</summary>
        public int Height { get; private set; }

        /// <summary>The rows taken by shelves.</summary>
        internal int UsedHeight { get; set; }

        /// <summary>Entries placed on this page and not yet released.</summary>
        internal int Live { get; set; }

        /// <summary>Bumps with every write, so a backend knows when its image of the page is stale.</summary>
        public int Version { get; internal set; }

        /// <summary>The glyph cache budget frame of the last draw or placement that used this page.</summary>
        public long LastUse { get; internal set; }

        /// <summary>Whether the atlas dropped this page; its entries must be composed again.</summary>
        public bool IsEvicted { get; internal set; }

        /// <summary>The backend's image of the page, valid while <see cref="RealizedVersion"/> matches.</summary>
        public IDisposable? Realized { get; set; }

        /// <summary>The <see cref="Version"/> <see cref="Realized"/> was made from.</summary>
        public int RealizedVersion { get; set; }

        internal void Grow(int height)
        {
            var grown = GC.AllocateArray<byte>(LcdRunAtlas.PageWidth * 4 * height, pinned: true);

            // A fresh array rather than a resize in place: a backend image of the old version
            // can still be waiting to be read by a pending GPU upload.
            Pixels.AsSpan(0, LcdRunAtlas.PageWidth * 4 * UsedHeight).CopyTo(grown);
            Pixels = grown;
            Height = height;
            Version++;
        }
    }

    /// <summary>
    /// A subpixel run mask placed in an <see cref="LcdRunAtlas"/>: the page and the rectangle
    /// holding its stripe coverage. Disposing it releases its place.
    /// </summary>
    internal sealed class LcdAtlasEntry : IDisposable
    {
        private LcdRunAtlas? _atlas;

        internal LcdAtlasEntry(LcdRunAtlas atlas, LcdAtlasPage page, int x, int y, int width, int height)
        {
            _atlas = atlas;
            Page = page;
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public LcdAtlasPage Page { get; }

        public int X { get; }

        public int Y { get; }

        public int Width { get; }

        public int Height { get; }

        /// <summary>Whether the atlas dropped this entry's page, so it no longer holds the mask.</summary>
        public bool IsEvicted => Page.IsEvicted;

        /// <summary>Stamps the entry's page as used by the draw of <paramref name="frame"/>.</summary>
        public void Touch(long frame) => Page.LastUse = frame;

        public void Dispose()
        {
            Interlocked.Exchange(ref _atlas, null)?.Release(this);
        }
    }

    /// <summary>
    /// The storage of subpixel run masks on hardware GPU contexts: RGBA pages shared by every
    /// run, shelf-packed, so the masks of many runs can be drawn by one batched call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An entry holds a whole run's composed stripe coverage, as the run mask image it replaces
    /// did, so drawing it blends exactly the coverage the run's own image would. Glyph-sized
    /// entries could not: neighbouring glyphs sum their coverage before the per-channel blend,
    /// and the blend reads the destination, which one draw call reads once for all its sprites.
    /// </para>
    /// <para>
    /// A page whose entries are all released is dropped at once. Beyond that, the budget is
    /// enforced a page at a time: when a new shelf would take the atlas over its budget, the
    /// page used longest ago is dropped whole, and its entries report themselves evicted, so
    /// their runs compose them again. A page used by a frame still being drawn (see
    /// <see cref="GlyphCacheBudget.PinFloor"/>) is never dropped; if every page is in use the
    /// atlas grows past its budget instead.
    /// </para>
    /// </remarks>
    internal sealed class LcdRunAtlas
    {
        /// <summary>Page width in pixels: the OpenGL ES 3.0 guaranteed texture dimension.</summary>
        public const int PageWidth = 2048;

        /// <summary>Page height limit, the same guaranteed dimension.</summary>
        public const int MaxPageHeight = 2048;

        private const int RowQuantum = 64;

        private readonly object _lock = new();
        private readonly List<LcdAtlasPage> _pages = new();
        private readonly long _budget;
        private readonly int _maxPageHeight;
        private readonly GlyphCacheBudget _clock;
        private readonly GlyphCachePoolHandle _handle;
        private long _allocated;
        private long _evictions;

        /// <param name="budget">The budget the atlas charges its pages to.</param>
        public LcdRunAtlas(GlyphCacheBudget budget)
            : this(32L * 1024 * 1024, MaxPageHeight, budget)
        {
        }

        /// <param name="budgetBytes">The byte budget of all pages together, at least one page of 64 rows.</param>
        /// <param name="maxPageHeight">The row limit of a page, at most <see cref="MaxPageHeight"/>.</param>
        /// <param name="budget">
        /// The budget the atlas charges its pages to; <see cref="GlyphCacheBudget.Shared"/> when omitted.
        /// </param>
        public LcdRunAtlas(long budgetBytes, int maxPageHeight = MaxPageHeight, GlyphCacheBudget? budget = null)
        {
            _budget = Math.Max(budgetBytes, (long)PageWidth * 4 * RowQuantum);
            _maxPageHeight = Math.Clamp(maxPageHeight, RowQuantum, MaxPageHeight);
            _clock = budget ?? GlyphCacheBudget.Shared;
            _handle = _clock.Register(GlyphCachePoolKind.LcdAtlas, this);
        }

        /// <summary>The atlas every hardware GPU context places its subpixel run masks in.</summary>
        public static LcdRunAtlas Shared { get; } = new(32L * 1024 * 1024);

        /// <summary>Bytes of all page arrays.</summary>
        public long AllocatedBytes => Interlocked.Read(ref _allocated);

        /// <summary>Pages dropped to stay within the budget since construction.</summary>
        public long Evictions => Interlocked.Read(ref _evictions);

        /// <summary>A snapshot of the live pages; for diagnostics and tests.</summary>
        public LcdAtlasPage[] GetPages()
        {
            lock (_lock)
            {
                return _pages.ToArray();
            }
        }

        /// <summary>Whether a mask of this size fits a page.</summary>
        public bool Fits(int width, int height) => width <= PageWidth && height <= _maxPageHeight;

        /// <summary>
        /// The frame a draw or placement stamps the pages it uses with: pages stamped with a
        /// frame still being drawn are protected from eviction.
        /// </summary>
        public long Tick() => _clock.Frame;

        /// <summary>
        /// Places an RGBA mask of <paramref name="width"/> x <paramref name="height"/> pixels,
        /// rows <paramref name="width"/> * 4 bytes apart. Returns <c>null</c> when it does not
        /// fit a page.
        /// </summary>
        public LcdAtlasEntry? TryAdd(ReadOnlySpan<byte> rgba, int width, int height)
        {
            if (width <= 0 || height <= 0 || !Fits(width, height))
            {
                return null;
            }

            lock (_lock)
            {
                var tick = Tick();
                var (page, x, y) = Place(width, height, tick);

                for (var row = 0; row < height; row++)
                {
                    rgba.Slice(row * width * 4, width * 4)
                        .CopyTo(page.Pixels.AsSpan(((y + row) * PageWidth + x) * 4, width * 4));
                }

                page.Version++;
                page.Live++;
                page.LastUse = tick;

                return new LcdAtlasEntry(this, page, x, y, width, height);
            }
        }

        internal void Release(LcdAtlasEntry entry)
        {
            lock (_lock)
            {
                var page = entry.Page;

                if (page.IsEvicted)
                {
                    return;
                }

                page.Live--;

                // Entries cannot be freed one by one; a page with none left goes whole.
                if (page.Live == 0)
                {
                    Evict(page);
                }
            }
        }

        private (LcdAtlasPage Page, int X, int Y) Place(int width, int height, long tick)
        {
            // An open shelf of a similar height first, so short masks do not waste tall rows.
            foreach (var page in _pages)
            {
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
                if (page.UsedHeight + height <= _maxPageHeight)
                {
                    return (page, 0, OpenShelf(page, width, height, tick));
                }
            }

            var rows = RoundUp(height);

            MakeRoom((long)PageWidth * 4 * rows);

            var fresh = new LcdAtlasPage(rows);

            _pages.Add(fresh);
            Interlocked.Add(ref _allocated, fresh.Pixels.Length);
            _handle.Charge(fresh.Pixels.Length);

            return (fresh, 0, OpenShelf(fresh, width, height, tick));
        }

        private int OpenShelf(LcdAtlasPage page, int width, int height, long tick)
        {
            var y = page.UsedHeight;

            // The page is about to take this entry, so the room made for its growth must come
            // from other pages.
            page.LastUse = tick;

            if (y + height > page.Height)
            {
                var rows = Math.Min(_maxPageHeight, RoundUp(y + height));

                MakeRoom((long)PageWidth * 4 * (rows - page.Height));

                var before = page.Pixels.Length;

                page.Grow(rows);
                Interlocked.Add(ref _allocated, page.Pixels.Length - before);
                _handle.Charge(page.Pixels.Length - before);
            }

            page.Shelves.Add((y, height, width));
            page.UsedHeight = y + height;

            return y;
        }

        private static int RoundUp(int rows) => (rows + RowQuantum - 1) / RowQuantum * RowQuantum;

        /// <summary>Drops the least recently used pages until <paramref name="bytes"/> more fit the budget.</summary>
        private void MakeRoom(long bytes)
        {
            var pinFloor = _clock.PinFloor;

            while (_allocated + bytes > _budget)
            {
                LcdAtlasPage? victim = null;

                foreach (var page in _pages)
                {
                    if (page.LastUse < pinFloor && (victim is null || page.LastUse < victim.LastUse))
                    {
                        victim = page;
                    }
                }

                if (victim is null)
                {
                    return;
                }

                Evict(victim);
                Interlocked.Increment(ref _evictions);
            }
        }

        private void Evict(LcdAtlasPage page)
        {
            _pages.Remove(page);
            Interlocked.Add(ref _allocated, -page.Pixels.Length);
            _handle.Credit(page.Pixels.Length);

            page.IsEvicted = true;
            page.Realized?.Dispose();
            page.Realized = null;
        }
    }
}
