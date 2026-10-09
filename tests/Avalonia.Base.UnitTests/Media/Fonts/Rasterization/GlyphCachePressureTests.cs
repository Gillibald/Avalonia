using System;
using System.Threading;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization
{
    /// <summary>
    /// Under memory pressure the glyph caches give back everything the frames being drawn do not
    /// use. Each test uses a budget of its own.
    /// </summary>
    public class GlyphCachePressureTests
    {
        private const string NotoMonoUri =
            "resm:Avalonia.Base.UnitTests.Assets.NotoMono-Regular.ttf?assembly=Avalonia.Base.UnitTests";

        private static GlyphMask MakeMask(int side) => new(new byte[side * side], side, side, 0, 0);

        private static GlyphMaskKey Key(int glyph) => new((ushort)glyph, 128, 0, GlyphMaskMode.Antialiased);

        private static RunMaskKey RunKey(ushort scaleQ) => new(scaleQ, 0, GlyphMaskMode.Antialiased, 0);

        [Fact]
        public void Memory_Pressure_Drops_Everything_No_Open_Frame_Uses()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var typeface = SyntheticFont.FromAsset(NotoMonoUri).CreateGlyphTypeface();
            var masks = new GlyphMaskCache(budget);
            var atlas = new GlyphMaskAtlas(budget);
            var runMasks = new RunMaskCache(budget);
            var sprites = new TransformedRunState(budget);

            typeface.CacheBudget = budget;

            using (budget.BeginFrame())
            {
                for (var glyph = 0; glyph < 100; glyph++)
                {
                    masks.GetOrBuild(Key(glyph), static _ => MakeMask(24));
                    Assert.True(atlas.TryAdd(Key(glyph), MakeMask(24), atlas.Tick(), out _));
                }

                for (ushort scale = 1; scale <= 3; scale++)
                {
                    runMasks.Add(RunKey(scale), new RunMask(new[] { new RunMaskPart(new Disposable(), 0, 0, 1, 1, 1000) }));
                    sprites.Add(new TransformedGlyphSprites(RunKey(scale), 0, false, new TransformedSprite[50]));
                }

                typeface.GetTrueTypeHinter(GlyphMaskKey.QuantizeScale(15f), GlyphMaskMode.Antialiased);
            }

            Assert.True(sprites.TryGet(RunKey(1), out var primarySprites));

            budget.TrimForMemoryPressure();

            Assert.Equal(0, budget.BytesOf(GlyphCachePoolKind.Masks));
            Assert.Equal(0, budget.BytesOf(GlyphCachePoolKind.Atlas));
            Assert.Equal(0, budget.BytesOf(GlyphCachePoolKind.Hinters));

            // A run's primary state stays with the run.
            Assert.Equal(1000, budget.BytesOf(GlyphCachePoolKind.RunMasks));
            Assert.Equal(primarySprites.ByteCost, budget.BytesOf(GlyphCachePoolKind.SpriteSets));
        }

        [Fact]
        public void Memory_Pressure_Keeps_What_An_Open_Frame_Draws()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var masks = new GlyphMaskCache(budget);
            var atlas = new GlyphMaskAtlas(budget);

            using (budget.BeginFrame())
            {
                for (var glyph = 0; glyph < 100; glyph++)
                {
                    masks.GetOrBuild(Key(glyph), static _ => MakeMask(24));
                    Assert.True(atlas.TryAdd(Key(glyph), MakeMask(24), atlas.Tick(), out _));
                }
            }

            using var drawn = new ManualResetEventSlim();
            using var trimmed = new ManualResetEventSlim();

            // Another thread draws a frame that uses ten of the masks while the pressure arrives.
            var renderThread = new Thread(() =>
            {
                using (budget.BeginFrame())
                {
                    for (var glyph = 0; glyph < 10; glyph++)
                    {
                        masks.GetOrBuild(Key(glyph), static _ => throw new InvalidOperationException("must be a hit"));
                    }

                    drawn.Set();
                    trimmed.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                }
            });

            renderThread.Start();
            Assert.True(drawn.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

            budget.TrimForMemoryPressure();

            Assert.Equal(10, masks.Count);

            // Atlas pages may be drawn by the open frame, so they go when the next frame begins.
            Assert.True(atlas.AllocatedBytes > 0);

            trimmed.Set();
            renderThread.Join();

            using (budget.BeginFrame())
            {
                Assert.Equal(0, atlas.AllocatedBytes);
            }
        }

        private sealed class Disposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
