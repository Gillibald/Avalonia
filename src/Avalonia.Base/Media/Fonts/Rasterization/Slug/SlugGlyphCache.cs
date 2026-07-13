using System;
using System.Collections.Concurrent;
using System.Threading;
using Avalonia.Media;

namespace Avalonia.Media.Fonts.Rasterization.Slug
{
    /// <summary>
    /// A bounded per-typeface-instance cache of Slug glyph payloads keyed by glyph id alone —
    /// payloads are size-independent, so one build serves every zoom level ever drawn, which is
    /// the economics that justify the vector tier. Hits are lock-free; builds run outside the
    /// lock (racing builders may duplicate work, the losing result is discarded); inserts and
    /// CLOCK eviction run under one lock, keeping retained payload bytes under the budget.
    /// </summary>
    /// <remarks>
    /// A build that returns null — an outline-less glyph, or one the caller's cap checks
    /// declined — is memoised as a decline, so the tables are never re-walked for a glyph that
    /// can never join the tier. Eviction drops entries whole; payloads are immutable and any
    /// texel data serialized from them is an independent copy, so eviction can never invalidate
    /// something already uploaded. The eviction ring repeats the <see cref="GlyphMaskCache"/>
    /// scheme for the same reason that cache states: the shared policy type is still in review
    /// upstream, and the rings should be folded together once it lands.
    /// </remarks>
    internal sealed class SlugGlyphCache
    {
        /// <summary>
        /// Default retained-byte budget. Measured working sets run ~200 texels-worth of payload
        /// per Latin glyph and ~460 for CJK, so 2 MB comfortably holds a document's realized
        /// repertoire; revisit against real document apps before production wiring.
        /// </summary>
        public const int DefaultBudgetBytes = 2 * 1024 * 1024;

        private static readonly SlugGlyphData s_declined = new(
            Array.Empty<float>(), Array.Empty<int>(), Array.Empty<int>(), FillRule.NonZero,
            0, 0, 0, 0, new int[1], Array.Empty<int>(), new int[1], Array.Empty<int>());

        private readonly ConcurrentDictionary<ushort, Entry> _entries = new();
        private readonly object _lock = new();
        private readonly int _budget;
        private Entry? _hand;
        private int _count;
        private int _totalCost;

        public SlugGlyphCache(int budgetBytes = DefaultBudgetBytes)
        {
            _budget = budgetBytes < 1 ? 1 : budgetBytes;
        }

        /// <summary>Number of cached entries, declines included.</summary>
        public int Count => _entries.Count;

        /// <summary>Total retained payload bytes.</summary>
        public int TotalCost => Volatile.Read(ref _totalCost);

        /// <summary>
        /// Peeks a cached entry without building. Returns true when the glyph has a cached
        /// outcome; <paramref name="data"/> is null when that outcome is a decline. Lock-free;
        /// for diagnostics and tests.
        /// </summary>
        public bool TryGet(ushort glyph, out SlugGlyphData? data)
        {
            if (_entries.TryGetValue(glyph, out var entry) && Volatile.Read(ref entry.Data) is { } hit)
            {
                data = ReferenceEquals(hit, s_declined) ? null : hit;
                return true;
            }

            data = null;
            return false;
        }

        /// <summary>
        /// Returns the payload for <paramref name="glyph"/>, building it on a miss, or null when
        /// the glyph is declined (now or memoised earlier). The build runs outside the lock;
        /// when two threads race, one result wins at insertion and both callers receive the
        /// winner. The state-passing delegate keeps callers closure-free.
        /// </summary>
        public SlugGlyphData? GetOrBuild<TState>(ushort glyph, TState state,
            Func<ushort, TState, SlugGlyphData?> build)
        {
            if (_entries.TryGetValue(glyph, out var entry) && Volatile.Read(ref entry.Data) is { } hit)
            {
                Volatile.Write(ref entry.Referenced, 1);
                return ReferenceEquals(hit, s_declined) ? null : hit;
            }

            var built = build(glyph, state) ?? s_declined;

            lock (_lock)
            {
                entry = _entries.GetOrAdd(glyph, static g => new Entry(g));

                if (Volatile.Read(ref entry.Data) is { } winner)
                {
                    // Lost the build race — discard our result and hand out the published one.
                    return ReferenceEquals(winner, s_declined) ? null : winner;
                }

                Volatile.Write(ref entry.Data, built);
                RingAdd(entry);
                _totalCost += built.RetainedBytes;
                EvictToBudget();

                return ReferenceEquals(built, s_declined) ? null : built;
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

                _totalCost -= Volatile.Read(ref victim.Data)!.RetainedBytes;
                RingRemove(victim);
                Volatile.Write(ref victim.Data, null);
                _entries.TryRemove(victim.Glyph, out _);

                // Unlink only, never dispose: payloads are immutable and lock-free readers that
                // fetched one a moment ago stay valid; the GC reclaims it.
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
            public Entry(ushort glyph) => Glyph = glyph;

            public readonly ushort Glyph;
            public SlugGlyphData? Data;
            public int Referenced;
            public Entry? Prev;
            public Entry? Next;
        }
    }
}
