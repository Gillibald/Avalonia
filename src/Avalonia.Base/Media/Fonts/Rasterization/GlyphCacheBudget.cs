using System;
using System.Collections.Generic;
using System.Threading;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>The kinds of glyph cache a <see cref="GlyphCacheBudget"/> accounts for.</summary>
    internal enum GlyphCachePoolKind
    {
        /// <summary>Composed run masks of every run (<see cref="RunMaskCache"/>).</summary>
        RunMasks,

        /// <summary>Sprite sets of every run (<see cref="TransformedRunState"/>).</summary>
        SpriteSets,

        /// <summary>A8 atlas pages (<see cref="GlyphMaskAtlas"/>).</summary>
        Atlas,

        /// <summary>RGBA pages of subpixel run masks (<see cref="LcdRunAtlas"/>).</summary>
        LcdAtlas,

        /// <summary>Rasterized glyph masks of one typeface (<see cref="GlyphMaskCache"/>).</summary>
        Masks,

        /// <summary>TrueType size states of one typeface.</summary>
        Hinters,

        /// <summary>Outline and colour glyph geometry of one typeface (<see cref="GlyphCache"/>).</summary>
        Outlines,
    }

    /// <summary>
    /// The accountant of every glyph cache in the process: each cache registers as a pool and
    /// charges the bytes it adds and credits the bytes it drops, so <see cref="UsedBytes"/> is the
    /// memory all glyph caches hold together.
    /// </summary>
    /// <remarks>
    /// Pools keep their own data structures and locks; the budget only counts. A pool that the
    /// GC collects without releasing its handle (a typeface nobody disposed) is credited by
    /// <see cref="SweepCollectedPools"/>.
    /// </remarks>
    internal sealed class GlyphCacheBudget
    {
        /// <summary>The limit of <see cref="Shared"/> on desktop platforms.</summary>
        public const long DefaultLimitBytes = 64L * 1024 * 1024;

        private readonly object _poolsLock = new();
        private readonly List<GlyphCachePoolHandle> _pools = new();
        private long _used;
        private long _peak;

        public GlyphCacheBudget(long limitBytes)
        {
            LimitBytes = Math.Max(1, limitBytes);
            RunMasks = Register(GlyphCachePoolKind.RunMasks, this);
            SpriteSets = Register(GlyphCachePoolKind.SpriteSets, this);
        }

        /// <summary>The budget of every glyph cache the process creates.</summary>
        public static GlyphCacheBudget Shared { get; } = new(DefaultLimitBytes);

        /// <summary>The composed run masks of every run charged to this budget.</summary>
        public GlyphCachePoolHandle RunMasks { get; }

        /// <summary>The sprite sets of every run charged to this budget.</summary>
        public GlyphCachePoolHandle SpriteSets { get; }

        /// <summary>The global maximum of all pools together.</summary>
        public long LimitBytes { get; private set; }

        /// <summary>Bytes charged by all pools together.</summary>
        public long UsedBytes => Interlocked.Read(ref _used);

        /// <summary>The highest <see cref="UsedBytes"/> since construction or <see cref="ResetPeak"/>.</summary>
        public long PeakBytes => Interlocked.Read(ref _peak);

        /// <summary>Starts a new peak measurement at the current use; for diagnostics.</summary>
        public void ResetPeak() => Interlocked.Exchange(ref _peak, UsedBytes);

        /// <summary>
        /// Registers <paramref name="owner"/> as a pool of <paramref name="kind"/>. The budget
        /// holds the owner weakly; the owner keeps the returned handle and charges through it.
        /// </summary>
        public GlyphCachePoolHandle Register(GlyphCachePoolKind kind, object owner)
        {
            var handle = new GlyphCachePoolHandle(this, kind, owner);

            lock (_poolsLock)
            {
                _pools.Add(handle);
            }

            return handle;
        }

        /// <summary>The bytes charged by the live pools of <paramref name="kind"/>; for diagnostics and tests.</summary>
        public long BytesOf(GlyphCachePoolKind kind)
        {
            long bytes = 0;

            lock (_poolsLock)
            {
                foreach (var pool in _pools)
                {
                    if (pool.Kind == kind)
                    {
                        bytes += pool.Bytes;
                    }
                }
            }

            return bytes;
        }

        /// <summary>Credits and forgets the pools the GC collected without releasing their handle.</summary>
        public void SweepCollectedPools()
        {
            lock (_poolsLock)
            {
                for (var i = _pools.Count - 1; i >= 0; i--)
                {
                    var pool = _pools[i];

                    if (!pool.IsOwnerAlive)
                    {
                        pool.CreditAll();
                        _pools.RemoveAt(i);
                    }
                }
            }
        }

        internal void Unregister(GlyphCachePoolHandle handle)
        {
            lock (_poolsLock)
            {
                _pools.Remove(handle);
            }
        }

        internal void Add(long bytes)
        {
            var used = Interlocked.Add(ref _used, bytes);

            if (bytes > 0)
            {
                long peak;

                while (used > (peak = Interlocked.Read(ref _peak)) &&
                       Interlocked.CompareExchange(ref _peak, used, peak) != peak)
                {
                }
            }
        }
    }

    /// <summary>
    /// A pool's registration with a <see cref="GlyphCacheBudget"/>: the bytes the pool holds,
    /// charged and credited by the pool as it adds and drops payload.
    /// </summary>
    internal sealed class GlyphCachePoolHandle
    {
        private readonly WeakReference<object> _owner;
        private long _bytes;
        private bool _released;

        internal GlyphCachePoolHandle(GlyphCacheBudget budget, GlyphCachePoolKind kind, object owner)
        {
            Budget = budget;
            Kind = kind;
            _owner = new WeakReference<object>(owner);
        }

        public GlyphCacheBudget Budget { get; }

        public GlyphCachePoolKind Kind { get; }

        /// <summary>The bytes the pool holds.</summary>
        public long Bytes => Interlocked.Read(ref _bytes);

        internal bool IsOwnerAlive => _owner.TryGetTarget(out _);

        /// <summary>Records that the pool now holds <paramref name="bytes"/> more.</summary>
        public void Charge(long bytes)
        {
            if (bytes == 0)
            {
                return;
            }

            Interlocked.Add(ref _bytes, bytes);
            Budget.Add(bytes);
        }

        /// <summary>Records that the pool dropped <paramref name="bytes"/>.</summary>
        public void Credit(long bytes) => Charge(-bytes);

        /// <summary>Credits everything the pool holds and removes it from the budget.</summary>
        public void Release()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            CreditAll();
            Budget.Unregister(this);
        }

        internal void CreditAll()
        {
            var bytes = Interlocked.Exchange(ref _bytes, 0);

            Budget.Add(-bytes);
        }
    }
}
