using System;
using System.Collections.Generic;
using Avalonia.Platform;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// The backend images of one typeface's colour masks, for transformed draws that cannot write
    /// the surface directly (under an opacity, a layer or a turned clip): each colour mask, keyed
    /// as in the colour mask cache, is composed once into a premultiplied image through the render
    /// interface, and every run that shows the glyph under the same transform draws that image.
    /// Colour masks carry their own colours, so the images do not depend on the text colour.
    /// </summary>
    /// <remarks>
    /// The images are charged to the glyph cache budget and evicted oldest first, like atlas
    /// pages only where a frame begins, so a frame never loses an image it draws. A draw looks its
    /// images up every time rather than holding them, so an evicted image is never drawn.
    /// </remarks>
    internal sealed class ColorFallbackImageCache : IGlyphCachePool
    {
        // The image object, its handle and the dictionary entry, beside the pixels.
        private const int EntryOverheadBytes = 160;

        private readonly object _lock = new();
        private readonly Dictionary<ColorGlyphMaskKey, Entry> _entries = new();
        private readonly GlyphCacheBudget _budget;
        private readonly GlyphCachePoolHandle _handle;

        public ColorFallbackImageCache(GlyphCacheBudget budget)
        {
            _budget = budget;
            _handle = budget.Register(GlyphCachePoolKind.ColorFallbackImages, this, perTypeface: true);
        }

        /// <summary>The images held.</summary>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _entries.Count;
                }
            }
        }

        /// <summary>
        /// The image of the colour mask <paramref name="mask"/> stored under <paramref name="key"/>,
        /// made on a miss; <c>null</c> for an empty mask or without a render interface.
        /// </summary>
        public IBitmapImpl? GetOrCreate(in ColorGlyphMaskKey key, GlyphMask mask)
        {
            var frame = _budget.Frame;

            lock (_lock)
            {
                if (_entries.TryGetValue(key, out var entry))
                {
                    entry.LastUse = frame;
                    return entry.Image;
                }
            }

            if (mask.IsEmpty || Create(mask) is not { } image)
            {
                return null;
            }

            var bytes = (long)mask.Width * mask.Height * 4 + EntryOverheadBytes;

            lock (_lock)
            {
                if (_entries.TryGetValue(key, out var raced))
                {
                    raced.LastUse = frame;
                    image.Dispose();
                    return raced.Image;
                }

                _entries.Add(key, new Entry(image, bytes) { LastUse = frame });
            }

            _handle.Charge(bytes);

            return image;
        }

        private static unsafe IBitmapImpl? Create(GlyphMask mask)
        {
            if (AvaloniaLocator.Current.GetService<IPlatformRenderInterface>() is not { } renderInterface)
            {
                return null;
            }

            if (TextRasterCounters.TryGetCurrent() is { } counters)
            {
                counters.ColorFallbackImages++;
            }

            var bitmap = renderInterface.CreateWriteableBitmap(new PixelSize(mask.Width, mask.Height),
                new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

            using (var framebuffer = bitmap.Lock())
            {
                var span = new Span<byte>((void*)framebuffer.Address, framebuffer.RowBytes * mask.Height);

                span.Clear();
                RunMaskComposer.ComposeColor(mask, -mask.Left, -mask.Top, span, mask.Width, mask.Height,
                    framebuffer.RowBytes);
            }

            return bitmap;
        }

        long IGlyphCachePool.OldestUse
        {
            get
            {
                var oldest = long.MaxValue;

                lock (_lock)
                {
                    foreach (var entry in _entries.Values)
                    {
                        oldest = Math.Min(oldest, entry.LastUse);
                    }
                }

                return oldest;
            }
        }

        long IGlyphCachePool.EvictOldest(long usedBefore, long bytes)
        {
            long freed = 0;
            List<KeyValuePair<ColorGlyphMaskKey, Entry>>? victims = null;

            lock (_lock)
            {
                foreach (var pair in _entries)
                {
                    if (pair.Value.LastUse < usedBefore)
                    {
                        (victims ??= new List<KeyValuePair<ColorGlyphMaskKey, Entry>>()).Add(pair);
                    }
                }

                if (victims is null)
                {
                    return 0;
                }

                victims.Sort(static (a, b) => a.Value.LastUse.CompareTo(b.Value.LastUse));

                foreach (var (key, entry) in victims)
                {
                    if (freed >= bytes)
                    {
                        break;
                    }

                    _entries.Remove(key);
                    entry.Image.Dispose();
                    freed += entry.Bytes;
                }
            }

            _handle.Credit(freed);

            return freed;
        }

        /// <summary>Disposes every image and leaves the budget, for a typeface that draws no more.</summary>
        public void Release()
        {
            lock (_lock)
            {
                foreach (var entry in _entries.Values)
                {
                    entry.Image.Dispose();
                }

                _entries.Clear();
            }

            _handle.Release();
        }

        private sealed class Entry
        {
            public Entry(IBitmapImpl image, long bytes)
            {
                Image = image;
                Bytes = bytes;
            }

            public IBitmapImpl Image { get; }

            public long Bytes { get; }

            public long LastUse;
        }
    }
}
