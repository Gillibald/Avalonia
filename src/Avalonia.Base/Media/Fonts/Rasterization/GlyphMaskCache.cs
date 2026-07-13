using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// A bounded cache of rasterized glyph masks keyed by
    /// (glyph, scale bucket, subpixel phase, mode) — the sibling of <see cref="GlyphCache"/> for
    /// the managed rasterization path. Hits are lock-free; builds run outside the lock (racing
    /// builders may duplicate work, the losing result is discarded); inserts and CLOCK eviction
    /// run under one lock, keeping total payload bytes under the budget.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Population is demand-driven with exact-fit allocations — there is no page to reserve and
    /// nothing here scales with a font's glyph count; memory follows the drawn working set only.
    /// Evicted entries are dropped whole (masks retain nothing), and because composed run masks
    /// are independent copies, eviction can never invalidate anything already drawable.
    /// </para>
    /// <para>
    /// The eviction ring is a private copy of <see cref="ClockEvictionPolicy"/>'s scheme rather
    /// than a reuse of it: that policy is intrusively typed to <see cref="GlyphCacheEntry"/>
    /// (whose pin/bounds machinery masks do not need), and generalizing it would churn a file
    /// that is still in review upstream. Fold the two rings together once the outline stack has
    /// landed.
    /// </para>
    /// </remarks>
    internal sealed class GlyphMaskCache
    {
        /// <summary>Default mask byte budget. Calibrated in Phase 3 against real scenes.</summary>
        public const int DefaultBudgetBytes = 8 * 1024 * 1024;

        private readonly ConcurrentDictionary<GlyphMaskKey, Entry> _entries = new();
        private readonly object _lock = new();
        private readonly int _budget;
        private Entry? _hand;
        private int _count;
        private int _totalCost;

        public GlyphMaskCache(int budgetBytes = DefaultBudgetBytes)
        {
            _budget = budgetBytes < 1 ? 1 : budgetBytes;
        }

        /// <summary>Number of cached masks.</summary>
        public int Count => _entries.Count;

        /// <summary>Total retained mask bytes.</summary>
        public int TotalCost => Volatile.Read(ref _totalCost);

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
                Volatile.Write(ref entry.Referenced, 1);
                return hit;
            }

            var built = build(key, state) ?? GlyphMask.Empty;

            lock (_lock)
            {
                entry = _entries.GetOrAdd(key, static k => new Entry(k));

                if (Volatile.Read(ref entry.Mask) is { } winner)
                {
                    // Lost the build race — discard our result and hand out the published one.
                    return winner;
                }

                Volatile.Write(ref entry.Mask, built);
                RingAdd(entry);
                _totalCost += built.ByteCost;
                EvictToBudget();

                return built;
            }
        }

        private void EvictToBudget()
        {
            while (_totalCost > _budget)
            {
                var victim = SelectVictim();

                if (victim is null)
                {
                    break;
                }

                _totalCost -= Volatile.Read(ref victim.Mask)!.ByteCost;
                RingRemove(victim);
                Volatile.Write(ref victim.Mask, null);
                _entries.TryRemove(victim.Key, out _);

                // Unlink only, never dispose: composed run masks copied from this payload and any
                // lock-free reader that fetched it a moment ago stay valid; the GC reclaims it.
            }
        }

        private void RingAdd(Entry entry)
        {
            // New entries arrive referenced so they survive at least one sweep.
            Volatile.Write(ref entry.Referenced, 1);

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

        private Entry? SelectVictim()
        {
            if (_hand is null)
            {
                return null;
            }

            // Two trips clear every referenced bit once, so a victim is always found (nothing is
            // ever pinned here).
            var limit = _count * 2;
            var hand = _hand;

            for (var i = 0; i < limit; i++)
            {
                var next = hand!.Next!;

                if (Volatile.Read(ref hand.Referenced) != 0)
                {
                    Volatile.Write(ref hand.Referenced, 0);
                }
                else
                {
                    _hand = next;
                    return hand;
                }

                hand = next;
            }

            _hand = hand;
            return _hand;
        }

        private sealed class Entry
        {
            public Entry(GlyphMaskKey key) => Key = key;

            public readonly GlyphMaskKey Key;
            public GlyphMask? Mask;
            public int Referenced;
            public Entry? Prev;
            public Entry? Next;
        }
    }
}
