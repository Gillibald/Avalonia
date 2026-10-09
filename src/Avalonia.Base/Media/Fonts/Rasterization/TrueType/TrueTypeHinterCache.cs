using System.Collections.Generic;

namespace Avalonia.Media.Fonts.Rasterization.TrueType
{
    /// <summary>
    /// The bytecode hinters of one typeface, one per (quantized size, mask mode): the render
    /// class feeds GETINFO, which prep may branch on, so the size state is per mode. Entries
    /// memoise failures as <c>null</c>, so a broken prep costs one attempt.
    /// </summary>
    /// <remarks>
    /// Each kept hinter is charged to the glyph cache budget and records the frame of its last
    /// use, so a zoom whose glyph masks fit the budget keeps its size states too, and the budget
    /// evicts the sizes drawn longest ago. A thread that still holds an evicted hinter keeps
    /// using it; the next request for its size creates another.
    /// </remarks>
    internal sealed class TrueTypeHinterCache : IGlyphCachePool
    {
        private readonly Dictionary<(ushort ScaleQ, GlyphMaskMode Mode), Entry> _hinters = new();
        private readonly GlyphCacheBudget _clock;
        private readonly GlyphCachePoolHandle _handle;
        private readonly long _hinterBytes;
        private readonly List<(ushort, GlyphMaskMode)> _stale = new();

        /// <param name="budget">The budget the hinters are charged to.</param>
        /// <param name="hinterBytes">The memory one hinter holds.</param>
        public TrueTypeHinterCache(GlyphCacheBudget budget, long hinterBytes)
        {
            _hinterBytes = hinterBytes;
            _clock = budget;
            _handle = budget.Register(GlyphCachePoolKind.Hinters, this, perTypeface: true);
        }

        /// <summary>The number of hinters kept, including memoised failures.</summary>
        public int Count
        {
            get
            {
                lock (_hinters)
                {
                    return _hinters.Count;
                }
            }
        }

        /// <summary>
        /// The hinter for <paramref name="scaleQ"/> and <paramref name="mode"/>, created by
        /// <paramref name="typeface"/> on a miss.
        /// </summary>
        public TrueTypeGlyphHinter? Get(ushort scaleQ, GlyphMaskMode mode, GlyphTypeface typeface)
        {
            lock (_hinters)
            {
                if (_hinters.TryGetValue((scaleQ, mode), out var entry))
                {
                    entry.LastUse = _clock.Frame;
                    return entry.Hinter;
                }

                var hinter = typeface.CreateTrueTypeHinter(scaleQ, mode);

                _hinters.Add((scaleQ, mode), new Entry(hinter) { LastUse = _clock.Frame });

                if (hinter is not null)
                {
                    _handle.Charge(_hinterBytes);
                }

                return hinter;
            }
        }

        long IGlyphCachePool.OldestUse
        {
            get
            {
                var oldest = long.MaxValue;

                lock (_hinters)
                {
                    foreach (var entry in _hinters.Values)
                    {
                        if (entry.Hinter is not null && entry.LastUse < oldest)
                        {
                            oldest = entry.LastUse;
                        }
                    }
                }

                return oldest;
            }
        }

        long IGlyphCachePool.EvictOldest(long usedBefore, long bytes)
        {
            long freed = 0;

            lock (_hinters)
            {
                while (freed < bytes)
                {
                    (ushort, GlyphMaskMode) victim = default;
                    var victimUse = long.MaxValue;

                    foreach (var pair in _hinters)
                    {
                        if (pair.Value.Hinter is not null && pair.Value.LastUse < usedBefore &&
                            pair.Value.LastUse < victimUse)
                        {
                            victim = pair.Key;
                            victimUse = pair.Value.LastUse;
                        }
                    }

                    if (victimUse == long.MaxValue)
                    {
                        break;
                    }

                    _hinters.Remove(victim);
                    _handle.Credit(_hinterBytes);
                    freed += _hinterBytes;
                }

                // Memoised failures hold no memory but would make the lookup grow with every size
                // a zoom passes; they go with the hinters of their age.
                foreach (var pair in _hinters)
                {
                    if (pair.Value.Hinter is null && pair.Value.LastUse < usedBefore)
                    {
                        _stale.Add(pair.Key);
                    }
                }

                foreach (var key in _stale)
                {
                    _hinters.Remove(key);
                }

                _stale.Clear();
            }

            return freed;
        }

        private sealed class Entry
        {
            public Entry(TrueTypeGlyphHinter? hinter) => Hinter = hinter;

            public TrueTypeGlyphHinter? Hinter { get; }

            /// <summary>The frame of the hinter's last use.</summary>
            public long LastUse;
        }
    }
}
