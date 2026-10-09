using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization
{
    /// <summary>
    /// Every glyph cache charges the bytes it holds to one budget, so the budget knows what all
    /// typefaces and cache kinds hold together. Each test uses a budget of its own.
    /// </summary>
    public class GlyphCacheBudgetTests
    {
        private const string NotoMonoUri =
            "resm:Avalonia.Base.UnitTests.Assets.NotoMono-Regular.ttf?assembly=Avalonia.Base.UnitTests";

        private static GlyphMask MakeMask(int side) => new(new byte[side * side], side, side, 0, 0);

        private static GlyphMaskKey Key(ushort glyph, float ppem = 16f)
            => new(glyph, GlyphMaskKey.QuantizeScale(ppem), 0, GlyphMaskMode.Antialiased);

        [Fact]
        public void Concurrent_Builds_Keep_The_Charged_Bytes_Exact()
        {
            var budget = new GlyphCacheBudget(256 * 1024);
            var cache = new GlyphMaskCache(budget);
            const int threads = 8;
            using var start = new Barrier(threads);

            // Eight threads build the masks of one face in different orders, each in frames of
            // its own, over a window of glyphs that moves with the frames: equal keys race (the
            // losing build is discarded) while inserts evict what earlier frames drew.
            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, thread =>
            {
                var random = new Random(thread);

                start.SignalAndWait(TimeSpan.FromSeconds(10));

                for (var frame = 0; frame < 40; frame++)
                {
                    using (budget.BeginFrame())
                    {
                        for (var i = 0; i < 100; i++)
                        {
                            var glyph = (ushort)((frame * 37 + random.Next(150)) % 1500);

                            cache.GetOrBuild(Key(glyph), static key => MakeMask(4 + key.Glyph % 29));
                        }
                    }
                }
            });

            Assert.True(cache.Evictions > 0, "no mask was evicted, so no credit raced a charge");
            Assert.Equal(cache.TotalCost, budget.UsedBytes);
            Assert.Equal(budget.UsedBytes, budget.BytesOf(GlyphCachePoolKind.Masks));

            cache.Clear();

            Assert.Equal(0, budget.UsedBytes);
        }

        [Fact]
        public void Outline_Geometry_Is_Charged_Until_It_Is_Evicted()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var cache = new GlyphCache(budgetBytes: 150, budget: budget);

            cache.GetOrBuildGeometry(cache.GetEntry(1), static _ => Geometry(100));

            Assert.Equal(100, budget.BytesOf(GlyphCachePoolKind.Outlines));

            // Over the cache's own cap: the first geometry goes.
            cache.GetOrBuildGeometry(cache.GetEntry(2), static _ => Geometry(100));

            Assert.Equal(cache.TotalCost, budget.BytesOf(GlyphCachePoolKind.Outlines));
            Assert.Equal(100, budget.UsedBytes);
        }

        [Fact]
        public void Atlas_Pages_Are_Charged_As_They_Are_Allocated()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var atlas = new GlyphMaskAtlas(budget);
            var tick = atlas.Tick();

            for (ushort glyph = 1; glyph < 200; glyph++)
            {
                Assert.True(atlas.TryAdd(Key(glyph), MakeMask(40), tick, out _));
            }

            Assert.True(atlas.AllocatedBytes > 0);
            Assert.Equal(atlas.AllocatedBytes, budget.BytesOf(GlyphCachePoolKind.Atlas));
            Assert.Equal(atlas.AllocatedBytes, budget.UsedBytes);
        }

        [Fact]
        public void Lcd_Atlas_Pages_Are_Charged_Until_Their_Entries_Are_Released()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var atlas = new LcdRunAtlas(budget);
            var entry = atlas.TryAdd(new byte[30 * 10 * 4], 30, 10);

            Assert.NotNull(entry);
            Assert.True(atlas.AllocatedBytes > 0);
            Assert.Equal(atlas.AllocatedBytes, budget.BytesOf(GlyphCachePoolKind.LcdAtlas));

            entry!.Dispose();

            Assert.Equal(0, atlas.AllocatedBytes);
            Assert.Equal(0, budget.UsedBytes);
        }

        [Fact]
        public void Run_Masks_Are_Charged_Until_Replaced_Or_Disposed()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var cache = new RunMaskCache(budget);

            cache.Add(RunKey(1), RunMaskOf(1000));
            cache.Add(RunKey(2), RunMaskOf(200));

            Assert.Equal(1200, budget.BytesOf(GlyphCachePoolKind.RunMasks));

            // The secondary ring holds three; the fourth secondary replaces the first.
            cache.Add(RunKey(3), RunMaskOf(300));
            cache.Add(RunKey(4), RunMaskOf(400));
            cache.Add(RunKey(5), RunMaskOf(500));

            Assert.Equal(1000 + 300 + 400 + 500, budget.UsedBytes);

            cache.Remove(RunKey(1));

            Assert.Equal(300 + 400 + 500, budget.UsedBytes);

            cache.Dispose();

            Assert.Equal(0, budget.UsedBytes);
        }

        [Fact]
        public void Sprite_Sets_Are_Charged_Until_Disposed()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var state = new TransformedRunState(budget);
            var first = SpritesOf(10, scaleQ: 100);
            var second = SpritesOf(25, scaleQ: 101);

            state.Add(first);
            state.Add(second);

            Assert.Equal(first.ByteCost + second.ByteCost, budget.BytesOf(GlyphCachePoolKind.SpriteSets));

            first.Masks = new GlyphMask[first.Count];

            Assert.Equal(first.ByteCost + second.ByteCost, budget.UsedBytes);

            state.Dispose();

            Assert.Equal(0, budget.UsedBytes);
        }

        [Fact]
        public void Hinter_Size_States_Are_Charged_While_Kept()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var typeface = SyntheticFont.FromAsset(NotoMonoUri).CreateGlyphTypeface();

            typeface.CacheBudget = budget;

            Assert.True(typeface.HasTrueTypeHinting);

            for (var size = 8f; size < 40f; size += 1f)
            {
                typeface.GetTrueTypeHinter(GlyphMaskKey.QuantizeScale(size), GlyphMaskMode.Antialiased);
            }

            Assert.True(typeface.TrueTypeHinterBytes > 4096);
            Assert.Equal(typeface.TrueTypeHinterCount * typeface.TrueTypeHinterBytes,
                budget.BytesOf(GlyphCachePoolKind.Hinters));
        }

        [Fact]
        public void A_Pool_The_Gc_Collected_Is_Credited()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);

            ChargeAndDrop(budget);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            budget.SweepCollectedPools();

            Assert.Equal(0, budget.UsedBytes);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ChargeAndDrop(GlyphCacheBudget budget)
        {
            var cache = new GlyphMaskCache(budget);

            cache.GetOrBuild(Key(1), static _ => MakeMask(20));

            Assert.True(budget.UsedBytes > 0);
        }

        private static BuiltGeometry Geometry(int cost)
            => new(new object(), cost, GlyphPayloadKind.Outline, Array.Empty<ushort>(), default, false);

        private static RunMaskKey RunKey(ushort scaleQ) => new(scaleQ, 0, GlyphMaskMode.Antialiased, 0);

        private static RunMask RunMaskOf(long bytes)
            => new(new[] { new RunMaskPart(new Disposable(), 0, 0, 1, 1, bytes) });

        private static TransformedGlyphSprites SpritesOf(int count, ushort scaleQ)
            => new(new RunMaskKey(scaleQ, 0, GlyphMaskMode.Antialiased, 0), 0, false, new TransformedSprite[count]);

        private sealed class Disposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
