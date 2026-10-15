using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media.Fonts.Rasterization;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// The per-typeface A8 atlas that stores transformed glyph masks on GPU contexts: entries
    /// hold their masks exactly, keep empty gutters for bilinear sampling, and the glyph cache
    /// budget evicts the page used least recently first.
    /// </summary>
    public class GlyphMaskAtlasTests
    {
        [Fact]
        public void Entries_Hold_Their_Masks_With_Empty_Gutters()
        {
            var atlas = new GlyphMaskAtlas();
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
                var pixels = slot.Page!.Pixels!;

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
            var atlas = new GlyphMaskAtlas();
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
                var pixels = slot.Page!.Pixels!;

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
            var atlas = new GlyphMaskAtlas();
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
            var atlas = new GlyphMaskAtlas();
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
                            slot.Page!.Pixels![(slot.Y + y) * GlyphMaskAtlas.PageWidth + slot.X + x]);
                    }
                }
            }
        }

        [Fact]
        public void Entries_Of_Every_Luminance_Bucket_Share_One_Page()
        {
            var atlas = new GlyphMaskAtlas();
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
            var atlas = new GlyphMaskAtlas();
            var random = new Random(3);

            Assert.False(atlas.TryAdd(Key(1), CreateMask(random, GlyphMaskAtlas.PageWidth, 10), atlas.Tick(), out _));
            Assert.False(atlas.TryAdd(Key(2), CreateMask(random, 10, GlyphMaskAtlas.MaxPageHeight), atlas.Tick(), out _));
            Assert.Equal(0, atlas.Count);
        }

        [Fact]
        public void Pages_Over_The_Limit_Are_Evicted_Least_Recently_Used_First()
        {
            // Each mask needs a page of its own (two fit neither side by side nor stacked), and the
            // limit holds two and a half such pages. Every draw is a frame of its own.
            var random = new Random(11);
            var tall = GlyphMaskAtlas.MaxPageHeight / 2 + 10;
            var budget = new GlyphCacheBudget(TallPageBytes(tall) * 5 / 2);
            var atlas = new GlyphMaskAtlas(budget);
            GlyphAtlasSlot a, b;

            using (budget.BeginFrame())
                Assert.True(atlas.TryAdd(Key(1), CreateMask(random, 600, tall), atlas.Tick(), out a));

            using (budget.BeginFrame())
                Assert.True(atlas.TryAdd(Key(2), CreateMask(random, 600, tall), atlas.Tick(), out b));

            Assert.NotSame(a.Page, b.Page);

            // Drawing the first entry again makes the second page the least recently used.
            using (budget.BeginFrame())
                Assert.True(atlas.TryGet(Key(1), atlas.Tick(), out _));

            using (budget.BeginFrame())
                Assert.True(atlas.TryAdd(Key(3), CreateMask(random, 600, tall), atlas.Tick(), out _));

            // The next frame trims to the limit.
            using (budget.BeginFrame())
            {
            }

            Assert.True(atlas.TryGet(Key(1), atlas.Tick(), out _));
            Assert.False(atlas.TryGet(Key(2), atlas.Tick(), out _));
            Assert.True(atlas.TryGet(Key(3), atlas.Tick(), out _));
            Assert.True(b.Page!.IsEvicted);
            Assert.False(a.Page!.IsEvicted);
            Assert.Equal(1, atlas.Evictions);
            Assert.True(atlas.AllocatedBytes <= budget.LimitBytes,
                $"{atlas.AllocatedBytes} bytes allocated over a limit of {budget.LimitBytes}");
        }

        [Fact]
        public void Pages_Used_By_The_Current_Frame_Are_Not_Evicted()
        {
            var random = new Random(13);
            var tall = GlyphMaskAtlas.MaxPageHeight / 2 + 10;
            var budget = new GlyphCacheBudget(TallPageBytes(tall) * 5 / 2);
            var atlas = new GlyphMaskAtlas(budget);

            // One frame placing four entries keeps all of them, past half over the limit.
            using (budget.BeginFrame())
            {
                var tick = atlas.Tick();

                for (var i = 0; i < 4; i++)
                {
                    Assert.True(atlas.TryAdd(Key(i), CreateMask(random, 600, tall), tick, out _));
                }

                for (var i = 0; i < 4; i++)
                {
                    Assert.True(atlas.TryGet(Key(i), tick, out var slot));
                    Assert.False(slot.Page!.IsEvicted);
                }
            }

            Assert.Equal(0, atlas.Evictions);
            Assert.True(atlas.AllocatedBytes > budget.InlineLimitBytes, "the frame's pages fit");
        }

        [Fact]
        public void Entries_Of_Two_Owners_With_Equal_Keys_Hold_Their_Own_Masks()
        {
            var atlas = new GlyphMaskAtlas();
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
                            slot.Page!.Pixels![(slot.Y + y) * GlyphMaskAtlas.PageWidth + slot.X + x]);
                    }
                }
            }

            Assert.False(atlas.TryGet(3, Key(1), 0, tick, out _));
            Assert.False(atlas.TryGet(Key(1), 0, tick, out _));
        }


        [Fact]
        public void Pages_Of_Every_Owner_Are_Evicted_Least_Recently_Used_First()
        {
            // Two owners each fill a page; a third owner's page takes the place of the one used
            // longest ago, whoever owns it.
            var random = new Random(19);
            var tall = GlyphMaskAtlas.MaxPageHeight / 2 + 10;
            var budget = new GlyphCacheBudget(TallPageBytes(tall) * 5 / 2);
            var atlas = new GlyphMaskAtlas(budget);
            GlyphAtlasSlot a, b;

            using (budget.BeginFrame())
                Assert.True(atlas.TryAdd(1, Key(1), 0, CreateMask(random, 600, tall), atlas.Tick(), out a));

            using (budget.BeginFrame())
                Assert.True(atlas.TryAdd(2, Key(1), 0, CreateMask(random, 600, tall), atlas.Tick(), out b));

            using (budget.BeginFrame())
                Assert.True(atlas.TryGet(1, Key(1), 0, atlas.Tick(), out _));

            using (budget.BeginFrame())
                Assert.True(atlas.TryAdd(3, Key(1), 0, CreateMask(random, 600, tall), atlas.Tick(), out _));

            using (budget.BeginFrame())
            {
            }

            Assert.False(a.Page!.IsEvicted);
            Assert.True(b.Page!.IsEvicted);
            Assert.False(atlas.TryGet(2, Key(1), 0, atlas.Tick(), out _));
            Assert.True(atlas.AllocatedBytes <= budget.LimitBytes,
                $"{atlas.AllocatedBytes} bytes allocated over a limit of {budget.LimitBytes}");
        }

        [Fact]
        public void Pages_Drawn_In_The_Previous_Frame_Are_Kept_Up_To_A_Quarter_Over_The_Limit()
        {
            var random = new Random(23);
            var tall = GlyphMaskAtlas.MaxPageHeight / 2 + 10;
            var budget = new GlyphCacheBudget(TallPageBytes(tall) * 5 / 2);
            var atlas = new GlyphMaskAtlas(budget);
            var slots = new GlyphAtlasSlot[3];

            // Three pages, a fifth over the limit, in one frame: the next frame keeps them, since a
            // static scene draws them again.
            using (budget.BeginFrame())
            {
                for (var i = 0; i < slots.Length; i++)
                {
                    Assert.True(atlas.TryAdd(Key(i), CreateMask(random, 600, tall), atlas.Tick(), out slots[i]));
                }
            }

            using (budget.BeginFrame())
            {
            }

            Assert.All(slots, slot => Assert.False(slot.Page!.IsEvicted));
            Assert.Equal(0, atlas.Evictions);
            Assert.True(atlas.AllocatedBytes > budget.LimitBytes, "the frame's pages fit the limit");
        }

        [Fact]
        public void Pages_No_Recent_Frame_Drew_Are_Trimmed_To_The_Limit()
        {
            var random = new Random(29);
            var tall = GlyphMaskAtlas.MaxPageHeight / 2 + 10;
            var budget = new GlyphCacheBudget(TallPageBytes(tall) * 5 / 2);
            var atlas = new GlyphMaskAtlas(budget);
            var slots = new GlyphAtlasSlot[3];

            using (budget.BeginFrame())
            {
                for (var i = 0; i < slots.Length; i++)
                {
                    Assert.True(atlas.TryAdd(Key(i), CreateMask(random, 600, tall), atlas.Tick(), out slots[i]));
                }
            }

            // A frame drawing the first page again, then one drawing nothing: the pages neither
            // drew go first, until the atlas is back within the limit.
            using (budget.BeginFrame())
            {
                Assert.True(atlas.TryGet(Key(0), atlas.Tick(), out _));
            }

            using (budget.BeginFrame())
            {
            }

            Assert.False(slots[0].Page!.IsEvicted);
            Assert.True(slots[1].Page!.IsEvicted);
            Assert.Equal(1, atlas.Evictions);
            Assert.True(atlas.AllocatedBytes <= budget.LimitBytes,
                $"{atlas.AllocatedBytes} bytes allocated over a limit of {budget.LimitBytes}");
        }

        [Fact]
        public void Past_A_Quarter_Over_The_Limit_The_Pages_Of_The_Previous_Frame_Stay_Until_A_Frame_Passes_Without_Them()
        {
            var random = new Random(31);
            var tall = GlyphMaskAtlas.MaxPageHeight / 2 + 10;
            var page = TallPageBytes(tall);
            var budget = new GlyphCacheBudget(page * 5 / 2);
            var atlas = new GlyphMaskAtlas(budget);
            var slots = new GlyphAtlasSlot[8];

            using (budget.BeginFrame())
            {
                for (var i = 0; i < 5; i++)
                {
                    Assert.True(atlas.TryAdd(Key(i), CreateMask(random, 600, tall), atlas.Tick(), out slots[i]));
                }
            }

            // Twice the limit: the next frame keeps the previous frame's pages, which a static
            // scene draws from, and places pages of its own beside them.
            using (budget.BeginFrame())
            {
                Assert.Equal(5 * page, atlas.AllocatedBytes);

                for (var i = 5; i < slots.Length; i++)
                {
                    Assert.True(atlas.TryAdd(Key(i), CreateMask(random, 600, tall), atlas.Tick(), out slots[i]));
                }
            }

            // A frame later the first frame's pages go; the second frame's stay, still within a
            // quarter over the limit.
            using (budget.BeginFrame())
            {
            }

            Assert.All(slots[..5], slot => Assert.True(slot.Page!.IsEvicted));
            Assert.All(slots[5..], slot => Assert.False(slot.Page!.IsEvicted));
            Assert.Equal(5, atlas.Evictions);
            Assert.True(atlas.AllocatedBytes <= budget.SoftLimitBytes);
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
        public void A_Page_Left_Without_Entries_By_Retired_Owners_Is_Dropped_When_The_Next_Frame_Begins()
        {
            var random = new Random(41);
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var atlas = new GlyphMaskAtlas(budget);
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

            using (budget.BeginFrame())
            {
            }

            Assert.True(alone.Page.IsEvicted);
            Assert.False(mixed.Page!.IsEvicted);
            Assert.True(atlas.TryGet(2, Key(2), 0, atlas.Tick(), out _));
            Assert.Equal(1, atlas.Evictions);
            Assert.Single(atlas.GetPages());
        }

        [Fact]
        public void Rgba_Entries_Hold_Their_Colour_Masks_In_Rgba_Order_With_Empty_Gutters()
        {
            var atlas = new GlyphMaskAtlas(format: GlyphAtlasFormat.Rgba);
            var random = new Random(99);
            var tick = atlas.Tick();
            var entries = new List<(GlyphMask Mask, GlyphAtlasSlot Slot)>();

            for (var i = 0; i < 200; i++)
            {
                var mask = CreateColorMask(random, random.Next(2, 60), random.Next(2, 70));

                Assert.True(atlas.TryAdd(Key(i), mask, tick, out var slot));
                entries.Add((mask, slot));
            }

            foreach (var (mask, slot) in entries)
            {
                var page = slot.Page!;

                Assert.Equal(4, page.BytesPerPixel);
                Assert.Equal(GlyphMaskAtlas.PageWidth * 4, page.RowBytes);
                Assert.Equal(page.RowBytes * page.Height, page.Pixels!.Length);
                Assert.Equal(mask.Left, slot.Left);
                Assert.Equal(mask.Top, slot.Top);

                // The masks are BGRA; the page holds the same premultiplied colour in RGBA order, and
                // the row and column on every side of the entry stay transparent.
                for (var y = -1; y <= mask.Height; y++)
                {
                    for (var x = -1; x <= mask.Width; x++)
                    {
                        var texel = (slot.Y + y) * page.RowBytes + (slot.X + x) * 4;
                        var inside = x >= 0 && y >= 0 && x < mask.Width && y < mask.Height;

                        for (var c = 0; c < 4; c++)
                        {
                            var expected = inside ? mask.Alpha[(y * mask.Width + x) * 4 + s_bgraOfRgba[c]] : 0;

                            Assert.Equal(expected, page.Pixels![texel + c]);
                        }
                    }
                }
            }
        }

        [Fact]
        public void Filling_A_Page_Allocates_At_Most_Twice_Its_Pixels()
        {
            // One shelf per mask: the page grows with every entry until it reaches its full height.
            // Each growth copies the page into a new array, so the arrays a filling page leaves
            // behind are garbage a zoom that keeps laying out new scales makes in every frame.
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var atlas = new GlyphMaskAtlas(budget);
            var random = new Random(43);
            var masks = new GlyphMask[(GlyphMaskAtlas.MaxPageHeight - 2) / 41];

            for (var i = 0; i < masks.Length; i++)
            {
                masks[i] = CreateMask(random, 900, 40);
            }

            GlyphAtlasSlot slot = default;
            long allocated;

            using (budget.BeginFrame())
            {
                var tick = atlas.Tick();
                var before = ThreadAllocations.Start();

                for (var i = 0; i < masks.Length; i++)
                {
                    Assert.True(atlas.TryAdd(Key(i), masks[i], tick, out slot));
                }

                allocated = ThreadAllocations.Since(before);
            }

            var page = slot.Page!;

            Assert.Single(atlas.GetPages());
            Assert.Equal(GlyphMaskAtlas.MaxPageHeight, page.Height);
            Assert.True(allocated <= 2L * page.Pixels!.Length,
                $"filling a page of {page.Pixels!.Length} bytes allocated {allocated} bytes");
        }

        [Fact]
        public void Pages_Opened_After_A_Page_Filled_Are_Allocated_Once()
        {
            // An atlas that filled a page keeps growing, as a zoom laying out new scales does: the
            // pages it opens next fill too, so growing them in steps only adds garbage.
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var atlas = new GlyphMaskAtlas(budget);
            var random = new Random(53);
            var perPage = (GlyphMaskAtlas.MaxPageHeight - 2) / 41;
            var masks = new GlyphMask[perPage * 3];

            for (var i = 0; i < masks.Length; i++)
            {
                masks[i] = CreateMask(random, 900, 40);
            }

            long allocated;

            using (budget.BeginFrame())
            {
                var tick = atlas.Tick();
                var before = ThreadAllocations.Start();

                for (var i = 0; i < masks.Length; i++)
                {
                    Assert.True(atlas.TryAdd(Key(i), masks[i], tick, out _));
                }

                allocated = ThreadAllocations.Since(before);
            }

            var pages = atlas.GetPages();
            var pageBytes = (long)GlyphMaskAtlas.PageWidth * GlyphMaskAtlas.MaxPageHeight;

            Assert.Equal(3, pages.Length);
            Assert.All(pages, page => Assert.Equal(GlyphMaskAtlas.MaxPageHeight, page.Height));

            // The first page doubles as it fills; the two after it are allocated at full height.
            Assert.True(allocated <= 4 * pageBytes,
                $"filling three pages of {pageBytes} bytes allocated {allocated} bytes");
        }

        [Fact]
        public void Rgba_Pages_Charge_Four_Bytes_Per_Texel_And_Eviction_Credits_Them()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var atlas = new GlyphMaskAtlas(budget, format: GlyphAtlasFormat.Rgba);
            var random = new Random(5);
            GlyphAtlasSlot slot;

            using (budget.BeginFrame())
            {
                Assert.True(atlas.TryAdd(Key(1), CreateColorMask(random, 40, 40), atlas.Tick(), out slot));
            }

            var page = slot.Page!;
            var bytes = (long)GlyphMaskAtlas.PageWidth * 4 * page.Height;

            Assert.Equal(bytes, atlas.AllocatedBytes);
            Assert.Equal(bytes + GlyphMaskAtlas.IndexEntryBytes, budget.BytesOf(GlyphCachePoolKind.Atlas));
            Assert.Equal(bytes + GlyphMaskAtlas.IndexEntryBytes,
                ((IGlyphCachePool)atlas).EvictOldest(long.MaxValue, long.MaxValue));
            Assert.True(page.IsEvicted);
            Assert.Equal(0, atlas.AllocatedBytes);
            Assert.Equal(0, budget.BytesOf(GlyphCachePoolKind.Atlas));
            Assert.False(atlas.TryGet(Key(1), atlas.Tick(), out _));
        }

        /// <summary>
        /// A page of an atlas whose pages live in GPU textures keeps no array of its texels: the
        /// managed memory it takes is its ink bitmap, an eighth of the page's bytes at most.
        /// </summary>
        [Fact]
        public void A_Resident_Page_Keeps_No_Pixel_Copy()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var atlas = new GlyphMaskAtlas(budget, residentPages: true);
            var random = new Random(43);
            var masks = new GlyphMask[(GlyphMaskAtlas.MaxPageHeight - 2) / 41];

            for (var i = 0; i < masks.Length; i++)
            {
                masks[i] = CreateMask(random, 900, 40);
            }

            var retainedBefore = GC.GetTotalMemory(true);
            long allocated;

            using (budget.BeginFrame())
            {
                var tick = atlas.Tick();
                var before = ThreadAllocations.Start();

                for (var i = 0; i < masks.Length; i++)
                {
                    Assert.True(atlas.TryAdd(Key(i), masks[i], tick, out _));
                }

                allocated = ThreadAllocations.Since(before);
            }

            var retained = GC.GetTotalMemory(true) - retainedBefore;
            var page = Assert.Single(atlas.GetPages());
            var quarter = page.ChargedBytes / 4;

            Assert.Equal(GlyphMaskAtlas.MaxPageHeight, page.Height);
            Assert.Null(page.Pixels);
            Assert.True(allocated < quarter, $"filling a page of {page.ChargedBytes} bytes allocated {allocated} bytes");
            Assert.True(retained < quarter, $"filling a page of {page.ChargedBytes} bytes retained {retained} bytes");
            GC.KeepAlive(masks);
        }

        /// <summary>
        /// An evicted resident page keeps its texture until a frame begins that no frame still
        /// being drawn precedes, then releases it.
        /// </summary>
        [Fact]
        public void An_Evicted_Resident_Page_Releases_Its_Texture_When_The_Next_Frame_Begins()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var atlas = new GlyphMaskAtlas(budget, residentPages: true);
            var texture = new DisposeCounter();
            GlyphAtlasPage page;

            using (budget.BeginFrame())
            {
                Assert.True(atlas.TryAdd(Key(1), CreateMask(new Random(3), 20, 20), atlas.Tick(), out var slot));

                page = slot.Page!;
                page.Realized = texture;

                Assert.True(((IGlyphCachePool)atlas).EvictOldest(long.MaxValue, long.MaxValue) > 0);
                Assert.True(page.IsEvicted);
                Assert.Same(texture, page.Realized);
                Assert.Equal(0, texture.Disposals);
            }

            using (budget.BeginFrame())
            {
            }

            Assert.Null(page.Realized);
            Assert.Equal(1, texture.Disposals);
            Assert.False(page.CanComposeWhole);
        }

        private sealed class DisposeCounter : IDisposable
        {
            public int Disposals { get; private set; }

            public void Dispose() => Disposals++;
        }

        /// <summary>
        /// A page is charged its allocated rows, whatever holds its texels: the budget's limits and
        /// eviction order depend on those numbers.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Pages_Charge_Their_Allocated_Rows_Through_Growth_And_Eviction(bool rgba)
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var atlas = new GlyphMaskAtlas(budget, format: rgba ? GlyphAtlasFormat.Rgba : GlyphAtlasFormat.Alpha8);
            var charges = ChargesThroughGrowth(atlas, budget, new Random(61));

            Assert.True(charges.Distinct().Count() > 2, "the page did not grow");
            Assert.All(atlas.GetPages(), page => Assert.Equal((long)page.RowBytes * page.Height, page.ChargedBytes));

            var held = atlas.AllocatedBytes + atlas.IndexBytes;

            Assert.Equal(held, budget.BytesOf(GlyphCachePoolKind.Atlas));
            Assert.Equal(held, ((IGlyphCachePool)atlas).EvictOldest(long.MaxValue, long.MaxValue));
            Assert.Equal(0, atlas.AllocatedBytes);
            Assert.Equal(0, budget.BytesOf(GlyphCachePoolKind.Atlas));
        }

        /// <summary>
        /// Adds masks one shelf at a time until the atlas has two pages, and returns what the
        /// atlas pool was charged after each add, checked against the pages' rows.
        /// </summary>
        internal static List<long> ChargesThroughGrowth(GlyphMaskAtlas atlas, GlyphCacheBudget budget, Random random)
        {
            var charges = new List<long>();

            for (var i = 0; atlas.GetPages().Length < 2; i++)
            {
                using (budget.BeginFrame())
                {
                    var mask = atlas.Format == GlyphAtlasFormat.Rgba
                        ? CreateColorMask(random, 600, 50)
                        : CreateMask(random, 900, 50);

                    Assert.True(atlas.TryAdd(Key(i), mask, atlas.Tick(), out _));
                }

                var rows = atlas.GetPages().Sum(page => (long)page.RowBytes * page.Height);

                Assert.Equal(rows, atlas.AllocatedBytes);
                Assert.Equal(rows + atlas.IndexBytes, budget.BytesOf(GlyphCachePoolKind.Atlas));
                charges.Add(budget.BytesOf(GlyphCachePoolKind.Atlas));
            }

            return charges;
        }

        /// <summary>
        /// Batching asks whether two sprites hold ink at a pixel they both cover. The pages of an
        /// atlas whose pages live in GPU textures answer from an ink bitmap, which must answer as
        /// the pixels of the same entries do, alone and paired with a page that keeps its pixels.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_Ink_Bitmap_Answers_SharesCoverage_As_The_Pixels_Do(bool rgba)
        {
            var format = rgba ? GlyphAtlasFormat.Rgba : GlyphAtlasFormat.Alpha8;
            var pixelAtlas = new GlyphMaskAtlas(format: format);
            var residentAtlas = new GlyphMaskAtlas(format: format, residentPages: true);
            var random = new Random(71);
            var entries = new List<(GlyphMask Mask, GlyphAtlasSlot Pixel, GlyphAtlasSlot Resident)>();

            for (var i = 0; i < 160; i++)
            {
                var mask = CreateSparseMask(random, random.Next(1, 90), random.Next(1, 70), rgba);

                Assert.True(pixelAtlas.TryAdd(Key(i), mask, pixelAtlas.Tick(), out var pixel));
                Assert.True(residentAtlas.TryAdd(Key(i), mask, residentAtlas.Tick(), out var resident));
                Assert.Equal((pixel.X, pixel.Y), (resident.X, resident.Y));
                entries.Add((mask, pixel, resident));
            }

            var shared = 0;
            const int trials = 6000;

            for (var trial = 0; trial < trials; trial++)
            {
                var a = entries[random.Next(entries.Count)];
                var b = entries[random.Next(entries.Count)];
                var dx = random.Next(-b.Mask.Width + 1, a.Mask.Width);
                var dy = random.Next(-b.Mask.Height + 1, a.Mask.Height);
                var expected = MasksShareInk(a.Mask, b.Mask, dx, dy);

                bool Shares(GlyphAtlasSlot first, GlyphAtlasSlot second) => GlyphAtlasBatchBuilder.SharesCoverage(
                    first.Page!, new GlyphAtlasSprite(first.X, first.Y, first.Width, first.Height, 0, 0),
                    second.Page!, new GlyphAtlasSprite(second.X, second.Y, second.Width, second.Height, dx, dy));

                Assert.Equal(expected, Shares(a.Pixel, b.Pixel));
                Assert.Equal(expected, Shares(a.Resident, b.Resident));
                Assert.Equal(expected, Shares(a.Pixel, b.Resident));
                Assert.Equal(expected, Shares(a.Resident, b.Pixel));

                shared += expected ? 1 : 0;
            }

            Assert.InRange(shared, trials / 10, trials - trials / 10);
        }

        /// <summary>Whether mask <paramref name="b"/>, offset by (<paramref name="dx"/>, <paramref name="dy"/>), holds ink where <paramref name="a"/> does.</summary>
        private static bool MasksShareInk(GlyphMask a, GlyphMask b, int dx, int dy)
        {
            for (var y = Math.Max(0, dy); y < Math.Min(a.Height, dy + b.Height); y++)
            {
                for (var x = Math.Max(0, dx); x < Math.Min(a.Width, dx + b.Width); x++)
                {
                    if (HasInk(a, x, y) && HasInk(b, x - dx, y - dy))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool HasInk(GlyphMask mask, int x, int y)
            => mask.Channels == 4 ? mask.Alpha[(y * mask.Width + x) * 4 + 3] != 0 : mask.Alpha[y * mask.Width + x] != 0;

        /// <summary>A coverage or premultiplied BGRA mask with ink in 1 to 10 % of its texels.</summary>
        internal static GlyphMask CreateSparseMask(Random random, int width, int height, bool rgba)
        {
            var channels = rgba ? 4 : 1;
            var pixels = new byte[width * height * channels];
            var inked = random.Next(1, 11);

            for (var i = 0; i < width * height; i++)
            {
                if (random.Next(100) >= inked)
                {
                    continue;
                }

                var alpha = random.Next(1, 256);

                if (!rgba)
                {
                    pixels[i] = (byte)alpha;
                    continue;
                }

                pixels[i * 4] = (byte)random.Next(0, alpha + 1);
                pixels[i * 4 + 1] = (byte)random.Next(0, alpha + 1);
                pixels[i * 4 + 2] = (byte)random.Next(0, alpha + 1);
                pixels[i * 4 + 3] = (byte)alpha;
            }

            return new GlyphMask(pixels, width, height, 0, 0, channels: channels);
        }

        // For each RGBA channel, the BGRA channel holding it.
        private static readonly int[] s_bgraOfRgba = { 2, 1, 0, 3 };

        /// <summary>A premultiplied BGRA mask of random colours.</summary>
        internal static GlyphMask CreateColorMask(Random random, int width, int height)
        {
            var pixels = new byte[width * height * 4];

            for (var i = 0; i < pixels.Length; i += 4)
            {
                var alpha = random.Next(1, 256);

                pixels[i] = (byte)random.Next(0, alpha + 1);
                pixels[i + 1] = (byte)random.Next(0, alpha + 1);
                pixels[i + 2] = (byte)random.Next(0, alpha + 1);
                pixels[i + 3] = (byte)alpha;
            }

            return new GlyphMask(pixels, width, height, -random.Next(0, 4), -random.Next(0, 20), channels: 4);
        }

        private static long TallPageBytes(int tall) => (long)GlyphMaskAtlas.PageWidth * ((tall + 1 + 63) / 64 * 64);

        private static GlyphMaskKey Key(int glyph)
            => new((ushort)glyph, 160, 0, GlyphMaskMode.Antialiased, GridFit: false,
                Transform: new GlyphMaskTransform(0, 100, -100, 0));

        internal static GlyphMask CreateMask(Random random, int width, int height)
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
