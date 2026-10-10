using System;
using System.Collections.Generic;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>How a transformed sprite is coloured.</summary>
    internal enum TransformedSpriteKind : byte
    {
        /// <summary>A monochrome glyph in the text foreground, with the coverage correction.</summary>
        Foreground,

        /// <summary>
        /// A COLR v0 layer in the text foreground. Layers skip the coverage correction: it is
        /// non-linear, so abutting layers whose coverages sum to full would show seams.
        /// </summary>
        ForegroundLayer,

        /// <summary>A COLR v0 layer in a palette colour, without the coverage correction.</summary>
        PaletteLayer,
    }

    /// <summary>
    /// One glyph mask of a transformed run: which mask (glyph and quarter-pixel phases; the rest
    /// of the mask key is shared by the run), where its top-left lands relative to the run's
    /// snapped origin pixel, and how it is coloured.
    /// </summary>
    internal struct TransformedSprite
    {
        public ushort Glyph;
        public byte PhaseX;
        public byte PhaseY;
        public TransformedSpriteKind Kind;
        public int X;
        public int Y;
        public int Width;
        public int Height;

        /// <summary>The straight ARGB palette colour of a <see cref="TransformedSpriteKind.PaletteLayer"/>.</summary>
        public uint Color;
    }

    /// <summary>
    /// Sprites of a run drawn by one backend call, in the run's order: all on one atlas page (or
    /// one standalone glyph image) and all coloured alike. A run's batches are drawn in order;
    /// sprites of a batch need not be consecutive in the run, since a sprite may join a batch
    /// ahead of batches it overlaps nothing of (see <see cref="GlyphAtlasBatchBuilder"/>).
    /// </summary>
    internal sealed class GlyphAtlasBatch : IDisposable
    {
        public GlyphAtlasBatch(GlyphAtlasPage? page, int start, int count, TransformedSpriteKind kind, uint color,
            IDisposable backend)
        {
            Page = page;
            Start = start;
            Count = count;
            Kind = kind;
            Color = color;
            Backend = backend;
        }

        /// <summary>The atlas page the sprites sample; <c>null</c> for a standalone glyph image.</summary>
        public GlyphAtlasPage? Page { get; }

        /// <summary>The run's index of the first sprite in this batch.</summary>
        public int Start { get; }

        public int Count { get; }

        public TransformedSpriteKind Kind { get; }

        /// <summary>The straight ARGB colour of a palette layer batch.</summary>
        public uint Color { get; }

        /// <summary>The backend's realized sprite arrays (and image, for a standalone glyph).</summary>
        public IDisposable Backend { get; }

        /// <summary>
        /// The same object for the batches of one run, built together, none of whose sprites
        /// overlaps a sprite of the run's other batches; <c>null</c> for a batch that overlaps
        /// one. Two batches carrying the same object, drawn at the same offset, put coverage on
        /// no pixel in common, so they may be drawn in either order.
        /// </summary>
        public object? DisjointRun { get; init; }

        public void Dispose() => Backend.Dispose();
    }

    /// <summary>
    /// A transformed run's glyph masks laid out relative to its snapped origin pixel, for one
    /// quantized linear transform and origin phase, or an upright run's on a hardware GPU. The
    /// coverage stays in the shared glyph storage (the glyph mask cache on raster contexts, the
    /// typeface's atlas on GPU contexts) and each draw places the glyph masks directly, so a
    /// run holds no bitmap of its own.
    /// </summary>
    internal sealed class TransformedGlyphSprites : IDisposable
    {
        // Text whose colour changes keeps the batches of its last few luminance buckets, since
        // each bucket's coverage is a set of atlas entries of its own.
        private const int BucketSlots = 4;

        private readonly TransformedSprite[] _sprites;
        private BucketBatches[]? _bucketBatches;
        private int _current;
        private int _next;
        private bool _disposed;

        public TransformedGlyphSprites(in RunMaskKey key, ushort emboldenQ, bool oblique, TransformedSprite[] sprites)
        {
            Key = key;
            EmboldenQ = emboldenQ;
            Oblique = oblique;
            _sprites = sprites;
            GlyphRasterDiagnostics.CountSpriteSetBuild();
        }

        public RunMaskKey Key { get; }

        /// <summary>
        /// Whether the sprites are an upright run's grid-fitted glyph masks, which live in the
        /// glyph mask cache like every upright mask, rather than transformed ones.
        /// </summary>
        public bool IsUpright { get; init; }

        /// <summary>The bold simulation of the glyph masks, as <see cref="GlyphMaskKey.EmboldenQ"/>.</summary>
        public ushort EmboldenQ { get; }

        /// <summary>Whether the glyph masks are slanted, as <see cref="GlyphMaskKey.Oblique"/>.</summary>
        public bool Oblique { get; }

        public ReadOnlySpan<TransformedSprite> Sprites => _sprites;

        public int Count => _sprites.Length;

        /// <summary>
        /// The realized atlas batches last built or drawn, in draw order; <c>null</c> until first
        /// drawn on a GPU context.
        /// </summary>
        public GlyphAtlasBatch[]? Batches => _bucketBatches?[_current].Batches;

        /// <summary>
        /// The glyph masks the raster blitter reads, one per sprite; <c>null</c> until first
        /// drawn on a raster context. Holding the masks keeps them valid after the glyph mask
        /// cache evicts them, until the run rebuilds its sprites.
        /// </summary>
        public GlyphMask[]? Masks
        {
            get => _masks;
            internal set
            {
                _masks = value;
                Owner?.Recharge();
            }
        }

        private GlyphMask[]? _masks;

        /// <summary>The run state holding this set, which charges its bytes to the glyph cache budget.</summary>
        internal TransformedRunState? Owner { get; set; }

        /// <summary>Bytes of the pre-tinted images in <see cref="FallbackImages"/>.</summary>
        internal long FallbackImageBytes { get; private set; }

        /// <summary>
        /// Pre-tinted per-sprite bitmaps for draws that cannot write the surface directly (a
        /// layer, an opacity, a complex clip), tinted with <see cref="FallbackTint"/>.
        /// </summary>
        internal IDisposable?[]? FallbackImages { get; private set; }

        /// <summary>The premultiplied BGRA tint <see cref="FallbackImages"/> were made with.</summary>
        internal uint FallbackTint { get; private set; }

        public bool IsDisposed => _disposed;

        /// <summary>Bytes of the sprite arrays this set holds.</summary>
        public long ByteCost
        {
            get
            {
                var cost = (long)_sprites.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<TransformedSprite>();

                if (_bucketBatches is { } slots)
                {
                    // The backend holds a source rectangle and a placement per sprite, four
                    // floats each.
                    foreach (var slot in slots)
                    {
                        foreach (var batch in slot.Batches ?? Array.Empty<GlyphAtlasBatch>())
                        {
                            cost += batch.Count * 32L;
                        }
                    }
                }

                if (Masks is not null)
                {
                    cost += (long)Masks.Length * IntPtr.Size;
                }

                return cost;
            }
        }

        /// <summary>
        /// The full glyph mask key of sprite <paramref name="index"/>; transformed keys are
        /// never grid-fitted, so their grid fit and pen snap are off. COLR layers are never
        /// simulated (see <see cref="GlyphTypeface.IsColorGlyph"/>), so only foreground glyph
        /// sprites carry the run's simulation.
        /// </summary>
        public GlyphMaskKey GetGlyphKey(int index)
        {
            ref readonly var sprite = ref _sprites[index];
            var simulated = sprite.Kind == TransformedSpriteKind.Foreground;

            return new GlyphMaskKey(sprite.Glyph, Key.ScaleQ, sprite.PhaseX, Key.Mode, Key.GridFit, Key.Strong,
                EmboldenQ: simulated ? EmboldenQ : (ushort)0, Oblique: simulated && Oblique, Transform: Key.Transform,
                PhaseY: sprite.PhaseY);
        }

        /// <summary>
        /// The batches drawable from <paramref name="atlas"/> in a foreground of luminance
        /// <paramref name="bucket"/>: built from it for that bucket, and none of their pages
        /// evicted.
        /// </summary>
        public bool TryGetBatches(GlyphMaskAtlas atlas, int bucket, out GlyphAtlasBatch[] batches)
        {
            batches = null!;

            if (_bucketBatches is not { } slots)
            {
                return false;
            }

            for (var i = 0; i < slots.Length; i++)
            {
                ref readonly var slot = ref slots[i];

                if (slot.Batches is null || slot.Bucket != bucket || slot.Atlas != atlas)
                {
                    continue;
                }

                foreach (var batch in slot.Batches)
                {
                    if (batch.Page is { IsEvicted: true })
                    {
                        return false;
                    }
                }

                _current = i;
                batches = slot.Batches;

                return true;
            }

            return false;
        }

        /// <summary>
        /// Keeps the batches of <paramref name="bucket"/>, replacing those of the same bucket or
        /// of the bucket stored longest ago.
        /// </summary>
        internal void SetBatches(GlyphMaskAtlas atlas, int bucket, GlyphAtlasBatch[] batches)
        {
            var slots = _bucketBatches ??= new BucketBatches[BucketSlots];
            var index = -1;

            for (var i = 0; i < slots.Length; i++)
            {
                if (slots[i].Batches is not null && slots[i].Bucket == bucket)
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                index = _next;
                _next = (_next + 1) % slots.Length;
            }

            DisposeBatches(slots[index].Batches);

            slots[index] = new BucketBatches(bucket, atlas, batches);
            _current = index;
            Owner?.Recharge();
        }

        internal void SetFallbackImages(IDisposable?[] images, uint tint)
        {
            DisposeFallbackImages();

            FallbackImages = images;
            FallbackTint = tint;

            // Sprites showing the same mask share one image, which is counted once.
            var distinct = new HashSet<object>(ReferenceEqualityComparer.Instance);
            long bytes = 0;

            for (var i = 0; i < images.Length; i++)
            {
                if (images[i] is { } image && distinct.Add(image))
                {
                    bytes += (long)_sprites[i].Width * _sprites[i].Height * 4;
                }
            }

            FallbackImageBytes = bytes;
            Owner?.Recharge();
        }

        private void DisposeFallbackImages()
        {
            if (FallbackImages is { } images)
            {
                // Sprites showing the same mask share one image.
                for (var i = 0; i < images.Length; i++)
                {
                    var image = images[i];

                    if (image is null)
                    {
                        continue;
                    }

                    image.Dispose();

                    for (var j = i + 1; j < images.Length; j++)
                    {
                        if (ReferenceEquals(images[j], image))
                        {
                            images[j] = null;
                        }
                    }
                }
            }

            FallbackImages = null;
            FallbackImageBytes = 0;
        }

        private static void DisposeBatches(GlyphAtlasBatch[]? batches)
        {
            if (batches is null)
            {
                return;
            }

            foreach (var batch in batches)
            {
                batch.Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_bucketBatches is { } slots)
            {
                for (var i = 0; i < slots.Length; i++)
                {
                    DisposeBatches(slots[i].Batches);
                    slots[i] = default;
                }
            }

            DisposeFallbackImages();
        }

        private readonly record struct BucketBatches(int Bucket, GlyphMaskAtlas? Atlas, GlyphAtlasBatch[]? Batches);
    }

    /// <summary>
    /// A run's transformed sprite sets: one primary slot for the dominant case plus a short ring
    /// for other transforms or phases, the same shape as <see cref="RunMaskCache"/>. Owned and
    /// disposed by the run.
    /// </summary>
    /// <remarks>
    /// The sets are charged to the glyph cache budget. The secondary sets record the frame of
    /// their last use, and the budget may evict those of earlier frames
    /// (<see cref="SpriteSetPool"/>) on the thread that draws the run; the primary slot and the
    /// settled set stay until the run is disposed.
    /// </remarks>
    internal sealed class TransformedRunState : IDisposable
    {
        private const int SecondarySize = 3;

        private readonly GlyphCacheBudget _budget;
        private long _charged;
        private TransformedGlyphSprites? _primary;
        private TransformedGlyphSprites?[]? _secondary;
        private long[]? _secondaryUse;
        private int _nextEvict;
        private int _ownerThread;
        private bool _disposed;

        /// <param name="budget">
        /// The budget the sprite sets are charged to; <see cref="GlyphCacheBudget.Shared"/> when omitted.
        /// </param>
        public TransformedRunState(GlyphCacheBudget? budget = null)
        {
            _budget = budget ?? GlyphCacheBudget.Shared;
        }

        /// <summary>The number of cached sprite sets; for diagnostics and tests.</summary>
        public int Count
        {
            get
            {
                var count = _primary is null ? 0 : 1;

                if (_secondary is { } secondary)
                {
                    foreach (var entry in secondary)
                    {
                        if (entry is not null)
                        {
                            count++;
                        }
                    }
                }

                return count;
            }
        }

        /// <summary>
        /// The sprite set the run's last static frame drew. While the run animates on a software
        /// GPU, this batch drawn under the change from <see cref="SettledTransform"/> to the
        /// current transform stands in for rasterizing every frame.
        /// </summary>
        public TransformedGlyphSprites? Settled { get; private set; }

        /// <summary>The device transform of the last static frame.</summary>
        public Matrix SettledTransform { get; private set; }

        /// <summary>The snapped origin pixel of the last static frame.</summary>
        public int SettledOriginX { get; private set; }

        public int SettledOriginY { get; private set; }

        /// <summary>Records a static frame.</summary>
        public void Settle(TransformedGlyphSprites sprites, in Matrix transform, int originX, int originY)
        {
            Settled = sprites;
            SettledTransform = transform;
            SettledOriginX = originX;
            SettledOriginY = originY;
        }

        /// <summary>Bytes of all cached sprite arrays; for diagnostics and tests.</summary>
        public long ByteCost
        {
            get
            {
                var cost = _primary?.ByteCost ?? 0;

                if (_secondary is { } secondary)
                {
                    foreach (var entry in secondary)
                    {
                        cost += entry?.ByteCost ?? 0;
                    }
                }

                return cost;
            }
        }

        /// <summary>
        /// Changes whenever a sprite set enters or leaves the state or the state is disposed, so a
        /// set taken from it at one version is still held, and not disposed, while the version
        /// stays.
        /// </summary>
        public int Version { get; private set; }

        public bool TryGet(in RunMaskKey key, out TransformedGlyphSprites sprites)
        {
            if (_primary is { } primary && primary.Key == key)
            {
                sprites = primary;
                return true;
            }

            if (_secondary is { } secondary)
            {
                for (var i = 0; i < secondary.Length; i++)
                {
                    if (secondary[i] is { } entry && entry.Key == key)
                    {
                        _secondaryUse![i] = _budget.Frame;
                        sprites = entry;
                        return true;
                    }
                }
            }

            sprites = null!;
            return false;
        }

        public void Add(TransformedGlyphSprites sprites)
        {
            Version++;
            sprites.Owner = this;
            _ownerThread = Environment.CurrentManagedThreadId;

            if (_primary is null)
            {
                _primary = sprites;
                Recharge();
                return;
            }

            if (_secondary is null)
            {
                _secondary = new TransformedGlyphSprites?[SecondarySize];
                _secondaryUse = new long[SecondarySize];
                _budget.SpriteSets.Track(this);
            }

            ref var slot = ref _secondary[_nextEvict];
            slot?.Dispose();
            slot = sprites;
            _secondaryUse![_nextEvict] = _budget.Frame;
            _nextEvict = (_nextEvict + 1) % SecondarySize;
            Recharge();
        }

        /// <summary>Charges the change in the bytes the cached sets hold since the last charge.</summary>
        internal void Recharge()
        {
            if (_disposed)
            {
                return;
            }

            var bytes = ChargedBytes(_primary);

            if (_secondary is { } secondary)
            {
                foreach (var entry in secondary)
                {
                    bytes += ChargedBytes(entry);
                }
            }

            _budget.SpriteSets.Handle.Charge(bytes - _charged);
            _charged = bytes;
        }

        private static long ChargedBytes(TransformedGlyphSprites? sprites)
            => sprites is null ? 0 : sprites.ByteCost + sprites.FallbackImageBytes;

        /// <summary>
        /// Whether the run is drawn on the current thread, or no frame is being drawn, so its
        /// sets may be evicted here.
        /// </summary>
        internal bool IsOwnedByCurrentThread =>
            _ownerThread == Environment.CurrentManagedThreadId || GlyphCacheBudget.IsQuiescentTrim;

        internal int SecondaryCount => _secondary?.Length ?? 0;

        /// <summary>
        /// The frame secondary slot <paramref name="index"/> was last used in;
        /// <see cref="long.MaxValue"/> when it is empty or holds the settled set.
        /// </summary>
        internal long SecondaryLastUse(int index)
            => _secondary![index] is { } sprites && !ReferenceEquals(sprites, Settled)
                ? _secondaryUse![index]
                : long.MaxValue;

        /// <summary>Evicts secondary slot <paramref name="index"/>; returns the bytes freed.</summary>
        internal long EvictSecondary(int index)
        {
            if (_secondary![index] is not { } sprites || ReferenceEquals(sprites, Settled))
            {
                return 0;
            }

            var before = _charged;

            Version++;
            _secondary[index] = null;
            sprites.Dispose();
            Recharge();

            return before - _charged;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                Version++;
                return;
            }

            // Leaves the pool first, so no trim evicts a set this disposes.
            if (_secondary is not null)
            {
                _budget.SpriteSets.Untrack(this);
            }

            _disposed = true;
            Version++;
            Settled = null;
            _primary?.Dispose();
            _primary = null;

            if (_secondary is { } secondary)
            {
                for (var i = 0; i < secondary.Length; i++)
                {
                    secondary[i]?.Dispose();
                    secondary[i] = null;
                }
            }

            _budget.SpriteSets.Handle.Credit(_charged);
            _charged = 0;
        }
    }

    /// <summary>
    /// The sprite sets of every run, as one pool of a <see cref="GlyphCacheBudget"/>: the
    /// secondary sets of the runs that have them may be evicted, oldest first. A run's sets are
    /// only touched on the thread that draws the run, between its frames, since the pending draws
    /// of a frame may still hold them.
    /// </summary>
    internal sealed class SpriteSetPool : IGlyphCachePool
    {
        private readonly object _lock = new();
        private readonly HashSet<TransformedRunState> _states = new();
        private readonly List<(TransformedRunState State, int Slot, long LastUse)> _candidates = new();

        internal SpriteSetPool(GlyphCacheBudget budget)
        {
            Handle = budget.Register(GlyphCachePoolKind.SpriteSets, this);
        }

        /// <summary>The registration every run state charges through.</summary>
        public GlyphCachePoolHandle Handle { get; }

        internal void Track(TransformedRunState state)
        {
            lock (_lock)
            {
                _states.Add(state);
            }
        }

        internal void Untrack(TransformedRunState state)
        {
            lock (_lock)
            {
                _states.Remove(state);
            }
        }

        public long OldestUse
        {
            get
            {
                var oldest = long.MaxValue;

                lock (_lock)
                {
                    foreach (var state in _states)
                    {
                        if (!state.IsOwnedByCurrentThread)
                        {
                            continue;
                        }

                        for (var i = 0; i < state.SecondaryCount; i++)
                        {
                            oldest = Math.Min(oldest, state.SecondaryLastUse(i));
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
                foreach (var state in _states)
                {
                    if (!state.IsOwnedByCurrentThread)
                    {
                        continue;
                    }

                    for (var i = 0; i < state.SecondaryCount; i++)
                    {
                        var lastUse = state.SecondaryLastUse(i);

                        if (lastUse < usedBefore)
                        {
                            _candidates.Add((state, i, lastUse));
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

                    freed += candidate.State.EvictSecondary(candidate.Slot);
                }

                _candidates.Clear();
            }

            return freed;
        }
    }
}
