using System;
using System.Collections.Generic;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Groups a run's atlas sprites into batches of one page and one colouring, in draw order.
    /// </summary>
    /// <remarks>
    /// A run whose glyphs live on two pages would otherwise turn into a batch at every page
    /// change. A foreground sprite instead joins the latest batch of its page even when batches
    /// of other pages were begun since, provided it overlaps no sprite of those batches: it is
    /// then drawn ahead of them, but no pixel sees its draws in another order. Overlapping
    /// sprites keep their order because the blend rounds to 8 bits after every draw, so one
    /// colour drawn over itself in two orders can differ by a level.
    /// <para>
    /// Upright sprites are always drawn one texel to one pixel with nearest sampling, so two of
    /// them overlap only where both hold coverage. Transformed sprites may be drawn stretched
    /// with bilinear sampling, which spreads a sprite's coverage into its empty texels, so any
    /// overlap of their rectangles counts.
    /// </para>
    /// <para>
    /// Colour glyph layers and standalone glyph images never move: a later sprite never joins a
    /// batch begun before one of them, so layers stay in their order with everything around
    /// them.
    /// </para>
    /// <para>
    /// The batches of one run still overlap in their bounds, since they share the run's line.
    /// A batch none of whose sprites overlaps a sprite of the run's other batches carries the
    /// run's identity (<see cref="GlyphAtlasBatch.DisjointRun"/>), so a backend collecting
    /// batches by page can tell that it may draw them in either order.
    /// </para>
    /// </remarks>
    internal sealed class GlyphAtlasBatchBuilder
    {
        // Sprites per stretch whose rectangle union is kept, so an overlap test against a batch
        // that spans a long line looks only at the stretches near the sprite.
        private const int StretchLength = 32;

        // Larger sprite arrays are dropped after a build instead of being kept for the next.
        private const int MaxKeptSprites = 4096;

        // Batches of a run tested against each other for the run's identity: every sprite is
        // tested against every batch, so past this many no batch carries the identity.
        private const int MaxDisjointTrackedGroups = 16;

        private readonly List<Group> _groups = new();
        private readonly Stack<Group> _pool = new();
        private readonly List<int> _overlapped = new();
        private int _firstOpen;
        private bool _inkOnly;
        private bool _tracksDisjoint;

        /// <summary>
        /// Starts grouping a run's sprites; <paramref name="upright"/> tells whether they are
        /// drawn one texel to one pixel, which lets sprites move past rectangles they overlap
        /// without sharing coverage.
        /// </summary>
        public void Begin(bool upright)
        {
            Clear();
            _inkOnly = upright;
            _tracksDisjoint = true;
        }

        /// <summary>
        /// Adds the sprite at <paramref name="index"/> of the run, drawn from
        /// <paramref name="page"/>.
        /// </summary>
        public void Add(GlyphAtlasPage page, TransformedSpriteKind kind, uint color, int index,
            in GlyphAtlasSprite sprite)
        {
            var last = _groups.Count - 1;
            var target = -1;

            if (kind == TransformedSpriteKind.Foreground)
            {
                for (var g = last; g >= _firstOpen; g--)
                {
                    if (_groups[g].Matches(page, kind, color))
                    {
                        target = g;
                        break;
                    }
                }
            }
            else if (last >= _firstOpen && _groups[last].Matches(page, kind, color))
            {
                target = last;
            }

            _overlapped.Clear();

            if (_tracksDisjoint)
            {
                var blocked = false;

                for (var g = 0; g <= last; g++)
                {
                    if (g != target && Overlaps(_groups[g], page, sprite))
                    {
                        _overlapped.Add(g);
                        blocked |= target >= 0 && g > target;
                    }
                }

                if (blocked)
                {
                    if (Overlaps(_groups[target], page, sprite))
                    {
                        _overlapped.Add(target);
                    }

                    target = -1;
                }
            }
            else if (target >= 0 && OverlapsAny(target + 1, last, page, sprite))
            {
                // Joining an earlier batch draws the sprite ahead of every batch begun after it.
                target = -1;
            }

            if (target < 0)
            {
                // A layer begins a batch no later sprite may move ahead of.
                if (kind != TransformedSpriteKind.Foreground)
                {
                    _firstOpen = _groups.Count;
                }

                var group = Rent();

                group.Page = page;
                group.Kind = kind;
                group.Color = color;
                group.Start = index;
                _groups.Add(group);
                target = _groups.Count - 1;

                if (_groups.Count > MaxDisjointTrackedGroups)
                {
                    _tracksDisjoint = false;
                }
            }

            _groups[target].Append(sprite);

            foreach (var g in _overlapped)
            {
                _groups[g].OverlapsOthers = true;
                _groups[target].OverlapsOthers = true;
            }
        }

        /// <summary>
        /// Adds a batch drawing one sprite from its own image; no later sprite moves ahead of it.
        /// </summary>
        public void AddStandalone(GlyphAtlasBatch batch)
        {
            var group = Rent();

            group.Standalone = batch;
            _groups.Add(group);
            _firstOpen = _groups.Count;

            if (_groups.Count > MaxDisjointTrackedGroups)
            {
                _tracksDisjoint = false;
            }
        }

        /// <summary>
        /// Realizes the batches through <paramref name="context"/>, in draw order, and starts
        /// over. On failure the batches made so far are disposed.
        /// </summary>
        public GlyphAtlasBatch[] Build(ITransformedGlyphContext context)
        {
            var batches = new GlyphAtlasBatch[_groups.Count];
            var built = 0;
            var pageGroups = 0;

            foreach (var group in _groups)
            {
                pageGroups += group.Standalone is null ? 1 : 0;
            }

            // A run of one batch on a page has no part to tell apart.
            var run = _tracksDisjoint && pageGroups > 1 ? batches : null;

            try
            {
                for (; built < batches.Length; built++)
                {
                    var group = _groups[built];

                    batches[built] = group.Standalone ?? new GlyphAtlasBatch(group.Page, group.Start, group.Count,
                        group.Kind, group.Color, context.CreateAtlasBatch(group.Sprites.AsSpan(0, group.Count), null))
                    {
                        DisjointRun = group.OverlapsOthers ? null : run,
                    };
                    group.Standalone = null;
                }
            }
            catch
            {
                for (var i = 0; i < built; i++)
                {
                    batches[i].Dispose();
                }

                throw;
            }

            Clear();

            return batches;
        }

        /// <summary>Disposes the standalone batches added since <see cref="Begin"/> and starts over.</summary>
        public void Abandon()
        {
            foreach (var group in _groups)
            {
                group.Standalone?.Dispose();
            }

            Clear();
        }

        private void Clear()
        {
            foreach (var group in _groups)
            {
                group.Reset();
                _pool.Push(group);
            }

            _groups.Clear();
            _firstOpen = 0;
        }

        private Group Rent() => _pool.Count > 0 ? _pool.Pop() : new Group();

        private bool OverlapsAny(int first, int last, GlyphAtlasPage page, in GlyphAtlasSprite sprite)
        {
            for (var g = first; g <= last; g++)
            {
                if (Overlaps(_groups[g], page, sprite))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Whether <paramref name="sprite"/>, drawn from <paramref name="page"/>, overlaps a sprite of <paramref name="group"/>.</summary>
        private bool Overlaps(Group group, GlyphAtlasPage page, in GlyphAtlasSprite sprite)
        {
            if (group.Standalone is not null || !Intersects(group.Bounds, sprite))
            {
                return false;
            }

            for (var s = 0; s * StretchLength < group.Count; s++)
            {
                if (!Intersects(group.Stretches[s], sprite))
                {
                    continue;
                }

                var end = Math.Min(group.Count, (s + 1) * StretchLength);

                for (var i = s * StretchLength; i < end; i++)
                {
                    ref readonly var other = ref group.Sprites[i];

                    if (Intersects(Bounds(other), sprite) &&
                        (!_inkOnly || SharesCoverage(group.Page!, other, page, sprite)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>Whether two sprites both hold coverage at a pixel they cover.</summary>
        private static bool SharesCoverage(GlyphAtlasPage firstPage, in GlyphAtlasSprite first,
            GlyphAtlasPage secondPage, in GlyphAtlasSprite second)
        {
            var left = Math.Max(first.X, second.X);
            var right = Math.Min(first.X + first.Width, second.X + second.Width);
            var top = Math.Max(first.Y, second.Y);
            var bottom = Math.Min(first.Y + first.Height, second.Y + second.Height);

            // Entries are never overwritten, and a grown page copies them, so the current arrays
            // hold both sprites' coverage.
            var firstPixels = firstPage.Pixels;
            var secondPixels = secondPage.Pixels;

            for (var y = top; y < bottom; y++)
            {
                var firstRow = (first.SourceY + y - first.Y) * GlyphMaskAtlas.PageWidth + first.SourceX - first.X;
                var secondRow = (second.SourceY + y - second.Y) * GlyphMaskAtlas.PageWidth + second.SourceX - second.X;

                for (var x = left; x < right; x++)
                {
                    if (firstPixels[firstRow + x] != 0 && secondPixels[secondRow + x] != 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static Box Bounds(in GlyphAtlasSprite sprite)
            => new(sprite.X, sprite.Y, sprite.X + sprite.Width, sprite.Y + sprite.Height);

        /// <summary>Whether the box and the sprite's rectangle overlap, edges excluded.</summary>
        private static bool Intersects(in Box box, in GlyphAtlasSprite sprite)
            => box.Left < sprite.X + sprite.Width && sprite.X < box.Right &&
               box.Top < sprite.Y + sprite.Height && sprite.Y < box.Bottom;

        private readonly record struct Box(int Left, int Top, int Right, int Bottom)
        {
            public Box Union(in Box other) => new(Math.Min(Left, other.Left), Math.Min(Top, other.Top),
                Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
        }

        /// <summary>The sprites of one batch in the making, with their bounds per stretch.</summary>
        private sealed class Group
        {
            public GlyphAtlasPage? Page;
            public TransformedSpriteKind Kind;
            public uint Color;
            public int Start;
            public GlyphAtlasBatch? Standalone;

            public GlyphAtlasSprite[] Sprites = new GlyphAtlasSprite[16];
            public int Count;
            public Box[] Stretches = new Box[4];
            public Box Bounds;

            /// <summary>Whether a sprite overlaps a sprite of another batch of the run.</summary>
            public bool OverlapsOthers;

            public bool Matches(GlyphAtlasPage page, TransformedSpriteKind kind, uint color)
                => Standalone is null && Page == page && Kind == kind && Color == color;

            public void Append(in GlyphAtlasSprite sprite)
            {
                if (Count == Sprites.Length)
                {
                    Array.Resize(ref Sprites, Count * 2);
                }

                var box = GlyphAtlasBatchBuilder.Bounds(sprite);
                var stretch = Count / StretchLength;

                if (Count % StretchLength == 0)
                {
                    if (stretch == Stretches.Length)
                    {
                        Array.Resize(ref Stretches, stretch * 2);
                    }

                    Stretches[stretch] = box;
                }
                else
                {
                    Stretches[stretch] = Stretches[stretch].Union(box);
                }

                Bounds = Count == 0 ? box : Bounds.Union(box);
                Sprites[Count++] = sprite;
            }

            public void Reset()
            {
                // The pooled groups must not keep pages or batches alive.
                Page = null;
                Standalone = null;
                Count = 0;
                OverlapsOthers = false;

                if (Sprites.Length > MaxKeptSprites)
                {
                    Sprites = new GlyphAtlasSprite[16];
                    Stretches = new Box[4];
                }
            }
        }
    }
}
