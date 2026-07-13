using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization
{
    public class GlyphMaskCacheTests
    {
        private static GlyphMask MakeMask(int side = 10)
            => new(new byte[side * side], side, side, 0, 0);

        private static GlyphMaskKey Key(ushort glyph, float ppem = 16f, byte phase = 0)
            => new(glyph, GlyphMaskKey.QuantizeScale(ppem), phase, GlyphMaskMode.Antialiased);

        [Fact]
        public void Scale_Quantizes_To_Eighth_Pixel_Buckets()
        {
            Assert.Equal(GlyphMaskKey.QuantizeScale(12.03f), GlyphMaskKey.QuantizeScale(12.06f));
            Assert.NotEqual(GlyphMaskKey.QuantizeScale(12f), GlyphMaskKey.QuantizeScale(12.2f));
            Assert.Equal(1, GlyphMaskKey.QuantizeScale(0f));
        }

        [Fact]
        public void Pen_Snaps_To_The_Nearest_Quarter_Pixel()
        {
            GlyphMaskKey.SnapPen(5.95f, out var pixel, out var phase);
            Assert.Equal(6, pixel);
            Assert.Equal(0, phase);

            GlyphMaskKey.SnapPen(5.13f, out pixel, out phase);
            Assert.Equal(5, pixel);
            Assert.Equal(1, phase);

            GlyphMaskKey.SnapPen(-0.9f, out pixel, out phase);
            Assert.Equal(-1, pixel);
            Assert.Equal(0, phase);
        }

        [Fact]
        public void Key_Is_Value_Equatable_Without_Boxing()
        {
            // Record structs implement IEquatable<T>, which is what keeps ConcurrentDictionary
            // lookups boxing-free on the hot path.
            Assert.IsAssignableFrom<IEquatable<GlyphMaskKey>>(Key(1));

            Assert.Equal(Key(1, 16f, 2), Key(1, 16f, 2));
            Assert.Equal(Key(1, 16f, 2).GetHashCode(), Key(1, 16f, 2).GetHashCode());

            Assert.NotEqual(Key(1), Key(2));
            Assert.NotEqual(Key(1, 16f), Key(1, 17f));
            Assert.NotEqual(Key(1, 16f, 0), Key(1, 16f, 1));
            Assert.NotEqual(
                new GlyphMaskKey(1, 128, 0, GlyphMaskMode.Antialiased),
                new GlyphMaskKey(1, 128, 0, GlyphMaskMode.Aliased));
        }

        [Fact]
        public void A_Hit_Returns_The_Same_Instance_Without_Rebuilding()
        {
            var cache = new GlyphMaskCache();
            var builds = 0;

            GlyphMask Build(GlyphMaskKey key)
            {
                builds++;
                return MakeMask();
            }

            var first = cache.GetOrBuild(Key(1), Build);
            var second = cache.GetOrBuild(Key(1), Build);

            Assert.Same(first, second);
            Assert.Equal(1, builds);
        }

        [Fact]
        public void An_Empty_Mask_Is_Memoised_And_Never_Rebuilt()
        {
            var cache = new GlyphMaskCache();
            var builds = 0;

            GlyphMask Build(GlyphMaskKey key)
            {
                builds++;
                return GlyphMask.Empty;
            }

            Assert.Same(GlyphMask.Empty, cache.GetOrBuild(Key(7), Build));
            Assert.Same(GlyphMask.Empty, cache.GetOrBuild(Key(7), Build));
            Assert.Equal(1, builds);
            Assert.True(cache.TryGet(Key(7), out var cached));
            Assert.Same(GlyphMask.Empty, cached);
        }

        [Fact]
        public async Task Racing_Builders_Publish_One_Winner_And_Both_Callers_Receive_It()
        {
            var cache = new GlyphMaskCache();
            using var barrier = new Barrier(2);
            var builds = 0;

            GlyphMask Build(GlyphMaskKey key)
            {
                // Hold both threads inside the build so both construct a candidate; exactly one
                // wins at insertion and the loser's result is discarded (D9).
                barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                Interlocked.Increment(ref builds);
                return MakeMask();
            }

            var key = Key(3);
            var tasks = new[]
            {
                Task.Run(() => cache.GetOrBuild(key, Build)),
                Task.Run(() => cache.GetOrBuild(key, Build)),
            };

            var results = await Task.WhenAll(tasks);

            Assert.Equal(2, builds);
            Assert.Same(results[0], results[1]);
            Assert.Equal(1, cache.Count);
        }

        [Fact]
        public void Eviction_Keeps_Total_Cost_Under_The_Budget()
        {
            var cost = MakeMask().ByteCost;
            var cache = new GlyphMaskCache(budgetBytes: cost * 3);

            for (ushort glyph = 1; glyph <= 6; glyph++)
            {
                cache.GetOrBuild(Key(glyph), _ => MakeMask());
            }

            Assert.True(cache.TotalCost <= cost * 3, $"TotalCost {cache.TotalCost} exceeds budget {cost * 3}");
            Assert.True(cache.Count <= 3);
        }

        [Fact]
        public void A_Touched_Entry_Survives_Eviction_Pressure()
        {
            // CLOCK gives second chances by comparing referenced bits, so the touch only helps
            // once a sweep has cleared some bits: when everything is freshly referenced, the
            // sweep clears the whole ring and correctly degrades to FIFO from the hand. Script
            // that state instead of racing it.
            var cost = MakeMask().ByteCost;
            var cache = new GlyphMaskCache(budgetBytes: cost * 3);

            for (ushort glyph = 1; glyph <= 3; glyph++)
            {
                cache.GetOrBuild(Key(glyph), _ => MakeMask());
            }

            // Over budget: the sweep clears 1..3, gives new 4 its chance, and evicts 1 (FIFO
            // among the uniformly-referenced) — leaving 2 and 3 with cleared bits.
            cache.GetOrBuild(Key(4), _ => MakeMask());
            Assert.False(cache.TryGet(Key(1), out _));

            // Touch 2: its bit is now set while 3's stays clear.
            cache.GetOrBuild(Key(2), _ => throw new InvalidOperationException("must be a hit"));

            // Next eviction passes the touched 2 (second chance) and reclaims the cold 3.
            cache.GetOrBuild(Key(5), _ => MakeMask());

            Assert.True(cache.TryGet(Key(2), out _), "the touched entry was evicted");
            Assert.False(cache.TryGet(Key(3), out _), "the cold entry survived");
        }

        [Fact]
        public void Construction_Cost_Does_Not_Scale_With_Glyph_Count()
        {
            // The cache has no glyph-count parameter at all — nothing to preallocate per font.
            // Guard the constructor's absolute footprint so an O(GlyphCount) table can never
            // sneak in (the old outline-cache lesson: 512 KB of pointer array per CJK font).
            var before = GC.GetAllocatedBytesForCurrentThread();
            var cache = new GlyphMaskCache();
            var after = GC.GetAllocatedBytesForCurrentThread();

            Assert.True(cache.Count == 0);
            Assert.True(after - before < 4096, $"cache construction allocated {after - before} bytes");
        }

        [Fact]
        public void Evicting_A_Mask_Does_Not_Invalidate_A_Composed_Buffer()
        {
            var typeface = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular).TryCreateGlyphTypeface();
            Assert.NotNull(typeface);

            var glyph = typeface!.CharacterToGlyphMap['g'];
            var key = new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale(32f), 0, GlyphMaskMode.Antialiased);
            var scratch = new GlyphPathBuilder();

            var mask = GlyphMasks.Build(typeface, scratch, key);
            Assert.False(mask.IsEmpty);

            // A cache sized to exactly one mask: inserting a second evicts the first.
            var cache = new GlyphMaskCache(budgetBytes: mask.ByteCost);
            var published = cache.GetOrBuild(key, _ => mask);
            Assert.Same(mask, published);

            var width = mask.Width + 8;
            var height = mask.Height + 8;
            var composed = new byte[width * height];
            RunMaskComposer.ComposeAlpha(mask, -mask.Left + 4, -mask.Top + 4, composed, width, height);
            var snapshot = composed.AsSpan().ToArray();

            var otherKey = key with { Glyph = (ushort)(glyph + 1) };
            cache.GetOrBuild(otherKey, _ => GlyphMasks.Build(typeface, scratch, otherKey));
            Assert.False(cache.TryGet(key, out _));

            // The composed pixels are an independent copy (D7's one deliberate copy), and a
            // rebuild after eviction reproduces the evicted mask bit for bit (determinism).
            Assert.True(composed.AsSpan().SequenceEqual(snapshot));

            var rebuilt = GlyphMasks.Build(typeface, scratch, key);
            Assert.True(rebuilt.Alpha.AsSpan().SequenceEqual(mask.Alpha));
        }

        [Fact]
        public void Built_Mask_Placement_Matches_The_Ink_Bounds()
        {
            var typeface = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular).TryCreateGlyphTypeface();
            Assert.NotNull(typeface);

            var glyph = typeface!.CharacterToGlyphMap['H'];
            var key = new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale(24f), 0, GlyphMaskMode.Antialiased);

            Assert.True(typeface.TryGetGlyphInkBounds(glyph, out var box));

            var mask = GlyphMasks.Build(typeface, new GlyphPathBuilder(), key);
            var scale = key.PixelsPerEm / typeface.Metrics.DesignEmHeight;

            Assert.Equal((int)Math.Floor(box.XMin * scale) - GlyphMasks.Apron, mask.Left);
            Assert.Equal((int)Math.Floor(-box.YMax * scale) - GlyphMasks.Apron, mask.Top);
            Assert.Contains(mask.Alpha, b => b == 255);
        }
    }
}
