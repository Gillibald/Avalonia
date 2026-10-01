using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Avalonia.Media.Fonts.Rasterization
{
    internal static partial class MaskGlyphRunRenderer
    {
        [ThreadStatic]
        private static TransientGlyphScratch? t_transient;

        /// <summary>
        /// Draws a frame of an animating run from glyph masks rasterized for that frame alone:
        /// the masks a static frame at the same transform draws, so the frame is as sharp as a
        /// static one and the first static frame after the animation matches it. The masks live
        /// in pooled per-thread buffers and nothing enters the glyph mask cache, the atlas or
        /// the run's sprite sets, so an animation cannot evict the masks of static text. The
        /// runs of a frame share its transform, so a mask rasterized for one run serves the
        /// others until a draw arrives under another transform or typeface. A
        /// raster context blends them straight into its surface; a GPU context, or a raster
        /// draw the surface cannot take directly, packs them into one transient image per run
        /// and draws it in one call. Returns <c>false</c> when a glyph mask would exceed
        /// <see cref="GlyphMasks.MaxMaskSize"/>.
        /// </summary>
        private static bool TryDrawTransient(ITransformedGlyphContext context, ManagedGlyphRunImpl run,
            in RunMaskKey key, in Matrix transform, int originX, int originY, byte alpha, Color color)
        {
            var items = ArrayPool<TransformedGlyphItem>.Shared.Rent(Math.Max(1, run.GlyphCount));
            var count = 0;
            var scratch = t_transient ??= new TransientGlyphScratch();

            try
            {
                if (!TryCollectTransformedItems(run, key, transform, ref items, ref count))
                {
                    return false;
                }

                var path = t_scratch ??= new GlyphPathBuilder();
                var glyphs = items.AsSpan(0, count);

                scratch.BeginDraw(run.GlyphTypeface, key);

                if (context.RasterTarget == GlyphRasterTarget.Raster && context.TryGetBlitTarget(out var target))
                {
                    BlendTransient(target, run.GlyphTypeface, path, scratch, glyphs, originX, originY,
                        RunMaskComposer.MakeTint(alpha, color.R, color.G, color.B));
                }
                else
                {
                    DrawTransientImages(context, run.GlyphTypeface, path, scratch, glyphs, originX, originY,
                        ToArgb(alpha, color));
                }

                return true;
            }
            finally
            {
                ArrayPool<TransformedGlyphItem>.Shared.Return(items);
                scratch.EndDraw();
            }
        }

        /// <summary>
        /// The arena offset of a glyph mask's raw coverage, rasterized into the arena unless an
        /// earlier run of the frame already did. Returns <c>false</c> for a glyph without an
        /// outline.
        /// </summary>
        private static bool TryGetCoverage(GlyphTypeface typeface, GlyphPathBuilder path, TransientGlyphScratch scratch,
            in TransformedGlyphItem item, out int offset)
        {
            if (!scratch.ArenaSlots.TryGetValue(item.Key, out offset))
            {
                var size = item.Width * item.Height;

                offset = scratch.AllocateArena(size);

                if (!GlyphMasks.RasterizeTransformed(typeface, path, item.Key, item.Left, item.Top, item.Width,
                        item.Height, scratch.Arena.AsSpan(offset, size), item.Width))
                {
                    offset = -1;
                }

                scratch.ArenaSlots.Add(item.Key, offset);
            }

            return offset >= 0;
        }

        /// <summary>
        /// Blends each glyph mask of the run from the scratch arena into the surface with the
        /// arithmetic of the static raster path.
        /// </summary>
        private static void BlendTransient(in GlyphBlitTarget target, GlyphTypeface typeface, GlyphPathBuilder path,
            TransientGlyphScratch scratch, ReadOnlySpan<TransformedGlyphItem> glyphs, int originX, int originY,
            uint tint)
        {
            var table = MaskGamma.GetTableForPremulBgra(tint);

            for (var i = 0; i < glyphs.Length; i++)
            {
                ref readonly var item = ref glyphs[i];

                if (!TryGetCoverage(typeface, path, scratch, item, out var offset))
                {
                    continue;
                }

                var coverage = scratch.Arena.AsSpan(offset, item.Width * item.Height);
                var x = originX + item.PenX + item.Left;
                var y = originY + item.PenY + item.Top;

                switch (item.Kind)
                {
                    case TransformedSpriteKind.Foreground:
                        GlyphMaskBlitter.Blend(target, coverage, item.Width, item.Height, x, y, tint, table);
                        break;
                    case TransformedSpriteKind.ForegroundLayer:
                        GlyphMaskBlitter.Blend(target, coverage, item.Width, item.Height, x, y, tint, null);
                        break;
                    default:
                        GlyphMaskBlitter.Blend(target, coverage, item.Width, item.Height, x, y,
                            ToPremulTint(item.Color), null);
                        break;
                }
            }
        }

        /// <summary>
        /// Packs each distinct glyph mask of the run once from the arena into the scratch page, corrected like
        /// an atlas entry (foreground glyphs for the foreground's luminance bucket, colour layers
        /// not at all), and draws the page's sprites from one transient image in run order,
        /// one call per run of sprites sharing a tint. A page that fills up is drawn and reused;
        /// a glyph too large for a page draws from an image of its own.
        /// </summary>
        private static void DrawTransientImages(ITransformedGlyphContext context, GlyphTypeface typeface,
            GlyphPathBuilder path, TransientGlyphScratch scratch, ReadOnlySpan<TransformedGlyphItem> glyphs,
            int originX, int originY, uint foregroundArgb)
        {
            var bucket = GetBucket(foregroundArgb);
            var placement = Matrix.CreateTranslation(originX, originY);

            for (var i = 0; i < glyphs.Length; i++)
            {
                ref readonly var item = ref glyphs[i];
                var corrected = item.Kind == TransformedSpriteKind.Foreground;
                var tint = item.Kind == TransformedSpriteKind.PaletteLayer ? item.Color : foregroundArgb;
                var x = item.PenX + item.Left;
                var y = item.PenY + item.Top;

                if (!GlyphMaskAtlas.Fits(item.Width, item.Height))
                {
                    FlushTransientPage(context, scratch, placement);
                    DrawTransientStandalone(context, typeface, path, item, corrected ? bucket : GlyphMaskAtlas.Uncorrected,
                        placement, tint);
                    continue;
                }

                if (!scratch.PageSlots.TryGetValue((item.Key, corrected), out var slot))
                {
                    if (!scratch.TryPlace(item.Width, item.Height, out var slotX, out var slotY))
                    {
                        FlushTransientPage(context, scratch, placement);
                        scratch.TryPlace(item.Width, item.Height, out slotX, out slotY);
                    }

                    if (TryGetCoverage(typeface, path, scratch, item, out var offset))
                    {
                        for (var row = 0; row < item.Height; row++)
                        {
                            var source = scratch.Arena.AsSpan(offset + row * item.Width, item.Width);
                            var target = scratch.Page.AsSpan((slotY + row) * TransientGlyphScratch.PageWidth + slotX,
                                item.Width);

                            if (corrected)
                            {
                                GlyphMaskAtlas.Correct(source, target, bucket);
                            }
                            else
                            {
                                source.CopyTo(target);
                            }
                        }

                        slot = (slotX, slotY);
                    }
                    else
                    {
                        slot = (-1, -1);
                    }

                    scratch.PageSlots.Add((item.Key, corrected), slot);
                }

                if (slot.X < 0)
                {
                    continue;
                }

                scratch.AddSprite(new GlyphAtlasSprite(slot.X, slot.Y, item.Width, item.Height, x, y), tint);
            }

            FlushTransientPage(context, scratch, placement);
        }

        private static void FlushTransientPage(ITransformedGlyphContext context, TransientGlyphScratch scratch,
            in Matrix placement)
        {
            if (scratch.SpriteCount == 0)
            {
                scratch.ResetPage();
                return;
            }

            var rows = scratch.ImageRows;
            var columns = scratch.ImageColumns;

            using (var image = context.CreateTransientImage(
                       scratch.Page.AsSpan(0, (rows - 1) * TransientGlyphScratch.PageWidth + columns), columns, rows,
                       TransientGlyphScratch.PageWidth))
            {
                var sprites = scratch.Sprites.AsSpan(0, scratch.SpriteCount);
                var tints = scratch.Tints;
                var start = 0;

                for (var i = 1; i <= sprites.Length; i++)
                {
                    if (i == sprites.Length || tints[i] != tints[start])
                    {
                        context.DrawTransientSprites(image, sprites.Slice(start, i - start), placement, tints[start]);
                        start = i;
                    }
                }
            }

            scratch.ResetPage();
        }

        private static void DrawTransientStandalone(ITransformedGlyphContext context, GlyphTypeface typeface,
            GlyphPathBuilder path, in TransformedGlyphItem item, int bucket, in Matrix placement, uint tint)
        {
            var size = item.Width * item.Height;
            var buffer = ArrayPool<byte>.Shared.Rent(size);

            try
            {
                var coverage = buffer.AsSpan(0, size);

                if (!GlyphMasks.RasterizeTransformed(typeface, path, item.Key, item.Left, item.Top, item.Width,
                        item.Height, coverage, item.Width))
                {
                    return;
                }

                if (bucket != GlyphMaskAtlas.Uncorrected)
                {
                    GlyphMaskAtlas.Correct(coverage, coverage, bucket);
                }

                var sprite = new GlyphAtlasSprite(0, 0, item.Width, item.Height, item.PenX + item.Left,
                    item.PenY + item.Top);

                using var image = context.CreateTransientImage(coverage, item.Width, item.Height, item.Width);

                context.DrawTransientSprites(image, MemoryMarshal.CreateReadOnlySpan(ref sprite, 1), placement, tint);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// The per-thread buffers of transient frames: an arena of the raw glyph masks of the
        /// runs drawn under one transform, and a page of one run's corrected glyph masks with
        /// their sprites for transient images. Both are reused frame to frame, so a steady
        /// animation allocates nothing once they have grown to its frames.
        /// </summary>
        private sealed class TransientGlyphScratch
        {
            public const int PageWidth = GlyphMaskAtlas.PageWidth;

            // Images are handed out in whole multiples of this many rows and columns, so a GPU
            // texture made for one frame's image fits the next frame's image of a similar size
            // and the backend can reuse it instead of allocating another.
            private const int SizeGranularity = 64;

            // An arena filled past this by huge glyphs, or by a long frame, is dropped after the
            // draw rather than held by the thread.
            private const int MaxRetainedArenaBytes = 4 * 1024 * 1024;

            private readonly WeakReference<GlyphTypeface?> _typeface = new(null);
            private ushort _scaleQ;
            private GlyphMaskTransform _transform;
            private int _arenaUsed;
            private int _shelfX;
            private int _shelfY;
            private int _shelfHeight;
            private int _usedHeight;
            private int _usedWidth;

            public readonly Dictionary<GlyphMaskKey, int> ArenaSlots = new();

            public readonly Dictionary<(GlyphMaskKey Key, bool Corrected), (int X, int Y)> PageSlots = new();

            public byte[] Arena { get; private set; } = Array.Empty<byte>();

            public byte[] Page { get; private set; } = new byte[PageWidth * SizeGranularity * 4];

            public GlyphAtlasSprite[] Sprites { get; private set; } = new GlyphAtlasSprite[64];

            public uint[] Tints { get; private set; } = new uint[64];

            public int SpriteCount { get; private set; }

            /// <summary>The columns of the page an image of its sprites needs.</summary>
            public int ImageColumns
                => Math.Min(PageWidth, (_usedWidth + SizeGranularity - 1) / SizeGranularity * SizeGranularity);

            /// <summary>The rows of the page an image of its sprites needs.</summary>
            public int ImageRows
                => Math.Min(GlyphMaskAtlas.MaxPageHeight, (_usedHeight + SizeGranularity - 1) / SizeGranularity * SizeGranularity);

            public int AllocateArena(int size)
            {
                if (_arenaUsed + size > Arena.Length)
                {
                    var grown = new byte[Math.Max(Arena.Length * 2, _arenaUsed + size)];

                    Arena.AsSpan(0, _arenaUsed).CopyTo(grown);
                    Arena = grown;
                }

                var offset = _arenaUsed;

                _arenaUsed += size;

                return offset;
            }

            /// <summary>
            /// Places a glyph mask on the page's current shelf or a new one below it, with a
            /// gutter of one pixel right of and below every mask. Returns <c>false</c> when the
            /// page is full.
            /// </summary>
            public bool TryPlace(int width, int height, out int x, out int y)
            {
                if (_shelfX + width + 1 > PageWidth)
                {
                    _shelfY += _shelfHeight + 1;
                    _shelfX = 0;
                    _shelfHeight = 0;
                }

                if (_shelfY + height + 1 > GlyphMaskAtlas.MaxPageHeight)
                {
                    x = y = 0;
                    return false;
                }

                x = _shelfX;
                y = _shelfY;
                _shelfX += width + 1;
                _shelfHeight = Math.Max(_shelfHeight, height);
                _usedHeight = Math.Max(_usedHeight, y + height);
                _usedWidth = Math.Max(_usedWidth, x + width);

                var rows = Math.Min(GlyphMaskAtlas.MaxPageHeight,
                    (_usedHeight + SizeGranularity - 1) / SizeGranularity * SizeGranularity);

                if (rows * PageWidth > Page.Length)
                {
                    var grown = new byte[Math.Min(GlyphMaskAtlas.MaxPageHeight, Math.Max(rows, Page.Length / PageWidth * 2)) *
                                         PageWidth];

                    Page.AsSpan(0, _usedHeight * PageWidth).CopyTo(grown);
                    Page = grown;
                }

                return true;
            }

            public void AddSprite(in GlyphAtlasSprite sprite, uint tint)
            {
                if (SpriteCount == Sprites.Length)
                {
                    var sprites = new GlyphAtlasSprite[Sprites.Length * 2];
                    var tints = new uint[Tints.Length * 2];

                    Sprites.AsSpan().CopyTo(sprites);
                    Tints.AsSpan().CopyTo(tints);
                    Sprites = sprites;
                    Tints = tints;
                }

                Sprites[SpriteCount] = sprite;
                Tints[SpriteCount] = tint;
                SpriteCount++;
            }

            /// <summary>Empties the page: its written cells return to zero, its gutters stay zero.</summary>
            public void ResetPage()
            {
                for (var row = 0; row < _usedHeight; row++)
                {
                    Page.AsSpan(row * PageWidth, _usedWidth).Clear();
                }

                PageSlots.Clear();
                SpriteCount = 0;
                _shelfX = _shelfY = _shelfHeight = _usedHeight = _usedWidth = 0;
            }

            /// <summary>
            /// Keeps the arena's masks for a draw of the same typeface under the same scale and
            /// transform as the previous one, the next run of the same frame, and empties it
            /// otherwise. The arena's keys hold everything else that tells masks apart.
            /// </summary>
            public void BeginDraw(GlyphTypeface typeface, in RunMaskKey key)
            {
                if (_typeface.TryGetTarget(out var previous) && ReferenceEquals(previous, typeface) &&
                    _scaleQ == key.ScaleQ && _transform == key.Transform)
                {
                    return;
                }

                _typeface.SetTarget(typeface);
                _scaleQ = key.ScaleQ;
                _transform = key.Transform;
                ClearArena();
            }

            public void EndDraw()
            {
                ResetPage();

                if (_arenaUsed > MaxRetainedArenaBytes)
                {
                    ClearArena();
                    Arena = Array.Empty<byte>();
                }
            }

            private void ClearArena()
            {
                ArenaSlots.Clear();
                _arenaUsed = 0;
            }
        }
    }
}
