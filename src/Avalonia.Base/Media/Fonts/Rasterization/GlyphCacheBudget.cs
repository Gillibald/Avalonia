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

    /// <summary>A glyph cache whose entries the <see cref="GlyphCacheBudget"/> it is registered with may evict.</summary>
    /// <remarks>
    /// Each pool keeps its own data structure and lock and records the
    /// <see cref="GlyphCacheBudget.Frame"/> each entry was last used in; the budget only picks
    /// which pool gives up its oldest entries next. A pool credits what it evicts through its
    /// handle, as it does for every other drop.
    /// </remarks>
    internal interface IGlyphCachePool
    {
        /// <summary>
        /// The frame the pool's least recently used evictable entry was last used in, or
        /// <see cref="long.MaxValue"/> when it holds none.
        /// </summary>
        long OldestUse { get; }

        /// <summary>
        /// Evicts entries last used before <paramref name="usedBefore"/>, oldest first, until
        /// <paramref name="bytes"/> are freed or no such entry is left. Returns the bytes freed.
        /// </summary>
        long EvictOldest(long usedBefore, long bytes);
    }

    /// <summary>
    /// The accountant of every glyph cache in the process: each cache registers as a pool and
    /// charges the bytes it adds and credits the bytes it drops, so <see cref="UsedBytes"/> is the
    /// memory all glyph caches hold together, and one limit (<see cref="LimitBytes"/>) bounds
    /// them all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pools keep their own data structures and locks; the budget only counts and picks victims.
    /// A pool that the GC collects without releasing its handle (a typeface nobody disposed) is
    /// credited by <see cref="SweepCollectedPools"/>.
    /// </para>
    /// <para>
    /// Recency is counted in frames. A drawing context that is not drawn inside another on its
    /// thread begins a frame (<see cref="BeginFrame"/>): a window's render pass, a bitmap rendered
    /// on its own. Every entry records the <see cref="Frame"/> it was last used in. What the open
    /// frames use is pinned (<see cref="PinFloor"/>), so a frame never evicts what it draws; what
    /// each source drew in its previous frame (<see cref="SoftFloor"/>) is kept while the caches
    /// stay within a quarter over the limit, since a static scene draws it again. When a frame's
    /// own content does not fit, the caches grow past the limit instead of evicting what the next
    /// frame draws again, and the frame after brings them back.
    /// </para>
    /// <para>
    /// Nothing is evicted while there is room. Each frame begins with a trim to the limit: among
    /// the entries older than the floor, the pool whose oldest entry has waited longest past its
    /// kind's minimum age gives up entries first. The minimum ages order the kinds by what an
    /// eviction costs to rebuild (run-level state composes again from cached masks, atlas pages
    /// upload again from cached masks, masks and hinters rasterize again, outlines parse again),
    /// while age still outweighs kind: content a thousand frames old goes before content two
    /// frames old whatever it is. A build that takes the caches past half over the limit evicts
    /// from its own pool at once, without touching other pools' locks.
    /// </para>
    /// <para>
    /// A typeface drawn within the idle period keeps at least an eighth of the limit of what it
    /// drew (or all of it, when less) until older content of every other pool has gone, so a
    /// face drawn now and then (a tooltip, a dialog opened every few seconds) is not emptied by
    /// another face streaming new glyphs through the whole limit.
    /// </para>
    /// <para>
    /// Atlas pages, run masks and sprite sets are frame-affine: their storage may be drawn by the
    /// thread that draws a frame, so they are trimmed only where a frame begins, and run-level
    /// state only on the thread that draws it.
    /// </para>
    /// </remarks>
    internal sealed class GlyphCacheBudget
    {
        /// <summary>The limit of <see cref="Shared"/> on desktop platforms.</summary>
        public const long DefaultLimitBytes = 64L * 1024 * 1024;

        /// <summary>
        /// Content not drawn for this many frames counts as idle: about two seconds at 60 Hz, so
        /// text drawn every few frames (a caret blink, a tooltip, a second window) never ages
        /// out, while a finished zoom gives its memory back soon after it ends. A frame left
        /// open this long (a drawing context nobody disposed) no longer pins anything.
        /// </summary>
        public const int IdleFrames = 120;

        // Windows and other frame sources whose previous frame is kept; the first slot stands for
        // every frame begun without a source.
        private const int MaxSources = 8;

        private const int SweepInterval = 64;

        // Frames an entry of each priority must have waited before it is weighed against older
        // entries of costlier kinds: the cheaper a kind is to rebuild, the earlier it goes.
        private static readonly int[] s_minimumAge = { 0, 2, 8, 30, 60 };

        private readonly object _poolsLock = new();
        private readonly List<GlyphCachePoolHandle> _pools = new();
        private readonly object _frameLock = new();
        private readonly List<OpenFrame> _open = new(4);
        private readonly FrameSource[] _sources = new FrameSource[MaxSources];
        private List<IGlyphCacheFrameListener> _frameListeners = new();
        private List<IGlyphCacheFrameListener> _runningListeners = new();
        private GlyphCachePoolHandle[] _trimPools = Array.Empty<GlyphCachePoolHandle>();
        private long _used;
        private long _peak;
        private long _frame;
        private long _pinFloor;
        private long _softFloor;
        private long _evictedBytes;
        private int _nextFrameId;
        private int _trimming;
        private int _trimGeneration;

        public GlyphCacheBudget(long limitBytes)
        {
            LimitBytes = Math.Max(1, limitBytes);
            RunMasks = new RunMaskPool(this);
            SpriteSets = new SpriteSetPool(this);
        }

        /// <summary>The budget of every glyph cache the process creates.</summary>
        public static GlyphCacheBudget Shared { get; } = new(DefaultLimitBytes);

        /// <summary>The composed run masks of every run charged to this budget.</summary>
        public RunMaskPool RunMasks { get; }

        /// <summary>The sprite sets of every run charged to this budget.</summary>
        public SpriteSetPool SpriteSets { get; }

        /// <summary>The global maximum of all pools together.</summary>
        public long LimitBytes { get; private set; }

        /// <summary>How far the previous frames of each source may keep the caches over the limit.</summary>
        public long SoftLimitBytes => LimitBytes + LimitBytes / 4;

        /// <summary>Past this, a build evicts from its own pool at once.</summary>
        public long InlineLimitBytes => LimitBytes + LimitBytes / 2;

        /// <summary>Sets <see cref="LimitBytes"/>; the next frame trims to it.</summary>
        public void SetLimit(long limitBytes) => LimitBytes = Math.Max(1, limitBytes);

        /// <summary>Bytes charged by all pools together.</summary>
        public long UsedBytes => Interlocked.Read(ref _used);

        /// <summary>The highest <see cref="UsedBytes"/> since construction or <see cref="ResetPeak"/>.</summary>
        public long PeakBytes => Interlocked.Read(ref _peak);

        /// <summary>Bytes evicted by trims and builds past the limit since construction; for diagnostics.</summary>
        public long EvictedBytes => Interlocked.Read(ref _evictedBytes);

        /// <summary>Starts a new peak measurement at the current use; for diagnostics.</summary>
        public void ResetPeak() => Interlocked.Exchange(ref _peak, UsedBytes);

        /// <summary>The number of the latest frame begun; entries record it as their last use.</summary>
        public long Frame => Volatile.Read(ref _frame);

        /// <summary>
        /// Entries last used at or after this frame are pinned: the oldest frame still being
        /// drawn, or the latest frame when none is open.
        /// </summary>
        public long PinFloor => Volatile.Read(ref _pinFloor);

        /// <summary>
        /// Entries last used at or after this frame were drawn by the previous frame of a source
        /// or since: what a static scene draws again.
        /// </summary>
        public long SoftFloor => Volatile.Read(ref _softFloor);

        /// <summary>
        /// The frame the current thread is drawing, 0 when it has none open; for diagnostics and
        /// tests.
        /// </summary>
        public long CurrentThreadFrame
        {
            get
            {
                var thread = Environment.CurrentManagedThreadId;

                lock (_frameLock)
                {
                    foreach (var open in _open)
                    {
                        if (open.Thread == thread && !IsStale(open.Start))
                        {
                            return open.Start;
                        }
                    }
                }

                return 0;
            }
        }

        /// <summary>
        /// Begins a frame of <paramref name="source"/> (a window's composition target, or
        /// <c>null</c> for drawing that belongs to none), unless the current thread is drawing
        /// one already: then the returned scope belongs to that frame and does nothing.
        /// </summary>
        public GlyphCacheFrame BeginFrame(object? source = null)
        {
            var thread = Environment.CurrentManagedThreadId;
            long frame;
            int id;

            lock (_frameLock)
            {
                for (var i = _open.Count - 1; i >= 0; i--)
                {
                    if (_open[i].Thread != thread)
                    {
                        continue;
                    }

                    if (!IsStale(_open[i].Start))
                    {
                        return default;
                    }

                    // A frame this thread left open long ago: its context was never disposed.
                    _open.RemoveAt(i);
                }

                frame = _frame + 1;
                Volatile.Write(ref _frame, frame);
                id = ++_nextFrameId;
                _open.Add(new OpenFrame(id, thread, frame));
                RecordSource(source, frame);
                UpdateFloors();
            }

            OnFrameStart(frame);

            return new GlyphCacheFrame(this, id);
        }

        /// <summary>Runs <paramref name="listener"/> once when the next frame begins.</summary>
        public void RunAtNextFrame(IGlyphCacheFrameListener listener)
        {
            lock (_frameLock)
            {
                if (!_frameListeners.Contains(listener))
                {
                    _frameListeners.Add(listener);
                }
            }
        }

        internal void EndFrame(int id)
        {
            lock (_frameLock)
            {
                for (var i = 0; i < _open.Count; i++)
                {
                    if (_open[i].Id == id)
                    {
                        _open.RemoveAt(i);
                        UpdateFloors();
                        return;
                    }
                }
            }
        }

        private bool IsStale(long start) => start < _frame - IdleFrames;

        private void RecordSource(object? source, long frame)
        {
            var slot = 0;

            if (source is not null)
            {
                slot = -1;

                for (var i = 1; i < _sources.Length; i++)
                {
                    if (_sources[i].Target is { } target && target.TryGetTarget(out var known) &&
                        ReferenceEquals(known, source))
                    {
                        slot = i;
                        break;
                    }
                }

                if (slot < 0)
                {
                    // A new source takes the slot of a collected or idle source, else the one
                    // that drew longest ago.
                    slot = 1;

                    for (var i = 1; i < _sources.Length; i++)
                    {
                        if (_sources[i].Target is not { } target || !target.TryGetTarget(out _) ||
                            IsStale(_sources[i].LastStart))
                        {
                            slot = i;
                            break;
                        }

                        if (_sources[i].LastStart < _sources[slot].LastStart)
                        {
                            slot = i;
                        }
                    }

                    _sources[slot] = new FrameSource { Target = new WeakReference<object>(source) };
                }
            }

            ref var entry = ref _sources[slot];

            entry.PreviousStart = entry.LastStart;
            entry.LastStart = frame;
        }

        private void UpdateFloors()
        {
            var pin = _frame;

            foreach (var open in _open)
            {
                if (!IsStale(open.Start) && open.Start < pin)
                {
                    pin = open.Start;
                }
            }

            var soft = pin;

            foreach (var source in _sources)
            {
                if (source.PreviousStart > 0 && !IsStale(source.LastStart) && source.PreviousStart < soft)
                {
                    soft = source.PreviousStart;
                }
            }

            Volatile.Write(ref _pinFloor, pin);
            Volatile.Write(ref _softFloor, soft);
        }

        private void OnFrameStart(long frame)
        {
            if (frame % SweepInterval == 0)
            {
                SweepCollectedPools();
            }

            RunFrameListeners(frame);
            TrimAtFrameStart();
        }

        private void RunFrameListeners(long frame)
        {
            List<IGlyphCacheFrameListener> listeners;

            lock (_frameLock)
            {
                if (_frameListeners.Count == 0)
                {
                    return;
                }

                listeners = _frameListeners;
                _frameListeners = _runningListeners;
                _runningListeners = listeners;
            }

            foreach (var listener in listeners)
            {
                listener.OnFrameStart(frame);
            }

            listeners.Clear();
        }

        /// <summary>
        /// Trims to the limit what no source drew in its last two frames, then, past a quarter
        /// over the limit, what no open frame draws.
        /// </summary>
        private void TrimAtFrameStart()
        {
            if (UsedBytes <= LimitBytes || Interlocked.CompareExchange(ref _trimming, 1, 0) != 0)
            {
                return;
            }

            try
            {
                Trim(LimitBytes, SoftFloor, fair: true);
                Trim(LimitBytes, SoftFloor, fair: false);

                if (UsedBytes > SoftLimitBytes)
                {
                    Trim(SoftLimitBytes, PinFloor, fair: false);
                }
            }
            finally
            {
                Volatile.Write(ref _trimming, 0);
            }
        }

        /// <summary>
        /// Evicts entries last used before <paramref name="usedBefore"/> until the caches hold at
        /// most <paramref name="target"/> bytes or no such entry is left. A <paramref name="fair"/>
        /// trim leaves each typeface the share of its recent entries it is owed.
        /// </summary>
        private void Trim(long target, long usedBefore, bool fair)
        {
            if (UsedBytes <= target)
            {
                return;
            }

            var count = SnapshotPools();
            var generation = ++_trimGeneration;
            var frame = Frame;
            var recent = frame - IdleFrames;
            var share = LimitBytes / 8;

            try
            {
                while (UsedBytes > target)
                {
                    GlyphCachePoolHandle? best = null;
                    IGlyphCachePool? bestPool = null;
                    var bestScore = long.MinValue;
                    var secondScore = long.MinValue;
                    var bestOldest = 0L;
                    var bestBefore = usedBefore;
                    var bestAllowance = long.MaxValue;

                    for (var i = 0; i < count; i++)
                    {
                        var handle = _trimPools[i];

                        if (handle.TrimGeneration == generation || handle.Bytes <= 0 ||
                            !handle.TryGetPool(out var pool))
                        {
                            continue;
                        }

                        var oldest = pool.OldestUse;

                        if (oldest >= usedBefore)
                        {
                            continue;
                        }

                        var before = usedBefore;
                        var allowance = long.MaxValue;

                        if (fair && handle.IsPerTypeface)
                        {
                            if (oldest >= recent)
                            {
                                // Only recent entries left: the face keeps its share of them.
                                allowance = handle.Bytes - share;

                                if (allowance <= 0)
                                {
                                    continue;
                                }
                            }
                            else
                            {
                                // Its idle entries go first; the recent ones wait for the share check.
                                before = Math.Min(before, recent);
                            }
                        }

                        var score = frame - oldest - s_minimumAge[handle.Priority];

                        if (score > bestScore)
                        {
                            secondScore = bestScore;
                            bestScore = score;
                            best = handle;
                            bestPool = pool;
                            bestOldest = oldest;
                            bestBefore = before;
                            bestAllowance = allowance;
                        }
                        else if (score > secondScore)
                        {
                            secondScore = score;
                        }
                    }

                    if (best is null)
                    {
                        return;
                    }

                    // Evict from the chosen pool while its entries still outrank the next pool's
                    // oldest, so ages interleave across pools.
                    var until = bestBefore;

                    if (secondScore != long.MinValue)
                    {
                        until = Math.Min(until, frame - s_minimumAge[best.Priority] - secondScore + 1);
                    }

                    until = Math.Max(until, bestOldest + 1);

                    var freed = bestPool!.EvictOldest(until, Math.Min(UsedBytes - target, bestAllowance));

                    Interlocked.Add(ref _evictedBytes, freed);

                    if (freed <= 0)
                    {
                        best.TrimGeneration = generation;
                    }
                }
            }
            finally
            {
                Array.Clear(_trimPools, 0, count);
            }
        }

        /// <summary>Copies the registered pools for a trim, which calls into pools without the registry lock.</summary>
        private int SnapshotPools()
        {
            lock (_poolsLock)
            {
                if (_trimPools.Length < _pools.Count)
                {
                    _trimPools = new GlyphCachePoolHandle[Math.Max(_pools.Count, _trimPools.Length * 2)];
                }

                _pools.CopyTo(_trimPools);

                return _pools.Count;
            }
        }

        /// <summary>
        /// Evicts from the pool of <paramref name="handle"/> alone, after a build took the caches
        /// past half over the limit: what earlier frames used, until the caches are back within it.
        /// </summary>
        internal void EvictInline(GlyphCachePoolHandle handle)
        {
            var over = UsedBytes - InlineLimitBytes;

            if (over > 0 && handle.TryGetPool(out var pool))
            {
                Interlocked.Add(ref _evictedBytes, pool.EvictOldest(PinFloor, over));
            }
        }

        /// <summary>
        /// Registers <paramref name="owner"/> as a pool of <paramref name="kind"/>. The budget
        /// holds the owner weakly; the owner keeps the returned handle and charges through it.
        /// An owner that implements <see cref="IGlyphCachePool"/> can be trimmed.
        /// </summary>
        /// <param name="kind">The kind of cache.</param>
        /// <param name="owner">The cache.</param>
        /// <param name="perTypeface">Whether the cache holds the entries of one typeface.</param>
        public GlyphCachePoolHandle Register(GlyphCachePoolKind kind, object owner, bool perTypeface = false)
        {
            var handle = new GlyphCachePoolHandle(this, kind, owner, perTypeface);

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

        private readonly record struct OpenFrame(int Id, int Thread, long Start);

        private struct FrameSource
        {
            public WeakReference<object>? Target;
            public long LastStart;
            public long PreviousStart;
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

        internal long Add(long bytes)
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

            return used;
        }
    }

    /// <summary>A frame begun by <see cref="GlyphCacheBudget.BeginFrame"/>; disposing it ends the frame.</summary>
    internal readonly struct GlyphCacheFrame : IDisposable
    {
        private readonly GlyphCacheBudget? _budget;
        private readonly int _id;

        internal GlyphCacheFrame(GlyphCacheBudget budget, int id)
        {
            _budget = budget;
            _id = id;
        }

        public void Dispose() => _budget?.EndFrame(_id);
    }

    /// <summary>Work a pool asks the budget to run when the next frame begins.</summary>
    internal interface IGlyphCacheFrameListener
    {
        /// <summary>Called on the thread that begins <paramref name="frame"/>, before it draws.</summary>
        void OnFrameStart(long frame);
    }

    /// <summary>
    /// A pool's registration with a <see cref="GlyphCacheBudget"/>: the bytes the pool holds,
    /// charged and credited by the pool as it adds and drops payload.
    /// </summary>
    internal sealed class GlyphCachePoolHandle
    {
        private readonly WeakReference<object> _owner;
        private long _bytes;
        private volatile bool _released;

        internal GlyphCachePoolHandle(GlyphCacheBudget budget, GlyphCachePoolKind kind, object owner,
            bool perTypeface)
        {
            Budget = budget;
            Kind = kind;
            IsPerTypeface = perTypeface;
            Priority = kind switch
            {
                GlyphCachePoolKind.RunMasks or GlyphCachePoolKind.SpriteSets => 1,
                GlyphCachePoolKind.Atlas or GlyphCachePoolKind.LcdAtlas => 2,
                GlyphCachePoolKind.Masks or GlyphCachePoolKind.Hinters => 3,
                _ => 4,
            };
            _owner = new WeakReference<object>(owner);
        }

        public GlyphCacheBudget Budget { get; }

        public GlyphCachePoolKind Kind { get; }

        /// <summary>
        /// The order in which kinds give up entries of equal age, cheapest to rebuild first: run
        /// masks and sprite sets compose again from cached masks, atlas pages upload again from
        /// cached masks, masks and hinters rasterize again, outlines parse and hint again.
        /// </summary>
        public int Priority { get; }

        /// <summary>Whether the pool holds the entries of one typeface.</summary>
        public bool IsPerTypeface { get; }

        /// <summary>The bytes the pool holds.</summary>
        public long Bytes => Interlocked.Read(ref _bytes);

        /// <summary>The trim in which the pool last had nothing to give.</summary>
        internal int TrimGeneration;

        internal bool IsOwnerAlive => _owner.TryGetTarget(out _);

        internal bool TryGetPool(out IGlyphCachePool pool)
        {
            if (_owner.TryGetTarget(out var owner) && owner is IGlyphCachePool trimmable && !_released)
            {
                pool = trimmable;
                return true;
            }

            pool = null!;
            return false;
        }

        /// <summary>
        /// Records that the pool now holds <paramref name="bytes"/> more. Past half over the
        /// limit, the pool evicts what earlier frames used at once; the caller may hold the
        /// pool's lock.
        /// </summary>
        public void Charge(long bytes)
        {
            if (bytes == 0 || _released)
            {
                return;
            }

            Interlocked.Add(ref _bytes, bytes);

            if (Budget.Add(bytes) > Budget.InlineLimitBytes && bytes > 0)
            {
                Budget.EvictInline(this);
            }
        }

        /// <summary>Records that the pool dropped <paramref name="bytes"/>.</summary>
        public void Credit(long bytes)
        {
            if (bytes == 0 || _released)
            {
                return;
            }

            Interlocked.Add(ref _bytes, -bytes);
            Budget.Add(-bytes);
        }

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
