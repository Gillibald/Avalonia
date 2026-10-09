using System.Threading;
using Avalonia.Media.Fonts.Rasterization;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization
{
    /// <summary>
    /// Glyph cache recency is counted in frames: what a frame draws stays cached until the frame
    /// ends, and what was drawn longest ago goes first. Each test uses a budget of its own.
    /// </summary>
    public class GlyphCacheFrameTests
    {
        private static GlyphMask MakeMask(int side = 10) => new(new byte[side * side], side, side, 0, 0);

        private static GlyphMaskKey Key(ushort glyph)
            => new(glyph, GlyphMaskKey.QuantizeScale(16f), 0, GlyphMaskMode.Antialiased);

        [Fact]
        public void A_Frame_Begun_Inside_Another_On_The_Same_Thread_Does_Not_Advance_The_Clock()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var before = budget.Frame;

            using (budget.BeginFrame())
            {
                var outer = budget.CurrentThreadFrame;

                Assert.Equal(before + 1, outer);
                Assert.Equal(outer, budget.Frame);

                using (budget.BeginFrame())
                {
                    Assert.Equal(outer, budget.Frame);
                    Assert.Equal(outer, budget.CurrentThreadFrame);
                }

                Assert.Equal(outer, budget.CurrentThreadFrame);
            }

            Assert.Equal(0, budget.CurrentThreadFrame);

            using (budget.BeginFrame())
            {
                Assert.Equal(before + 2, budget.Frame);
            }
        }

        [Fact]
        public void Frames_Of_Two_Threads_Each_Advance_The_Clock()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);

            using (budget.BeginFrame())
            {
                var mine = budget.CurrentThreadFrame;
                long other = 0;
                var thread = new Thread(() =>
                {
                    using (budget.BeginFrame())
                    {
                        other = budget.CurrentThreadFrame;
                    }
                });

                thread.Start();
                thread.Join();

                Assert.Equal(mine + 1, other);
                Assert.Equal(mine, budget.CurrentThreadFrame);
            }
        }

        [Fact]
        public void A_Mask_Used_In_The_Current_Frame_Survives_Eviction()
        {
            var cost = MakeMask().ByteCost;
            var budget = new GlyphCacheBudget(cost * 3);
            var cache = new GlyphMaskCache(budget);

            using (budget.BeginFrame())
            {
                for (ushort glyph = 1; glyph <= 6; glyph++)
                {
                    cache.GetOrBuild(Key(glyph), static _ => MakeMask());
                }

                // The frame draws all six, so the cache holds them past its budget.
                for (ushort glyph = 1; glyph <= 6; glyph++)
                {
                    Assert.True(cache.TryGet(Key(glyph), out _), $"glyph {glyph} was evicted in the frame that drew it");
                }
            }

            using (budget.BeginFrame())
            {
                cache.GetOrBuild(Key(7), static _ => MakeMask());
            }

            // The next frame trims to the limit.
            using (budget.BeginFrame())
            {
            }

            Assert.True(cache.TotalCost <= cost * 3, $"TotalCost {cache.TotalCost} exceeds the budget {cost * 3}");
            Assert.True(cache.TryGet(Key(7), out _));
        }

        [Fact]
        public void The_Mask_Drawn_Longest_Ago_Is_Evicted_First()
        {
            var cost = MakeMask().ByteCost;
            var budget = new GlyphCacheBudget(cost * 3);
            var cache = new GlyphMaskCache(budget);

            using (budget.BeginFrame())
            {
                for (ushort glyph = 1; glyph <= 3; glyph++)
                {
                    cache.GetOrBuild(Key(glyph), static _ => MakeMask());
                }
            }

            using (budget.BeginFrame())
            {
                cache.GetOrBuild(Key(1), static _ => throw new System.InvalidOperationException("must be a hit"));
            }

            using (budget.BeginFrame())
            {
                cache.GetOrBuild(Key(4), static _ => MakeMask());
            }

            using (budget.BeginFrame())
            {
            }

            Assert.True(cache.TryGet(Key(1), out _), "the mask drawn a frame ago was evicted");
            Assert.False(cache.TryGet(Key(2), out _), "the mask drawn longest ago survived");
            Assert.True(cache.TryGet(Key(3), out _));
            Assert.True(cache.TryGet(Key(4), out _));
        }
    }
}
