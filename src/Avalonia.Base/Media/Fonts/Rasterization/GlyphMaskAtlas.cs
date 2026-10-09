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
    /// The identity of a <see cref="GlyphMaskAtlas"/> entry: the coverage of <see cref="Key"/>
    /// rasterized by the typeface <see cref="Owner"/> stands for, corrected for
    /// <see cref="Bucket"/>. A mask key is complete only within one typeface, so an atlas
    /// holding the masks of several typefaces tells them apart by owner: the
    /// <see cref="GlyphTypeface.MaskOwnerId"/> of the typeface the mask belongs to, or 0 in an
    /// atlas of one typeface.
    /// </summary>
    internal readonly record struct GlyphAtlasEntryKey(int Owner, GlyphMaskKey Key, int Bucket);

    /// <summary>
    /// One A8 page of a <see cref="GlyphMaskAtlas"/>: <see cref="GlyphMaskAtlas.PageWidth"/>
    /// columns, rows grown on demand. The pixels live in a pinned array, so a backend can wrap
    /// them as an image without a copy. A page holds entries of every luminance bucket, each
    /// stored through its own bucket's correction.
    /// </summary>
    internal sealed class GlyphAtlasPage
    {
        internal readonly List<GlyphAtlasEntryKey> Keys = new();
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

        private readonly object _writtenLock = new();
        private int _writtenLeft = int.MaxValue;
        private int _writtenTop = int.MaxValue;
        private int _writtenRight;
        private int _writtenBottom;

        /// <summary>
        /// Takes the bounds of every write since the last call, so a backend that keeps a copy
        /// of the page can update only that part. Returns <c>false</c> when nothing was written.
        /// </summary>
        /// <remarks>
        /// A write is recorded before <see cref="Version"/> moves past it: a backend that reads
        /// the version first and takes the bounds second never misses a write of that version.
        /// </remarks>
        public bool TakeWritten(out PixelRect bounds)
        {
            lock (_writtenLock)
            {
                if (_writtenRight <= _writtenLeft || _writtenBottom <= _writtenTop)
                {
                    bounds = default;
                    return false;
                }

                bounds = new PixelRect(_writtenLeft, _writtenTop, _writtenRight - _writtenLeft,
                    _writtenBottom - _writtenTop);
                _writtenLeft = _writtenTop = int.MaxValue;
                _writtenRight = _writtenBottom = 0;

                return true;
            }
        }

        internal void MarkWritten(int x, int y, int width, int height)
        {
            lock (_writtenLock)
            {
                _writtenLeft = Math.Min(_writtenLeft, x);
                _writtenTop = Math.Min(_writtenTop, y);
                _writtenRight = Math.Max(_writtenRight, x + width);
                _writtenBottom = Math.Max(_writtenBottom, y + height);
            }
        }

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
    /// The storage of transformed glyph masks on GPU contexts: A8 pages, shelf-packed, drawn by
    /// the backend with one batched call per page. A context that updates part of a page in
    /// place keeps the masks of every typeface in <see cref="Shared"/>, so text in many
    /// typefaces samples few pages; other contexts use one atlas per typeface, whose pages are
    /// uploaded whole whenever they change. Every entry has an
    /// empty row and column on each side: shelves start one column in from the page's left edge
    /// and the first shelf one row down, and every entry keeps one empty column to its right and
    /// one empty row below it. A batch drawn with bilinear sampling under a scaling or rotating
    /// transform therefore reads empty page beyond each edge of an entry, never a neighbour's
    /// coverage or the clamped page edge, so its pixels do not depend on where the entry sits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Coverage is stored already corrected by the <see cref="MaskGamma"/> table of a
    /// luminance bucket, so a draw needs no correction stage and bilinear sampling interpolates
    /// corrected values. The correction is not linear: applied after sampling, it would act on
    /// blended coverage, which for dark text thins every edge a stretched draw softens. A glyph
    /// drawn in colours of several buckets has an entry per bucket; colour glyph layers, which
    /// must not be corrected, take <see cref="Uncorrected"/> entries. Entries of every bucket
    /// share pages, so text in colours of several buckets samples one page and its opaque
    /// colours can draw in one batched call.
    /// </para>
    /// <para>
    /// Entries cannot be freed one by one, so the budget is enforced a page at a time: when a
    /// new shelf would take the atlas over its budget, the page used longest ago is dropped
    /// whole. A page touched during the current draw (same tick) is never dropped, so building
    /// one run cannot evict the entries it placed a moment ago; if every page is in use the
    /// atlas grows past its budget instead.
    /// </para>
    /// <para>
    /// Once a drawing session has begun (<see cref="BeginSession"/>), pages touched during the
    /// session are kept as well, up to twice the budget: a typeface streaming new glyphs then
    /// drops pages nobody drew this session, never the pages of the text the session draws
    /// around it. Past twice the budget only the current draw's pages are kept.
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

        /// <summary>The bucket of entries holding coverage as rasterized, without correction.</summary>
        public const int Uncorrected = -1;

        /// <summary>The byte budget of <see cref="Shared"/>: 16 pages of the largest size.</summary>
        public const int SharedBudgetBytes = 32 * 1024 * 1024;

        private const int RowQuantum = 64;

        // The empty column left of every shelf and the empty row above a page's first shelf.
        private const int LeadingGutter = 1;

        private readonly object _lock = new();
        private readonly Dictionary<GlyphAtlasEntryKey, GlyphAtlasSlot> _slots = new();
        private readonly List<GlyphAtlasPage> _pages = new();
        private readonly List<GlyphAtlasPage> _emptied = new();
        private readonly int _budget;
        private readonly GlyphCachePoolHandle _handle;
        private long _allocated;
        private long _clock;
        private long _evictions;
        private long _sessionStart = long.MaxValue;

        /// <param name="budgetBytes">The byte budget of all pages together.</param>
        /// <param name="budget">
        /// The budget the atlas charges its pages to; <see cref="GlyphCacheBudget.Shared"/> when omitted.
        /// </param>
        public GlyphMaskAtlas(int budgetBytes, GlyphCacheBudget? budget = null)
        {
            _budget = Math.Max(budgetBytes, PageWidth * RowQuantum);
            _handle = (budget ?? GlyphCacheBudget.Shared).Register(GlyphCachePoolKind.Atlas, this);
        }

        /// <summary>
        /// The atlas of every typeface drawn on contexts that update part of a page in place,
        /// entries told apart by <see cref="GlyphAtlasEntryKey.Owner"/>.
        /// </summary>
        public static GlyphMaskAtlas Shared { get; } = new(SharedBudgetBytes);

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
        public static bool Fits(int width, int height)
            => LeadingGutter + width + 1 <= PageWidth && LeadingGutter + height + 1 <= MaxPageHeight;

        /// <summary>
        /// Starts a draw or build: pages stamped with the returned tick are protected from
        /// eviction until a later tick is taken.
        /// </summary>
        public long Tick() => Interlocked.Increment(ref _clock);

        /// <summary>
        /// Starts a drawing session: until the next session begins, the pages it draws from are
        /// kept while the atlas stays within twice its budget, and the pages <see cref="Retire"/>
        /// emptied are dropped. Called once per frame of a render target on the thread that draws
        /// it, not by the contexts of layers drawn within that frame.
        /// </summary>
        public void BeginSession()
        {
            var start = Tick();

            Interlocked.Exchange(ref _sessionStart, start);

            lock (_lock)
            {
                // A page emptied by retired owners is dropped here, between frames on the thread
                // that draws its backend image, unless another owner has placed entries on it since.
                foreach (var page in _emptied)
                {
                    if (!page.IsEvicted && page.Keys.Count == 0 && page.LastUse < start)
                    {
                        Evict(page);
                    }
                }

                _emptied.Clear();
            }
        }

        /// <summary>
        /// Removes the entries of <paramref name="owner"/>, a typeface that draws no more. A page
        /// left without entries is dropped when the next session begins. Batches built from the
        /// removed entries keep sampling their pages: a dropped page's pixels stay readable, and
        /// the atlas never writes over an entry.
        /// </summary>
        public void Retire(int owner)
        {
            if (owner == 0)
            {
                return;
            }

            lock (_lock)
            {
                List<GlyphAtlasEntryKey>? retired = null;

                foreach (var key in _slots.Keys)
                {
                    if (key.Owner == owner)
                    {
                        (retired ??= new List<GlyphAtlasEntryKey>()).Add(key);
                    }
                }

                if (retired is null)
                {
                    return;
                }

                foreach (var key in retired)
                {
                    _slots.Remove(key);
                }

                foreach (var page in _pages)
                {
                    if (page.Keys.RemoveAll(key => key.Owner == owner) > 0 && page.Keys.Count == 0)
                    {
                        _emptied.Add(page);
                    }
                }
            }
        }

        /// <summary>Looks up an uncorrected entry of owner 0 and stamps its page with <paramref name="tick"/>.</summary>
        public bool TryGet(in GlyphMaskKey key, long tick, out GlyphAtlasSlot slot)
            => TryGet(0, key, Uncorrected, tick, out slot);

        /// <summary>
        /// Looks up the entry of owner 0 holding the coverage of <paramref name="key"/> corrected
        /// for <paramref name="bucket"/>, and stamps its page with <paramref name="tick"/>.
        /// </summary>
        public bool TryGet(in GlyphMaskKey key, int bucket, long tick, out GlyphAtlasSlot slot)
            => TryGet(0, key, bucket, tick, out slot);

        /// <summary>
        /// Looks up the entry holding the coverage of <paramref name="key"/> as rasterized by
        /// <paramref name="owner"/>, corrected for <paramref name="bucket"/>, and stamps its page
        /// with <paramref name="tick"/>.
        /// </summary>
        public bool TryGet(int owner, in GlyphMaskKey key, int bucket, long tick, out GlyphAtlasSlot slot)
        {
            lock (_lock)
            {
                if (!_slots.TryGetValue(new GlyphAtlasEntryKey(owner, key, bucket), out slot))
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
        /// Places <paramref name="mask"/> uncorrected under <paramref name="key"/> of owner 0, or
        /// returns the entry a racing caller placed first. Returns <c>false</c> when the mask does
        /// not fit a page.
        /// </summary>
        public bool TryAdd(in GlyphMaskKey key, GlyphMask mask, long tick, out GlyphAtlasSlot slot)
            => TryAdd(0, key, Uncorrected, mask, tick, out slot);

        /// <summary>
        /// Places <paramref name="mask"/> under <paramref name="key"/> of owner 0 and
        /// <paramref name="bucket"/>, its coverage corrected by that bucket's table.
        /// </summary>
        public bool TryAdd(in GlyphMaskKey key, int bucket, GlyphMask mask, long tick, out GlyphAtlasSlot slot)
            => TryAdd(0, key, bucket, mask, tick, out slot);

        /// <summary>
        /// Places <paramref name="mask"/>, rasterized by <paramref name="owner"/>, under
        /// <paramref name="key"/> and <paramref name="bucket"/>, its coverage corrected by that
        /// bucket's table, or returns the entry a racing caller placed first. Returns
        /// <c>false</c> when the mask does not fit a page.
        /// </summary>
        public bool TryAdd(int owner, in GlyphMaskKey key, int bucket, GlyphMask mask, long tick,
            out GlyphAtlasSlot slot)
        {
            if (!mask.IsEmpty && !Fits(mask.Width, mask.Height))
            {
                slot = default;
                return false;
            }

            var entry = new GlyphAtlasEntryKey(owner, key, bucket);

            lock (_lock)
            {
                if (_slots.TryGetValue(entry, out slot))
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
                    _slots.Add(entry, slot);
                    return true;
                }

                var timer = GlyphPhaseTimers.Start();
                var (page, x, y) = Place(mask.Width + 1, mask.Height + 1, tick);

                Write(page, mask, x, y, bucket);
                GlyphPhaseTimers.Stop(GlyphTimerPhase.AtlasWrite, timer);
                page.Keys.Add(entry);
                page.LastUse = tick;

                slot = new GlyphAtlasSlot(page, x, y, mask.Width, mask.Height, mask.Left, mask.Top);
                _slots.Add(entry, slot);

                return true;
            }
        }

        private (GlyphAtlasPage Page, int X, int Y) Place(int width, int height, long tick)
        {
            // An open shelf of a similar height first, so short glyphs do not waste tall rows.
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
                if (page.UsedHeight + height <= MaxPageHeight)
                {
                    return (page, LeadingGutter, OpenShelf(page, width, height, tick));
                }
            }

            var rows = RoundUp(LeadingGutter + height);

            MakeRoom((long)PageWidth * rows, tick);

            var fresh = new GlyphAtlasPage(rows) { UsedHeight = LeadingGutter };

            _pages.Add(fresh);
            Interlocked.Add(ref _allocated, fresh.Pixels.Length);

            return (fresh, LeadingGutter, OpenShelf(fresh, width, height, tick));
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

            page.Shelves.Add((y, height, LeadingGutter + width));
            page.UsedHeight = y + height;

            return y;
        }

        private static int RoundUp(int rows) => (rows + RowQuantum - 1) / RowQuantum * RowQuantum;

        /// <summary>
        /// Drops the least recently used pages until <paramref name="bytes"/> more fit the budget,
        /// sparing the pages of the current session while the atlas stays within twice its budget.
        /// </summary>
        private void MakeRoom(long bytes, long tick)
        {
            var sessionStart = Math.Min(Interlocked.Read(ref _sessionStart), tick);

            while (_allocated + bytes > _budget)
            {
                var victim = LeastRecentlyUsed(sessionStart);

                if (victim is null)
                {
                    if (sessionStart == tick || _allocated + bytes <= 2L * _budget)
                    {
                        return;
                    }

                    victim = LeastRecentlyUsed(tick);

                    if (victim is null)
                    {
                        return;
                    }
                }

                Evict(victim);
            }
        }

        /// <summary>The page used longest ago among those last used before <paramref name="tick"/>.</summary>
        private GlyphAtlasPage? LeastRecentlyUsed(long tick)
        {
            GlyphAtlasPage? victim = null;

            foreach (var page in _pages)
            {
                if (page.LastUse < tick && (victim is null || page.LastUse < victim.LastUse))
                {
                    victim = page;
                }
            }

            return victim;
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

        private static void Write(GlyphAtlasPage page, GlyphMask mask, int x, int y, int bucket)
        {
            var pixels = page.Pixels;

            for (var row = 0; row < mask.Height; row++)
            {
                var source = mask.Alpha.AsSpan(row * mask.Width, mask.Width);
                var target = pixels.AsSpan((y + row) * PageWidth + x, mask.Width);

                if (bucket == Uncorrected)
                {
                    source.CopyTo(target);
                }
                else
                {
                    Correct(source, target, bucket);
                }
            }

            page.MarkWritten(x, y, mask.Width, mask.Height);
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
