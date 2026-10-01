using System;
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
        public RunMaskPart(IDisposable handle, int offsetX, int offsetY, int width, int height)
        {
            Handle = handle;
            OffsetX = offsetX;
            OffsetY = offsetY;
            Width = width;
            Height = height;
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
        }

        /// <summary>The realized parts, left to right, then top to bottom.</summary>
        public ReadOnlySpan<RunMaskPart> Parts => _parts;

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
    internal sealed class RunMaskCache : IDisposable
    {
        private const int SecondarySize = 3;

        private RunMaskKey _primaryKey;
        private RunMask? _primary;
        private (RunMaskKey Key, RunMask Mask)[]? _secondary;
        private int _nextEvict;

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
                primary.Dispose();
                _primary = null;
                return;
            }

            if (_secondary is { } secondary)
            {
                for (var i = 0; i < secondary.Length; i++)
                {
                    if (secondary[i].Mask is { } mask && secondary[i].Key == key)
                    {
                        mask.Dispose();
                        secondary[i] = default;
                        return;
                    }
                }
            }
        }

        public void Add(in RunMaskKey key, RunMask mask)
        {
            if (_primary is null)
            {
                _primaryKey = key;
                _primary = mask;
                return;
            }

            _secondary ??= new (RunMaskKey, RunMask)[SecondarySize];

            ref var slot = ref _secondary[_nextEvict];
            slot.Mask?.Dispose();
            slot = (key, mask);
            _nextEvict = (_nextEvict + 1) % SecondarySize;
        }

        public void Dispose()
        {
            _primary?.Dispose();
            _primary = null;

            if (_secondary is { } secondary)
            {
                for (var i = 0; i < secondary.Length; i++)
                {
                    secondary[i].Mask?.Dispose();
                    secondary[i] = default;
                }
            }
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
