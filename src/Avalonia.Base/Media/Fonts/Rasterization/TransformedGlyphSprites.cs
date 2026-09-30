using System;

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
    /// Consecutive sprites of a run drawn by one backend call: all on one atlas page (or one
    /// standalone glyph image) and all coloured alike.
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

        /// <summary>The first sprite of the run in this batch.</summary>
        public int Start { get; }

        public int Count { get; }

        public TransformedSpriteKind Kind { get; }

        /// <summary>The straight ARGB colour of a palette layer batch.</summary>
        public uint Color { get; }

        /// <summary>The backend's realized sprite arrays (and image, for a standalone glyph).</summary>
        public IDisposable Backend { get; }

        public void Dispose() => Backend.Dispose();
    }

    /// <summary>
    /// A transformed run's glyph masks laid out relative to its snapped origin pixel, for one
    /// quantized linear transform and origin phase. This replaces a run-sized bitmap: the
    /// coverage stays in the shared glyph storage (the glyph mask cache on raster contexts,
    /// the typeface's atlas on GPU contexts), and each draw places the glyph masks directly.
    /// </summary>
    internal sealed class TransformedGlyphSprites : IDisposable
    {
        private readonly TransformedSprite[] _sprites;
        private GlyphAtlasBatch[]? _batches;
        private bool _disposed;

        public TransformedGlyphSprites(in RunMaskKey key, bool simulated, TransformedSprite[] sprites)
        {
            Key = key;
            Simulated = simulated;
            _sprites = sprites;
        }

        public RunMaskKey Key { get; }

        /// <summary>Whether the glyph masks are the typeface's simulated (bold, oblique) outlines.</summary>
        public bool Simulated { get; }

        public ReadOnlySpan<TransformedSprite> Sprites => _sprites;

        public int Count => _sprites.Length;

        /// <summary>The realized atlas batches, in draw order; <c>null</c> until first drawn on a GPU context.</summary>
        public GlyphAtlasBatch[]? Batches => _batches;

        /// <summary>The atlas the batches were built from.</summary>
        public GlyphMaskAtlas? BatchAtlas { get; private set; }

        public bool IsDisposed => _disposed;

        /// <summary>Bytes of the sprite arrays this set holds.</summary>
        public long ByteCost
        {
            get
            {
                var cost = (long)_sprites.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<TransformedSprite>();

                if (_batches is { } batches)
                {
                    // The backend holds a source rectangle and a placement per sprite, four
                    // floats each.
                    foreach (var batch in batches)
                    {
                        cost += batch.Count * 32L;
                    }
                }

                return cost;
            }
        }

        /// <summary>The full glyph mask key of sprite <paramref name="index"/>.</summary>
        public GlyphMaskKey GetGlyphKey(int index)
        {
            ref readonly var sprite = ref _sprites[index];

            return new GlyphMaskKey(sprite.Glyph, Key.ScaleQ, sprite.PhaseX, Key.Mode, GridFit: false, StemSnap: false,
                Transform: Key.Transform, PhaseY: sprite.PhaseY, ApplySimulations: Simulated);
        }

        /// <summary>Whether the batches are drawable from <paramref name="atlas"/>: built from it and none of their pages evicted.</summary>
        public bool HasValidBatches(GlyphMaskAtlas atlas)
        {
            if (_batches is not { } batches || BatchAtlas != atlas)
            {
                return false;
            }

            foreach (var batch in batches)
            {
                if (batch.Page is { IsEvicted: true })
                {
                    return false;
                }
            }

            return true;
        }

        internal void SetBatches(GlyphMaskAtlas atlas, GlyphAtlasBatch[] batches)
        {
            DisposeBatches();

            _batches = batches;
            BatchAtlas = atlas;
        }

        private void DisposeBatches()
        {
            if (_batches is { } batches)
            {
                foreach (var batch in batches)
                {
                    batch.Dispose();
                }
            }

            _batches = null;
            BatchAtlas = null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeBatches();
        }
    }

    /// <summary>
    /// A run's transformed sprite sets: one primary slot for the dominant case plus a short ring
    /// for other transforms or phases, the same shape as <see cref="RunMaskCache"/>. Owned and
    /// disposed by the run.
    /// </summary>
    internal sealed class TransformedRunState : IDisposable
    {
        private const int SecondarySize = 3;

        private TransformedGlyphSprites? _primary;
        private TransformedGlyphSprites?[]? _secondary;
        private int _nextEvict;

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

        public bool TryGet(in RunMaskKey key, out TransformedGlyphSprites sprites)
        {
            if (_primary is { } primary && primary.Key == key)
            {
                sprites = primary;
                return true;
            }

            if (_secondary is { } secondary)
            {
                foreach (var entry in secondary)
                {
                    if (entry is not null && entry.Key == key)
                    {
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
            if (_primary is null)
            {
                _primary = sprites;
                return;
            }

            _secondary ??= new TransformedGlyphSprites?[SecondarySize];

            ref var slot = ref _secondary[_nextEvict];
            slot?.Dispose();
            slot = sprites;
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
                    secondary[i]?.Dispose();
                    secondary[i] = null;
                }
            }
        }
    }
}
