using System.Collections.Generic;
using Avalonia.Media.Fonts.Rasterization;

namespace Avalonia.Media.Fonts.Tables.Bitmaps
{
    /// <summary>
    /// The decoded strike images of one sbix or CBDT table with their placements, keyed by strike
    /// and glyph, so a glyph drawn again composes from its texels instead of decoding its image
    /// again.
    /// </summary>
    /// <remarks>
    /// Each image is charged to the owning typeface's glyph cache budget and records the frame of
    /// its last use, so the budget evicts the images drawn longest ago along with the rest of the
    /// glyph caches. A thread that still holds an evicted image keeps using it; the next request
    /// decodes it again. The cache joins the budget on its first image, because the typeface's
    /// budget can still change after its tables load.
    /// </remarks>
    internal sealed class DecodedBitmapCache : IGlyphCachePool
    {
        private readonly Dictionary<uint, Entry> _images = new();
        private readonly GlyphTypeface _owner;
        private GlyphCacheBudget? _clock;
        private GlyphCachePoolHandle? _handle;
        private bool _released;

        /// <param name="owner">The typeface whose budget the images are charged to.</param>
        public DecodedBitmapCache(GlyphTypeface owner) => _owner = owner;

        /// <summary>The key of a glyph's image in a strike.</summary>
        public static uint Key(int strikeIndex, ushort glyphIndex) => (uint)strikeIndex << 16 | glyphIndex;

        /// <summary>The image kept for <paramref name="key"/>, marked used in the current frame.</summary>
        public bool TryGet(uint key, out BitmapGlyphPlacement image)
        {
            lock (_images)
            {
                if (_images.TryGetValue(key, out var entry))
                {
                    entry.LastUse = _clock!.Frame;
                    image = entry.Image;
                    return true;
                }
            }

            image = default;
            return false;
        }

        /// <summary>
        /// Keeps <paramref name="image"/> for <paramref name="key"/>, unless another thread kept one
        /// first or the cache was released.
        /// </summary>
        public void Add(uint key, in BitmapGlyphPlacement image)
        {
            lock (_images)
            {
                if (_released || _images.ContainsKey(key))
                {
                    return;
                }

                if (_handle is null)
                {
                    _clock = _owner.CacheBudget;
                    _handle = _clock.Register(GlyphCachePoolKind.BitmapImages, this, perTypeface: true);
                }

                _images.Add(key, new Entry(image) { LastUse = _clock!.Frame });
                _handle.Charge(image.Bitmap.Bgra.Length);
            }
        }

        /// <summary>Drops every image and leaves the budget, for a typeface that draws no more.</summary>
        public void Release()
        {
            GlyphCachePoolHandle? handle;

            lock (_images)
            {
                _released = true;
                _images.Clear();
                handle = _handle;
            }

            handle?.Release();
        }

        long IGlyphCachePool.OldestUse
        {
            get
            {
                var oldest = long.MaxValue;

                lock (_images)
                {
                    foreach (var entry in _images.Values)
                    {
                        if (entry.LastUse < oldest)
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

            lock (_images)
            {
                while (freed < bytes)
                {
                    var victim = 0u;
                    var victimUse = long.MaxValue;

                    foreach (var pair in _images)
                    {
                        if (pair.Value.LastUse < usedBefore && pair.Value.LastUse < victimUse)
                        {
                            victim = pair.Key;
                            victimUse = pair.Value.LastUse;
                        }
                    }

                    if (victimUse == long.MaxValue)
                    {
                        break;
                    }

                    var length = _images[victim].Image.Bitmap.Bgra.Length;

                    _images.Remove(victim);
                    _handle!.Credit(length);
                    freed += length;
                }
            }

            return freed;
        }

        private sealed class Entry
        {
            public Entry(BitmapGlyphPlacement image) => Image = image;

            public BitmapGlyphPlacement Image { get; }

            /// <summary>The frame of the image's last use.</summary>
            public long LastUse;
        }
    }
}
