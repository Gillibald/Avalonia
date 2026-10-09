using System.Collections.Generic;

namespace Avalonia.Media.Fonts.Rasterization.TrueType
{
    /// <summary>
    /// The bytecode hinters of one typeface, one per (quantized size, mask mode): the render
    /// class feeds GETINFO, which prep may branch on, so the size state is per mode. Entries
    /// memoise failures as <c>null</c>, so a broken prep costs one attempt. Each kept hinter is
    /// charged to the glyph cache budget.
    /// </summary>
    internal sealed class TrueTypeHinterCache
    {
        private const int MaxHinters = 16;

        // Most recently used first.
        private readonly List<(ushort ScaleQ, GlyphMaskMode Mode, TrueTypeGlyphHinter? Hinter)> _hinters = new();
        private readonly GlyphCachePoolHandle _handle;
        private readonly long _hinterBytes;

        /// <param name="budget">The budget the hinters are charged to.</param>
        /// <param name="hinterBytes">The memory one hinter holds.</param>
        public TrueTypeHinterCache(GlyphCacheBudget budget, long hinterBytes)
        {
            _hinterBytes = hinterBytes;
            _handle = budget.Register(GlyphCachePoolKind.Hinters, this);
        }

        /// <summary>The number of hinters kept, including memoised failures.</summary>
        public int Count
        {
            get
            {
                lock (_hinters)
                {
                    return _hinters.Count;
                }
            }
        }

        /// <summary>
        /// The hinter for <paramref name="scaleQ"/> and <paramref name="mode"/>, created by
        /// <paramref name="typeface"/> on a miss.
        /// </summary>
        public TrueTypeGlyphHinter? Get(ushort scaleQ, GlyphMaskMode mode, GlyphTypeface typeface)
        {
            // A zoom animation asks for a new size nearly every frame, so only the last
            // MaxHinters sizes keep their hinter; text at rest keeps its sizes at the front.
            lock (_hinters)
            {
                for (var i = 0; i < _hinters.Count; i++)
                {
                    var entry = _hinters[i];

                    if (entry.ScaleQ == scaleQ && entry.Mode == mode)
                    {
                        if (i > 0)
                        {
                            _hinters.RemoveAt(i);
                            _hinters.Insert(0, entry);
                        }

                        return entry.Hinter;
                    }
                }

                var hinter = typeface.CreateTrueTypeHinter(scaleQ, mode);

                if (_hinters.Count == MaxHinters)
                {
                    Drop(_hinters.Count - 1);
                }

                _hinters.Insert(0, (scaleQ, mode, hinter));

                if (hinter is not null)
                {
                    _handle.Charge(_hinterBytes);
                }

                return hinter;
            }
        }

        private void Drop(int index)
        {
            if (_hinters[index].Hinter is not null)
            {
                _handle.Credit(_hinterBytes);
            }

            _hinters.RemoveAt(index);
        }
    }
}
