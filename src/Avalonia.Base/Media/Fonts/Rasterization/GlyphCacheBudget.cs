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
    /// <para>
    /// Pools keep their own data structures and locks; the budget only counts. A pool that the
    /// GC collects without releasing its handle (a typeface nobody disposed) is credited by
    /// <see cref="SweepCollectedPools"/>.
    /// </para>
    /// <para>
    /// Recency is counted in frames. A drawing context that is not drawn inside another on its
    /// thread begins a frame (<see cref="BeginFrame"/>): a window's render pass, a bitmap rendered
    /// on its own. Every entry records the <see cref="Frame"/> it was last used in. What the open
    /// frames use is pinned (<see cref="PinFloor"/>), so a frame never evicts what it draws; what
    /// each source drew in its previous frame is the next candidate to keep
    /// (<see cref="SoftFloor"/>), since a static scene draws it again.
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

        private readonly object _poolsLock = new();
        private readonly List<GlyphCachePoolHandle> _pools = new();
        private readonly object _frameLock = new();
        private readonly List<OpenFrame> _open = new(4);
        private readonly FrameSource[] _sources = new FrameSource[MaxSources];
        private List<IGlyphCacheFrameListener> _frameListeners = new();
        private List<IGlyphCacheFrameListener> _runningListeners = new();
        private long _used;
        private long _peak;
        private long _frame;
        private long _pinFloor;
        private long _softFloor;
        private int _nextFrameId;

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

        /// <summary>Sets <see cref="LimitBytes"/>; the next frame trims to it.</summary>
        public void SetLimit(long limitBytes) => LimitBytes = Math.Max(1, limitBytes);

        /// <summary>Bytes charged by all pools together.</summary>
        public long UsedBytes => Interlocked.Read(ref _used);

        /// <summary>The highest <see cref="UsedBytes"/> since construction or <see cref="ResetPeak"/>.</summary>
        public long PeakBytes => Interlocked.Read(ref _peak);

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
