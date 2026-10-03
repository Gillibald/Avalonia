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
        public void Entries_Keep_An_Empty_Row_And_Column_On_Every_Side()
        {
            var atlas = new GlyphMaskAtlas(8 * 1024 * 1024);
            var random = new Random(4321);
            var tick = atlas.Tick();
            var entries = new List<(GlyphMask Mask, GlyphAtlasSlot Slot)>();

            // Enough entries to fill several shelves, so some start at the page's left edge and
            // some on its top shelf.
            for (var i = 0; i < 400; i++)
            {
                var mask = CreateMask(random, random.Next(2, 70), random.Next(2, 90));

                Assert.True(atlas.TryAdd(Key(i), mask, tick, out var slot));
                entries.Add((mask, slot));
            }

            foreach (var (mask, slot) in entries)
            {
                var pixels = slot.Page!.Pixels;

                // A bilinear draw of the entry reads one texel beyond each edge, which must be
                // empty page rather than the clamped edge of the page or a neighbour's coverage.
                Assert.True(slot.X >= 1 && slot.Y >= 1, $"entry at ({slot.X}, {slot.Y}) touches the page edge");
                Assert.True(slot.X + mask.Width < GlyphMaskAtlas.PageWidth && slot.Y + mask.Height < slot.Page.Height,
                    $"entry at ({slot.X}, {slot.Y}) touches the page edge");

                for (var x = slot.X - 1; x <= slot.X + mask.Width; x++)
                {
                    Assert.Equal(0, pixels[(slot.Y - 1) * GlyphMaskAtlas.PageWidth + x]);
                    Assert.Equal(0, pixels[(slot.Y + mask.Height) * GlyphMaskAtlas.PageWidth + x]);
                }

                for (var y = slot.Y; y < slot.Y + mask.Height; y++)
                {
                    Assert.Equal(0, pixels[y * GlyphMaskAtlas.PageWidth + slot.X - 1]);
                    Assert.Equal(0, pixels[y * GlyphMaskAtlas.PageWidth + slot.X + mask.Width]);
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
        public void Corrected_Entries_Hold_Their_Bucket_Table_Values()
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

            foreach (var (slot, bucket) in new[] { (raw, GlyphMaskAtlas.Uncorrected), (dark, 0), (light, MaskGamma.BucketCount - 1) })
            {
                Assert.True(atlas.TryGet(Key(1), bucket, tick, out var found));
                Assert.Same(slot.Page, found.Page);
                Assert.Equal(slot.X, found.X);
                Assert.Equal(slot.Y, found.Y);

                for (var y = 0; y < mask.Height; y++)
                {
                    for (var x = 0; x < mask.Width; x++)
                    {
                        var value = mask.Alpha[y * mask.Width + x];

                        Assert.Equal(bucket == GlyphMaskAtlas.Uncorrected ? value : MaskGamma.GetTable(bucket)[value],
                            slot.Page!.Pixels[(slot.Y + y) * GlyphMaskAtlas.PageWidth + slot.X + x]);
                    }
                }
            }
        }

        [Fact]
        public void Entries_Of_Every_Luminance_Bucket_Share_One_Page()
        {
            var atlas = new GlyphMaskAtlas(8 * 1024 * 1024);
            var random = new Random(17);
            var tick = atlas.Tick();
            var slots = new List<GlyphAtlasSlot>();

            // Colour glyph layers and text of every luminance bucket, a few glyphs each.
            for (var bucket = GlyphMaskAtlas.Uncorrected; bucket < MaskGamma.BucketCount; bucket++)
            {
                for (var glyph = 0; glyph < 4; glyph++)
                {
                    Assert.True(atlas.TryAdd(Key(glyph), bucket, CreateMask(random, 12, 16), tick, out var slot));
                    slots.Add(slot);
                }
            }

            var page = Assert.Single(atlas.GetPages());

            Assert.Equal((MaskGamma.BucketCount + 1) * 4, atlas.Count);

            foreach (var slot in slots)
            {
                Assert.Same(page, slot.Page);
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

        [Fact]
        public void Entries_Of_Two_Owners_With_Equal_Keys_Hold_Their_Own_Masks()
        {
            var atlas = new GlyphMaskAtlas(8 * 1024 * 1024);
            var random = new Random(17);
            var tick = atlas.Tick();
            var first = CreateMask(random, 12, 16);
            var second = CreateMask(random, 14, 18);

            Assert.True(atlas.TryAdd(1, Key(1), 0, first, tick, out var a));
            Assert.True(atlas.TryAdd(2, Key(1), 0, second, tick, out var b));
            Assert.Equal(2, atlas.Count);

            foreach (var (owner, mask) in new[] { (1, first), (2, second) })
            {
                Assert.True(atlas.TryGet(owner, Key(1), 0, tick, out var slot));
                Assert.Equal(owner == 1 ? a.X : b.X, slot.X);
                Assert.Equal(owner == 1 ? a.Y : b.Y, slot.Y);
                Assert.Equal(mask.Width, slot.Width);

                for (var y = 0; y < mask.Height; y++)
                {
                    for (var x = 0; x < mask.Width; x++)
                    {
                        Assert.Equal(MaskGamma.GetTable(0)[mask.Alpha[y * mask.Width + x]],
                            slot.Page!.Pixels[(slot.Y + y) * GlyphMaskAtlas.PageWidth + slot.X + x]);
                    }
                }
            }

            Assert.False(atlas.TryGet(3, Key(1), 0, tick, out _));
            Assert.False(atlas.TryGet(Key(1), 0, tick, out _));
        }

        [Fact]
        public void The_Shared_Atlas_Budget_Bounds_All_Typefaces_Together()
        {
            Assert.Equal(GlyphMaskAtlas.SharedBudgetBytes, GlyphMaskAtlas.Shared.BudgetBytes);

            // Two owners each fill a page; a third owner's page takes the place of the one used
            // longest ago, whoever owns it.
            var random = new Random(19);
            var tall = GlyphMaskAtlas.MaxPageHeight / 2 + 10;
            var atlas = new GlyphMaskAtlas((int)(TallPageBytes(tall) * 2 + TallPageBytes(tall) / 2));

            Assert.True(atlas.TryAdd(1, Key(1), 0, CreateMask(random, 600, tall), atlas.Tick(), out var a));
            Assert.True(atlas.TryAdd(2, Key(1), 0, CreateMask(random, 600, tall), atlas.Tick(), out var b));
            Assert.True(atlas.TryGet(1, Key(1), 0, atlas.Tick(), out _));
            Assert.True(atlas.TryAdd(3, Key(1), 0, CreateMask(random, 600, tall), atlas.Tick(), out _));

            Assert.False(a.Page!.IsEvicted);
            Assert.True(b.Page!.IsEvicted);
            Assert.False(atlas.TryGet(2, Key(1), 0, atlas.Tick(), out _));
            Assert.True(atlas.AllocatedBytes <= atlas.BudgetBytes,
                $"{atlas.AllocatedBytes} bytes allocated over a budget of {atlas.BudgetBytes}");
        }

        [Fact]
        public void Pages_Drawn_In_The_Current_Session_Are_Not_Evicted()
        {
            var random = new Random(23);
            var tall = GlyphMaskAtlas.MaxPageHeight / 2 + 10;
            var atlas = new GlyphMaskAtlas((int)(TallPageBytes(tall) * 2 + TallPageBytes(tall) / 2));

            atlas.BeginSession();

            // Three draws of one session, a page each: without the session, the third would
            // evict the first draw's page.
            var slots = new GlyphAtlasSlot[3];

            for (var i = 0; i < slots.Length; i++)
            {
                Assert.True(atlas.TryAdd(Key(i), CreateMask(random, 600, tall), atlas.Tick(), out slots[i]));
            }

            Assert.All(slots, slot => Assert.False(slot.Page!.IsEvicted));
            Assert.Equal(0, atlas.Evictions);
            Assert.True(atlas.AllocatedBytes > atlas.BudgetBytes, "the session's pages fit the budget");
        }

        [Fact]
        public void The_Atlas_Grows_Past_Its_Budget_Only_For_Pages_Of_The_Current_Session()
        {
            var random = new Random(29);
            var tall = GlyphMaskAtlas.MaxPageHeight / 2 + 10;
            var atlas = new GlyphMaskAtlas((int)(TallPageBytes(tall) * 2 + TallPageBytes(tall) / 2));
            var slots = new GlyphAtlasSlot[3];

            atlas.BeginSession();

            for (var i = 0; i < slots.Length; i++)
            {
                Assert.True(atlas.TryAdd(Key(i), CreateMask(random, 600, tall), atlas.Tick(), out slots[i]));
            }

            // The next session draws the first page again and adds a page: the pages the session
            // did not draw go, back within the budget, and the drawn one stays.
            atlas.BeginSession();

            Assert.True(atlas.TryGet(Key(0), atlas.Tick(), out _));
            Assert.True(atlas.TryAdd(Key(3), CreateMask(random, 600, tall), atlas.Tick(), out var added));

            Assert.False(slots[0].Page!.IsEvicted);
            Assert.True(slots[1].Page!.IsEvicted);
            Assert.True(slots[2].Page!.IsEvicted);
            Assert.False(added.Page!.IsEvicted);
            Assert.Equal(2, atlas.Evictions);
            Assert.True(atlas.AllocatedBytes <= atlas.BudgetBytes,
                $"{atlas.AllocatedBytes} bytes allocated over a budget of {atlas.BudgetBytes}");
        }

        [Fact]
        public void A_Session_Keeps_Its_Pages_Up_To_Twice_The_Budget()
        {
            var random = new Random(31);
            var tall = GlyphMaskAtlas.MaxPageHeight / 2 + 10;
            var atlas = new GlyphMaskAtlas((int)(TallPageBytes(tall) * 2 + TallPageBytes(tall) / 2));
            var slots = new GlyphAtlasSlot[8];

            atlas.BeginSession();

            // Past twice the budget, only the current draw's page is kept: the least recently
            // used page of the session goes first.
            for (var i = 0; i < slots.Length; i++)
            {
                Assert.True(atlas.TryAdd(Key(i), CreateMask(random, 600, tall), atlas.Tick(), out slots[i]));
                Assert.True(atlas.AllocatedBytes <= 2L * atlas.BudgetBytes,
                    $"{atlas.AllocatedBytes} bytes allocated, twice the budget is {2L * atlas.BudgetBytes}");
            }

            Assert.Equal(5, atlas.GetPages().Length);
            Assert.Equal(3, atlas.Evictions);
            Assert.True(slots[0].Page!.IsEvicted);
            Assert.True(slots[2].Page!.IsEvicted);
            Assert.False(slots[3].Page!.IsEvicted);
            Assert.False(slots[7].Page!.IsEvicted);
        }

        [Fact]
        public void Disposing_A_Typeface_Removes_Its_Atlas_Entries()
        {
            var typeface = GlyphAtlasBatchTests.LoadAsset("Inter-Regular.ttf");
            var other = GlyphAtlasBatchTests.LoadAsset("Inter-Regular.ttf");
            var atlas = GlyphMaskAtlas.Shared;
            var random = new Random(37);
            var tick = atlas.Tick();

            Assert.True(atlas.TryAdd(typeface.MaskOwnerId, Key(1), 0, CreateMask(random, 12, 16), tick, out _));
            Assert.True(atlas.TryAdd(typeface.MaskOwnerId, Key(2), 0, new GlyphMask(Array.Empty<byte>(), 0, 0, 0, 0),
                tick, out _));
            Assert.True(atlas.TryAdd(other.MaskOwnerId, Key(1), 0, CreateMask(random, 12, 16), tick, out _));

            var owner = typeface.MaskOwnerId;

            typeface.Dispose();

            Assert.False(atlas.TryGet(owner, Key(1), 0, atlas.Tick(), out _));
            Assert.False(atlas.TryGet(owner, Key(2), 0, atlas.Tick(), out _));
            Assert.True(atlas.TryGet(other.MaskOwnerId, Key(1), 0, atlas.Tick(), out _));

            other.Dispose();
        }

        [Fact]
        public void A_Page_Left_Without_Entries_By_Retired_Owners_Is_Dropped_When_The_Next_Session_Begins()
        {
            var random = new Random(41);
            var atlas = new GlyphMaskAtlas(GlyphMaskAtlas.SharedBudgetBytes);
            var tick = atlas.Tick();

            // Owner 1 alone fills a page; owners 1 and 2 share another.
            Assert.True(atlas.TryAdd(1, Key(1), 0, CreateMask(random, GlyphMaskAtlas.PageWidth - 2,
                GlyphMaskAtlas.MaxPageHeight - 2), tick, out var alone));
            Assert.True(atlas.TryAdd(1, Key(2), 0, CreateMask(random, 12, 16), tick, out var mixed));
            Assert.True(atlas.TryAdd(2, Key(2), 0, CreateMask(random, 12, 16), tick, out var kept));
            Assert.NotSame(alone.Page, mixed.Page);
            Assert.Same(mixed.Page, kept.Page);

            atlas.Retire(1);

            Assert.False(atlas.TryGet(1, Key(1), 0, tick, out _));
            Assert.False(atlas.TryGet(1, Key(2), 0, tick, out _));
            Assert.False(alone.Page!.IsEvicted);

            atlas.BeginSession();

            Assert.True(alone.Page.IsEvicted);
            Assert.False(mixed.Page!.IsEvicted);
            Assert.True(atlas.TryGet(2, Key(2), 0, atlas.Tick(), out _));
            Assert.Equal(1, atlas.Evictions);
            Assert.Single(atlas.GetPages());
        }

        private static long TallPageBytes(int tall) => (long)GlyphMaskAtlas.PageWidth * ((tall + 1 + 63) / 64 * 64);

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
