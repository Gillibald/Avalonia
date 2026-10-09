using System;
using System.Collections.Generic;
using Avalonia.Platform;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Cache identity of a composed run mask. Everything positional is relative to the run's
    /// snapped origin pixel, so scrolling by whole pixels reuses the same mask; fractional
    /// horizontal motion cycles the four origin phases. <see cref="Tint"/> is the premultiplied
    /// BGRA of a solid foreground, zero for the untinted alpha variant, or
    /// <see cref="CoverageTint"/> for the untinted coverage a raster surface blends (neither is
    /// a drawable premultiplied tint, so the sentinels cannot collide). Opacity is deliberately
    /// absent — it rides the draw call, so fades reuse the cached mask (D7). A transformed run
    /// also carries its quantized linear part and a vertical origin phase; upright runs leave
    /// both at their defaults.
    /// </summary>
    internal readonly record struct RunMaskKey(ushort ScaleQ, byte OriginPhase, GlyphMaskMode Mode, uint Tint, bool GridFit = true, bool PenSnap = false,
        GlyphMaskTransform Transform = default, byte OriginPhaseY = 0)
    {
        /// <summary>
        /// The <see cref="Tint"/> of a run's <see cref="RunCoverage"/>: colour channels above a
        /// zero alpha, which no premultiplied tint has.
        /// </summary>
        public const uint CoverageTint = 0x00FFFFFF;
    }

    /// <summary>
    /// Which run mask an upright run's last static frame drew, and where: the key, the device
    /// transform and the snapped origin pixel.
    /// </summary>
    internal readonly record struct SettledRunMask(RunMaskKey Key, Matrix Transform, int OriginX, int OriginY);

    /// <summary>
    /// The glyph mask pixels an upright frame used, and the pixels of the coverage it built.
    /// </summary>
    internal readonly record struct UprightRasterCost(long MaskPixels, long CoveragePixels);

    /// <summary>
    /// The portable subpixel draw payload: per-channel blending without backend support
    /// decomposes into two standard blits, a Multiply pass carrying the inverse corrected
    /// coverage and a Plus pass carrying the pre-tinted corrected coverage. The payload pixels
    /// stay in managed arrays, premultiplied BGRA, which a raster surface blends in one pass
    /// with the bytes of the two blits (<see cref="LcdMaskBlitter"/>); the bitmaps for the
    /// blits are made from them by the first draw that goes through the backend.
    /// </summary>
    internal sealed class LcdRunPayload : IDisposable
    {
        private LcdRunBitmaps? _bitmaps;

        public LcdRunPayload(uint[] multiply, uint[] plus, int width, int height)
        {
            Multiply = multiply;
            Plus = plus;
            Width = width;
            Height = height;
        }

        /// <summary>The Multiply payload, <see cref="Width"/> pixels per row.</summary>
        public uint[] Multiply { get; }

        /// <summary>The Plus payload, <see cref="Width"/> pixels per row.</summary>
        public uint[] Plus { get; }

        public int Width { get; }

        public int Height { get; }

        /// <summary>Whether the backend bitmaps have been made; for tests.</summary>
        internal bool HasBitmaps => _bitmaps is not null;

        /// <summary>The two blit bitmaps, made on first use.</summary>
        public unsafe LcdRunBitmaps GetBitmaps(IPlatformRenderInterface renderInterface)
        {
            if (_bitmaps is { } existing)
            {
                return existing;
            }

            var multiply = CreateBitmap(renderInterface, Multiply);

            try
            {
                _bitmaps = new LcdRunBitmaps(multiply, CreateBitmap(renderInterface, Plus));
            }
            catch
            {
                multiply.Dispose();
                throw;
            }

            return _bitmaps;
        }

        private unsafe IWriteableBitmapImpl CreateBitmap(IPlatformRenderInterface renderInterface, uint[] pixels)
        {
            var bitmap = renderInterface.CreateWriteableBitmap(new PixelSize(Width, Height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);

            // Written once; the bitmap is never locked again, so its backend image stays stable.
            using (var framebuffer = bitmap.Lock())
            {
                for (var row = 0; row < Height; row++)
                {
                    new ReadOnlySpan<uint>(pixels, row * Width, Width).CopyTo(
                        new Span<uint>((byte*)framebuffer.Address + row * framebuffer.RowBytes, Width));
                }
            }

            return bitmap;
        }

        public void Dispose()
        {
            _bitmaps?.Dispose();
            _bitmaps = null;
        }
    }

    /// <summary>The Multiply and Plus bitmaps of an <see cref="LcdRunPayload"/>.</summary>
    internal sealed class LcdRunBitmaps : IDisposable
    {
        public LcdRunBitmaps(IDisposable multiply, IDisposable plus)
        {
            Multiply = multiply;
            Plus = plus;
        }

        public IDisposable Multiply { get; }

        public IDisposable Plus { get; }

        public void Dispose()
        {
            Multiply.Dispose();
            Plus.Dispose();
        }
    }

    /// <summary>
    /// One realized bitmap of a composed run mask plus its placement relative to the run's
    /// snapped origin pixel.
    /// </summary>
    internal readonly struct RunMaskPart
    {
        /// <param name="handle">The realized drawable.</param>
        /// <param name="offsetX">Part left relative to the run's snapped origin pixel.</param>
        /// <param name="offsetY">Part top relative to the run's snapped origin pixel.</param>
        /// <param name="width">Part width in device pixels.</param>
        /// <param name="height">Part height in device pixels.</param>
        /// <param name="bytes">
        /// The memory the part holds of its own, charged to the glyph cache budget: its pixels,
        /// or 0 when they live in storage that charges itself (an <see cref="LcdRunAtlas"/> entry).
        /// </param>
        public RunMaskPart(IDisposable handle, int offsetX, int offsetY, int width, int height, long bytes)
        {
            Handle = handle;
            OffsetX = offsetX;
            OffsetY = offsetY;
            Width = width;
            Height = height;
            Bytes = bytes;
        }

        /// <summary>
        /// The realized drawable: a pre-tinted <see cref="IBitmapImpl"/> or an
        /// <see cref="LcdRunPayload"/> on the portable floor, or a backend mask handle from
        /// <see cref="IAlphaGlyphMaskContext"/>.
        /// </summary>
        public IDisposable Handle { get; }

        /// <summary>Part top-left relative to the run's snapped origin pixel, device px.</summary>
        public int OffsetX { get; }

        public int OffsetY { get; }

        public int Width { get; }

        public int Height { get; }

        /// <summary>The memory the part holds of its own.</summary>
        public long Bytes { get; }
    }

    /// <summary>
    /// An immutable composed run mask. Its bitmaps are written exactly once (inside the
    /// composing lock, before first draw) and never mutated afterwards, which is what makes the
    /// backend's image-identity caching turn them into GPU-resident textures after the first
    /// draw (D8).
    /// </summary>
    /// <remarks>
    /// A run wider than the drawing context's run-mask bound is split into several parts, each
    /// covering a disjoint range of device columns over the full height of the composed union.
    /// Every part composes every glyph whose mask reaches into it, clipped at the part edges,
    /// and each pixel's value depends only on the glyphs covering that pixel, in run order. Every pixel therefore holds exactly the value a single mask would hold, and
    /// since the parts do not overlap, each destination pixel is blended once. Glyph ink
    /// crossing a part edge, overlapping neighbours and kerning need no special boundary rule.
    /// </remarks>
    internal sealed class RunMask : IDisposable
    {
        private readonly RunMaskPart[] _parts;
        private bool _disposed;

        public RunMask(RunMaskPart[] parts)
        {
            _parts = parts;

            foreach (var part in parts)
            {
                ByteCost += part.Bytes;
            }
        }

        /// <summary>The realized parts, left to right, then top to bottom.</summary>
        public ReadOnlySpan<RunMaskPart> Parts => _parts;

        /// <summary>The memory the parts hold of their own.</summary>
        public long ByteCost { get; }

        /// <summary>
        /// Whether an atlas dropped the storage of a part, so the mask no longer holds the
        /// run's coverage and must be composed again.
        /// </summary>
        public bool IsEvicted
        {
            get
            {
                foreach (var part in _parts)
                {
                    if (part.Handle is LcdAtlasEntry { IsEvicted: true })
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (var part in _parts)
            {
                part.Handle.Dispose();
            }
        }
    }

    /// <summary>
    /// A small per-run cache of composed masks, mirroring the shape of the Skia blob cache: one
    /// primary slot for the dominant repeat case plus a short overflow ring for phase/tint
    /// variants. Owned by the run impl, which is deterministically ref-counted by the scene
    /// graph, so disposing evicted (and finally all) masks here cannot outlive a consumer —
    /// the same lifetime contract the SKTextBlob cache relies on today.
    /// </summary>
    /// <remarks>
    /// The masks are charged to the glyph cache budget. The secondary masks record the frame of
    /// their last use, and the budget may evict those of earlier frames
    /// (<see cref="RunMaskPool"/>) on the thread that draws the run; the primary slot stays
    /// until the run is disposed.
    /// </remarks>
    internal sealed class RunMaskCache : IDisposable
    {
        private const int SecondarySize = 3;

        private readonly GlyphCacheBudget _budget;
        private RunMaskKey _primaryKey;
        private RunMask? _primary;
        private Slot[]? _secondary;
        private int _nextEvict;
        private int _ownerThread;
        private bool _disposed;

        /// <param name="budget">
        /// The budget the masks are charged to; <see cref="GlyphCacheBudget.Shared"/> when omitted.
        /// </param>
        public RunMaskCache(GlyphCacheBudget? budget = null)
        {
            _budget = budget ?? GlyphCacheBudget.Shared;
        }

        /// <summary>The number of cached masks; for diagnostics and tests.</summary>
        public int Count
        {
            get
            {
                var count = _primary is null ? 0 : 1;

                if (_secondary is { } secondary)
                {
                    foreach (var entry in secondary)
                    {
                        if (entry.Mask is not null)
                        {
                            count++;
                        }
                    }
                }

                return count;
            }
        }

        public bool TryGet(in RunMaskKey key, out RunMask mask)
        {
            if (_primary is { } primary && _primaryKey == key)
            {
                mask = primary;
                return true;
            }

            if (_secondary is { } secondary)
            {
                for (var i = 0; i < secondary.Length; i++)
                {
                    if (secondary[i].Mask is { } hit && secondary[i].Key == key)
                    {
                        secondary[i].LastUse = _budget.Frame;
                        mask = hit;
                        return true;
                    }
                }
            }

            mask = null!;
            return false;
        }

        /// <summary>Drops and disposes the mask cached under <paramref name="key"/>, if any.</summary>
        public void Remove(in RunMaskKey key)
        {
            if (_primary is { } primary && _primaryKey == key)
            {
                Drop(primary);
                _primary = null;
                return;
            }

            if (_secondary is { } secondary)
            {
                for (var i = 0; i < secondary.Length; i++)
                {
                    if (secondary[i].Mask is { } mask && secondary[i].Key == key)
                    {
                        Drop(mask);
                        secondary[i] = default;
                        return;
                    }
                }
            }
        }

        public void Add(in RunMaskKey key, RunMask mask)
        {
            _ownerThread = Environment.CurrentManagedThreadId;
            _budget.RunMasks.Handle.Charge(mask.ByteCost);

            if (_primary is null)
            {
                _primaryKey = key;
                _primary = mask;
                return;
            }

            if (_secondary is null)
            {
                _secondary = new Slot[SecondarySize];
                _budget.RunMasks.Track(this);
            }

            ref var slot = ref _secondary[_nextEvict];

            if (slot.Mask is { } replaced)
            {
                Drop(replaced);
            }

            slot = new Slot { Key = key, Mask = mask, LastUse = _budget.Frame };
            _nextEvict = (_nextEvict + 1) % SecondarySize;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Leaves the pool first, so no trim evicts a mask this disposes.
            if (_secondary is not null)
            {
                _budget.RunMasks.Untrack(this);
            }

            if (_primary is { } primary)
            {
                Drop(primary);
                _primary = null;
            }

            if (_secondary is { } secondary)
            {
                for (var i = 0; i < secondary.Length; i++)
                {
                    if (secondary[i].Mask is { } mask)
                    {
                        Drop(mask);
                    }

                    secondary[i] = default;
                }
            }
        }

        /// <summary>
        /// Whether the run is drawn on the current thread, or no frame is being drawn, so its
        /// masks may be evicted here.
        /// </summary>
        internal bool IsOwnedByCurrentThread =>
            _ownerThread == Environment.CurrentManagedThreadId || GlyphCacheBudget.IsQuiescentTrim;

        internal int SecondaryCount => _secondary?.Length ?? 0;

        /// <summary>The frame secondary slot <paramref name="index"/> was last used in, <see cref="long.MaxValue"/> when empty.</summary>
        internal long SecondaryLastUse(int index)
            => _secondary![index].Mask is null ? long.MaxValue : _secondary[index].LastUse;

        /// <summary>Evicts secondary slot <paramref name="index"/>; returns the bytes freed.</summary>
        internal long EvictSecondary(int index)
        {
            ref var slot = ref _secondary![index];

            if (slot.Mask is not { } mask)
            {
                return 0;
            }

            slot = default;
            Drop(mask);

            return mask.ByteCost;
        }

        private void Drop(RunMask mask)
        {
            _budget.RunMasks.Handle.Credit(mask.ByteCost);
            mask.Dispose();
        }

        private struct Slot
        {
            public RunMaskKey Key;
            public RunMask? Mask;
            public long LastUse;
        }
    }

    /// <summary>
    /// The composed run masks of every run, as one pool of a <see cref="GlyphCacheBudget"/>: the
    /// secondary masks of the runs that have them may be evicted, oldest first. A run's masks
    /// are only touched on the thread that draws the run, between its frames, since the pending
    /// draws of a frame may still hold them.
    /// </summary>
    internal sealed class RunMaskPool : IGlyphCachePool
    {
        private readonly object _lock = new();
        private readonly HashSet<RunMaskCache> _caches = new();
        private readonly List<(RunMaskCache Cache, int Slot, long LastUse)> _candidates = new();

        internal RunMaskPool(GlyphCacheBudget budget)
        {
            Handle = budget.Register(GlyphCachePoolKind.RunMasks, this);
        }

        /// <summary>The registration every run mask cache charges through.</summary>
        public GlyphCachePoolHandle Handle { get; }

        internal void Track(RunMaskCache cache)
        {
            lock (_lock)
            {
                _caches.Add(cache);
            }
        }

        internal void Untrack(RunMaskCache cache)
        {
            lock (_lock)
            {
                _caches.Remove(cache);
            }
        }

        public long OldestUse
        {
            get
            {
                var oldest = long.MaxValue;

                lock (_lock)
                {
                    foreach (var cache in _caches)
                    {
                        if (!cache.IsOwnedByCurrentThread)
                        {
                            continue;
                        }

                        for (var i = 0; i < cache.SecondaryCount; i++)
                        {
                            oldest = Math.Min(oldest, cache.SecondaryLastUse(i));
                        }
                    }
                }

                return oldest;
            }
        }

        public long EvictOldest(long usedBefore, long bytes)
        {
            long freed = 0;

            lock (_lock)
            {
                foreach (var cache in _caches)
                {
                    if (!cache.IsOwnedByCurrentThread)
                    {
                        continue;
                    }

                    for (var i = 0; i < cache.SecondaryCount; i++)
                    {
                        var lastUse = cache.SecondaryLastUse(i);

                        if (lastUse < usedBefore)
                        {
                            _candidates.Add((cache, i, lastUse));
                        }
                    }
                }

                _candidates.Sort(static (a, b) => a.LastUse.CompareTo(b.LastUse));

                foreach (var candidate in _candidates)
                {
                    if (freed >= bytes)
                    {
                        break;
                    }

                    freed += candidate.Cache.EvictSecondary(candidate.Slot);
                }

                _candidates.Clear();
            }

            return freed;
        }
    }

    /// <summary>
    /// Watches one run's draws for a transform that changes every frame, such as a rotation or
    /// zoom animation, whose masks would never be drawn again.
    /// </summary>
    /// <remarks>
    /// Three consecutive changes mark the run as animating: a one-off relayout or zoom step,
    /// and a run drawn under two alternating transforms (a reflection, a second view), keep
    /// rasterizing, while an animation is recognized by its third frame, so at most three
    /// frames of its masks enter the caches. A repeated transform resets the count, which makes
    /// the first draw after the transform holds still rasterize again. A cache hit resets it
    /// too, unless the caller holds the count on hits: then an animation passing through a
    /// cached transform keeps counting as animating once it moves on, instead of rasterizing
    /// its next frames while the count builds up again.
    /// </remarks>
    internal sealed class TransformChurnGuard
    {
        /// <summary>Consecutive transform changes after which the run counts as animating.</summary>
        public const int Threshold = 3;

        private bool _hasLast;
        private ushort _lastScaleQ;
        private GlyphMaskTransform _lastTransform;
        private int _changes;

        /// <summary>
        /// Records a draw of the run and returns whether the run is animating, so its masks
        /// should not be rasterized for this frame. With <paramref name="holdOnCacheHit"/>, a
        /// changed transform that hits the cache leaves the count as it is instead of
        /// resetting it.
        /// </summary>
        public bool Record(ushort scaleQ, GlyphMaskTransform transform, bool cacheHit, bool holdOnCacheHit = false)
        {
            var changed = _hasLast && (scaleQ != _lastScaleQ || transform != _lastTransform);

            _changes = !changed ? 0 : !cacheHit ? _changes + 1 : holdOnCacheHit ? _changes : 0;
            _hasLast = true;
            _lastScaleQ = scaleQ;
            _lastTransform = transform;

            return _changes >= Threshold;
        }
    }
}
