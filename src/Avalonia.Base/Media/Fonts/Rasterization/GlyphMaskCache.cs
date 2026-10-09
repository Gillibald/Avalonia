using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// A cache of rasterized glyph masks keyed by (glyph, scale bucket, subpixel phase, mode),
    /// the sibling of <see cref="GlyphCache"/> for the managed rasterization path, bounded by the
    /// <see cref="GlyphCacheBudget"/> it is charged to. Hits are lock-free; builds run outside the
    /// lock (racing builders may duplicate work, the losing result is discarded); inserts and
    /// eviction run under one lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Population is demand-driven with exact-fit allocations — there is no page to reserve and
    /// nothing here scales with a font's glyph count; memory follows the drawn working set only.
    /// Evicted entries are dropped whole (masks retain nothing), and because composed run masks
    /// are independent copies, eviction can never invalidate anything already drawable.
    /// </para>
    /// <para>
    /// Recency is the <see cref="GlyphCacheBudget.Frame"/> a mask was last used in, written by a
    /// hit with a plain store: a lost update only makes a mask look one frame older. The ring
    /// keeps masks in the order they were queued; eviction takes the head, unless it was used
    /// since it was queued, in which case it moves behind the tail first. That approximates
    /// least-recently-used by frame without a heap or a relink per hit. A mask used by a frame
    /// still being drawn (<see cref="GlyphCacheBudget.PinFloor"/>) is never evicted.
    /// </para>
    /// </remarks>
    internal sealed class GlyphMaskCache : IGlyphCachePool
    {
        /// <summary>
        /// The largest mask worth caching. A bigger mask (a glyph near a thousand pixels per em)
        /// would evict a whole screen of text masks to be kept, so callers compose it from a
        /// transient buffer instead. A constant rather than a share of the limit, so the limit
        /// never changes which masks are cached and which are composed, and pixels stay the same.
        /// </summary>
        public const int MaxEntryBytes = 512 * 1024;

        private readonly ConcurrentDictionary<GlyphMaskKey, Entry> _entries = new();
        private readonly object _lock = new();
        private readonly GlyphCacheBudget _clock;
        private readonly GlyphCachePoolHandle _handle;
        private Entry? _hand;
        private int _count;
        private long _totalCost;
        private long _evictions;

        /// <param name="budget">
        /// The budget the cache charges its masks to; <see cref="GlyphCacheBudget.Shared"/> when omitted.
        /// </param>
        public GlyphMaskCache(GlyphCacheBudget? budget = null)
        {
            _clock = budget ?? GlyphCacheBudget.Shared;
            _handle = _clock.Register(GlyphCachePoolKind.Masks, this, perTypeface: true);
        }

        /// <summary>Number of cached masks.</summary>
        public int Count => _entries.Count;

        /// <summary>Total retained mask bytes.</summary>
        public long TotalCost => Interlocked.Read(ref _totalCost);

        /// <summary>Masks evicted for the glyph cache budget since construction; for diagnostics and tests.</summary>
        public long Evictions => Volatile.Read(ref _evictions);

        // Recent mask use of upright run builds, each total decayed by a sixteenth per build so
        // that about the last frame's runs count.
        private double _recentUsedPixels;
        private double _recentMissedPixels;

        /// <summary>
        /// The share of mask pixels that recent upright run builds had to rasterize rather than
        /// find here: near 0 while text draws at scales it has drawn before, near the share of
        /// distinct masks in a run while every frame meets a new scale.
        /// </summary>
        public double RecentMissShare => _recentUsedPixels > 0 ? _recentMissedPixels / _recentUsedPixels : 1;

        /// <summary>
        /// Records that a run build used <paramref name="usedPixels"/> mask pixels, of which it
        /// rasterized <paramref name="missedPixels"/>. A heuristic: concurrent builds may lose
        /// an update.
        /// </summary>
        public void RecordUse(long usedPixels, long missedPixels)
        {
            const double keep = 15.0 / 16;

            _recentUsedPixels = _recentUsedPixels * keep + usedPixels;
            _recentMissedPixels = _recentMissedPixels * keep + missedPixels;
        }

        /// <summary>Peeks a cached mask without building. Lock-free; for diagnostics and tests.</summary>
        public bool TryGet(in GlyphMaskKey key, out GlyphMask mask)
        {
            if (_entries.TryGetValue(key, out var entry) && Volatile.Read(ref entry.Mask) is { } hit)
            {
                mask = hit;
                return true;
            }

            mask = GlyphMask.Empty;
            return false;
        }

        /// <summary>
        /// Returns the cached mask for <paramref name="key"/>, building it with
        /// <paramref name="build"/> on a miss. The build runs outside the lock; when two threads
        /// race, one result wins at insertion and both callers receive the winner. A no-ink glyph
        /// is memoised as <see cref="GlyphMask.Empty"/> so it is never rebuilt.
        /// </summary>
        public GlyphMask GetOrBuild(in GlyphMaskKey key, Func<GlyphMaskKey, GlyphMask> build)
            => GetOrBuild(key, build, static (k, b) => b(k));

        /// <summary>
        /// State-passing variant of <see cref="GetOrBuild(in GlyphMaskKey, Func{GlyphMaskKey, GlyphMask})"/>
        /// so hot callers can use a static build delegate — the compose loop must not allocate a
        /// closure per glyph.
        /// </summary>
        public GlyphMask GetOrBuild<TState>(in GlyphMaskKey key, TState state,
            Func<GlyphMaskKey, TState, GlyphMask> build)
        {
            if (_entries.TryGetValue(key, out var entry) && Volatile.Read(ref entry.Mask) is { } hit)
            {
                entry.LastUse = _clock.Frame;
                GlyphRasterDiagnostics.CountMaskCacheHit();
                return hit;
            }

            GlyphRasterDiagnostics.CountMaskCacheMiss();

            var timer = GlyphPhaseTimers.Start();
            var built = build(key, state) ?? GlyphMask.Empty;

            GlyphPhaseTimers.Stop(GlyphTimerPhase.Rasterize, timer);

            lock (_lock)
            {
                entry = _entries.GetOrAdd(key, static k => new Entry(k));

                if (Volatile.Read(ref entry.Mask) is { } winner)
                {
                    // Lost the build race — discard our result and hand out the published one.
                    entry.LastUse = _clock.Frame;
                    return winner;
                }

                Volatile.Write(ref entry.Mask, built);
                RingAdd(entry);
                Interlocked.Add(ref _totalCost, built.ByteCost);
                _handle.Charge(built.ByteCost);

                return built;
            }
        }

        /// <summary>
        /// Drops every mask. Masks are unlinked, never disposed, so composed run masks and
        /// readers that fetched one a moment ago stay valid.
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                while (_hand is { } entry)
                {
                    RingRemove(entry);

                    var cost = Volatile.Read(ref entry.Mask)!.ByteCost;

                    Interlocked.Add(ref _totalCost, -cost);
                    _handle.Credit(cost);
                    Volatile.Write(ref entry.Mask, null);
                    _entries.TryRemove(entry.Key, out _);
                }
            }
        }

        long IGlyphCachePool.OldestUse
        {
            get
            {
                lock (_lock)
                {
                    return NormalizeHead()?.LastUse ?? long.MaxValue;
                }
            }
        }

        long IGlyphCachePool.EvictOldest(long usedBefore, long bytes)
        {
            long freed = 0;

            lock (_lock)
            {
                while (freed < bytes && SelectVictim(usedBefore) is { } victim)
                {
                    var cost = Volatile.Read(ref victim.Mask)!.ByteCost;

                    Interlocked.Add(ref _totalCost, -cost);
                    freed += cost;
                    _handle.Credit(cost);
                    Interlocked.Increment(ref _evictions);
                    RingRemove(victim);
                    Volatile.Write(ref victim.Mask, null);
                    _entries.TryRemove(victim.Key, out _);

                    // Unlink only, never dispose: composed run masks copied from this payload and
                    // any lock-free reader that fetched it a moment ago stay valid; the GC
                    // reclaims it.
                }
            }

            return freed;
        }

        private void RingAdd(Entry entry)
        {
            var frame = _clock.Frame;

            entry.LastUse = frame;
            entry.QueuedAt = frame;

            if (_hand is null)
            {
                entry.Prev = entry;
                entry.Next = entry;
                _hand = entry;
            }
            else
            {
                var tail = _hand.Prev!;
                tail.Next = entry;
                entry.Prev = tail;
                entry.Next = _hand;
                _hand.Prev = entry;
            }

            _count++;
        }

        private void RingRemove(Entry entry)
        {
            if (entry.Next == entry)
            {
                _hand = null;
            }
            else
            {
                entry.Prev!.Next = entry.Next;
                entry.Next!.Prev = entry.Prev;

                if (_hand == entry)
                {
                    _hand = entry.Next;
                }
            }

            entry.Prev = null;
            entry.Next = null;
            _count--;
        }

        /// <summary>
        /// Moves masks used since they were queued from the head of the ring behind its tail,
        /// so the head is the mask used longest ago, as far as the queue order tells.
        /// </summary>
        private Entry? NormalizeHead()
        {
            for (var i = 0; i < _count && _hand is { } head; i++)
            {
                var lastUse = head.LastUse;

                if (lastUse == head.QueuedAt)
                {
                    return head;
                }

                head.QueuedAt = lastUse;
                _hand = head.Next;
            }

            return _hand;
        }

        /// <summary>The mask used longest ago, unless it was used at or after <paramref name="floor"/>.</summary>
        private Entry? SelectVictim(long floor)
        {
            var head = NormalizeHead();

            return head is not null && head.LastUse < floor ? head : null;
        }

        private sealed class Entry
        {
            public Entry(GlyphMaskKey key) => Key = key;

            public readonly GlyphMaskKey Key;
            public GlyphMask? Mask;

            /// <summary>The frame of the mask's last use.</summary>
            public long LastUse;

            /// <summary>The <see cref="LastUse"/> the mask had when it was queued at the ring's tail.</summary>
            public long QueuedAt;

            public Entry? Prev;
            public Entry? Next;
        }
    }
}
