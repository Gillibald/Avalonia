using System;
using System.Collections.Generic;
using Avalonia.Media.Fonts.Rasterization;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// The per-typeface A8 atlas that stores transformed glyph masks on GPU contexts: entries
    /// hold their masks exactly, keep empty gutters for bilinear sampling, and the page budget
    /// is enforced least recently used first.
    /// </summary>
    public class GlyphMaskAtlasTests
    {
        [Fact]
        public void Entries_Hold_Their_Masks_With_Empty_Gutters()
        {
            var atlas = new GlyphMaskAtlas(8 * 1024 * 1024);
            var random = new Random(1234);
            var tick = atlas.Tick();
            var entries = new List<(GlyphMask Mask, GlyphAtlasSlot Slot)>();

            for (var i = 0; i < 400; i++)
            {
                var mask = CreateMask(random, random.Next(2, 70), random.Next(2, 90));

                Assert.True(atlas.TryAdd(Key(i), mask, tick, out var slot));
                Assert.False(slot.IsEmpty);
                entries.Add((mask, slot));
            }

            foreach (var (mask, slot) in entries)
            {
                var pixels = slot.Page!.Pixels;

                Assert.Equal(mask.Left, slot.Left);
                Assert.Equal(mask.Top, slot.Top);

                for (var y = 0; y < mask.Height; y++)
                {
                    for (var x = 0; x < mask.Width; x++)
                    {
                        Assert.Equal(mask.Alpha[y * mask.Width + x], pixels[(slot.Y + y) * GlyphMaskAtlas.PageWidth + slot.X + x]);
                    }

                    // The column right of the entry stays empty.
                    Assert.Equal(0, pixels[(slot.Y + y) * GlyphMaskAtlas.PageWidth + slot.X + mask.Width]);
                }

                // So does the row below it, gutter corner included.
                for (var x = 0; x <= mask.Width; x++)
                {
                    Assert.Equal(0, pixels[(slot.Y + mask.Height) * GlyphMaskAtlas.PageWidth + slot.X + x]);
                }
            }

            // No two entries, gutters included, share a pixel.
            for (var i = 0; i < entries.Count; i++)
            {
                for (var j = i + 1; j < entries.Count; j++)
                {
                    var a = entries[i].Slot;
                    var b = entries[j].Slot;

                    if (a.Page != b.Page)
                    {
                        continue;
                    }

                    var overlaps = a.X < b.X + b.Width + 1 && b.X < a.X + a.Width + 1 &&
                                   a.Y < b.Y + b.Height + 1 && b.Y < a.Y + a.Height + 1;

                    Assert.False(overlaps, $"entries {i} and {j} overlap");
                }
            }
        }

        [Fact]
        public void A_Repeated_Key_Returns_The_First_Entry()
        {
            var atlas = new GlyphMaskAtlas(8 * 1024 * 1024);
            var random = new Random(7);
            var tick = atlas.Tick();

            Assert.True(atlas.TryAdd(Key(1), CreateMask(random, 10, 12), tick, out var first));
            Assert.True(atlas.TryAdd(Key(1), CreateMask(random, 30, 30), tick, out var second));
            Assert.True(atlas.TryGet(Key(1), tick, out var found));

            Assert.Equal(first.X, second.X);
            Assert.Equal(first.Y, found.Y);
            Assert.Equal(10, found.Width);
            Assert.Equal(1, atlas.Count);
        }

        [Fact]
        public void Corrected_Entries_Hold_Their_Bucket_Table_Values_On_Pages_Of_Their_Bucket()
        {
            var atlas = new GlyphMaskAtlas(8 * 1024 * 1024);
            var random = new Random(11);
            var tick = atlas.Tick();
            var mask = CreateMask(random, 20, 24);

            Assert.True(atlas.TryAdd(Key(1), GlyphMaskAtlas.Uncorrected, mask, tick, out var raw));
            Assert.True(atlas.TryAdd(Key(1), 0, mask, tick, out var dark));
            Assert.True(atlas.TryAdd(Key(1), MaskGamma.BucketCount - 1, mask, tick, out var light));

            // One glyph, three entries: the same key is stored once per correction.
            Assert.Equal(3, atlas.Count);
            Assert.Equal(3, atlas.GetPages().Length);

            foreach (var (slot, bucket) in new[] { (raw, GlyphMaskAtlas.Uncorrected), (dark, 0), (light, MaskGamma.BucketCount - 1) })
            {
                Assert.Equal(bucket, slot.Page!.Bucket);
                Assert.True(atlas.TryGet(Key(1), bucket, tick, out var found));
                Assert.Same(slot.Page, found.Page);

                for (var y = 0; y < mask.Height; y++)
                {
                    for (var x = 0; x < mask.Width; x++)
                    {
                        var value = mask.Alpha[y * mask.Width + x];

                        Assert.Equal(bucket == GlyphMaskAtlas.Uncorrected ? value : MaskGamma.GetTable(bucket)[value],
                            slot.Page.Pixels[(slot.Y + y) * GlyphMaskAtlas.PageWidth + slot.X + x]);
                    }
                }
            }
        }

        [Fact]
        public void Masks_Too_Large_For_A_Page_Are_Refused()
        {
            var atlas = new GlyphMaskAtlas(8 * 1024 * 1024);
            var random = new Random(3);

            Assert.False(atlas.TryAdd(Key(1), CreateMask(random, GlyphMaskAtlas.PageWidth, 10), atlas.Tick(), out _));
            Assert.False(atlas.TryAdd(Key(2), CreateMask(random, 10, GlyphMaskAtlas.MaxPageHeight), atlas.Tick(), out _));
            Assert.Equal(0, atlas.Count);
        }

        [Fact]
        public void Pages_Over_Budget_Are_Evicted_Least_Recently_Used_First()
        {
            // Each mask needs a page of its own (two fit neither side by side nor stacked), and the
            // budget holds two such pages.
            var random = new Random(11);
            var tall = GlyphMaskAtlas.MaxPageHeight / 2 + 10;
            var pageBytes = (long)GlyphMaskAtlas.PageWidth * ((tall + 1 + 63) / 64 * 64);
            var atlas = new GlyphMaskAtlas((int)(pageBytes * 2 + pageBytes / 2));

            Assert.True(atlas.TryAdd(Key(1), CreateMask(random, 600, tall), atlas.Tick(), out var a));
            Assert.True(atlas.TryAdd(Key(2), CreateMask(random, 600, tall), atlas.Tick(), out var b));
            Assert.NotSame(a.Page, b.Page);

            // Drawing the first entry again makes the second page the least recently used.
            Assert.True(atlas.TryGet(Key(1), atlas.Tick(), out _));
            Assert.True(atlas.TryAdd(Key(3), CreateMask(random, 600, tall), atlas.Tick(), out _));

            Assert.True(atlas.TryGet(Key(1), atlas.Tick(), out _));
            Assert.False(atlas.TryGet(Key(2), atlas.Tick(), out _));
            Assert.True(atlas.TryGet(Key(3), atlas.Tick(), out _));
            Assert.True(b.Page!.IsEvicted);
            Assert.False(a.Page!.IsEvicted);
            Assert.Equal(1, atlas.Evictions);
            Assert.True(atlas.AllocatedBytes <= atlas.BudgetBytes,
                $"{atlas.AllocatedBytes} bytes allocated over a budget of {atlas.BudgetBytes}");
        }

        [Fact]
        public void Pages_Used_By_The_Current_Draw_Are_Not_Evicted()
        {
            var random = new Random(13);
            var tall = GlyphMaskAtlas.MaxPageHeight / 2 + 10;
            var pageBytes = (long)GlyphMaskAtlas.PageWidth * ((tall + 1 + 63) / 64 * 64);
            var atlas = new GlyphMaskAtlas((int)(pageBytes * 2 + pageBytes / 2));
            var tick = atlas.Tick();

            // One draw placing three entries keeps all of them, over budget.
            for (var i = 0; i < 3; i++)
            {
                Assert.True(atlas.TryAdd(Key(i), CreateMask(random, 600, tall), tick, out _));
            }

            for (var i = 0; i < 3; i++)
            {
                Assert.True(atlas.TryGet(Key(i), tick, out var slot));
                Assert.False(slot.Page!.IsEvicted);
            }

            Assert.Equal(0, atlas.Evictions);
        }

        [Fact]
        public void The_Atlas_Budget_Is_The_Mask_Cache_Budget()
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);

            Assert.Equal(typeface.MaskCache.BudgetBytes, typeface.MaskAtlas.BudgetBytes);
        }

        private static GlyphMaskKey Key(int glyph)
            => new((ushort)glyph, 160, 0, GlyphMaskMode.Antialiased, GridFit: false,
                Transform: new GlyphMaskTransform(0, 100, -100, 0));

        private static GlyphMask CreateMask(Random random, int width, int height)
        {
            var alpha = new byte[width * height];

            for (var i = 0; i < alpha.Length; i++)
            {
                alpha[i] = (byte)random.Next(1, 256);
            }

            return new GlyphMask(alpha, width, height, -random.Next(0, 4), -random.Next(0, 20));
        }
    }
}
