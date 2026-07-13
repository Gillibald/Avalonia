using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Fonts.Rasterization.Slug;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization.Slug
{
    public class SlugGlyphCacheTests
    {
        private static SlugGlyphData MakeData(double offset)
        {
            var sink = new SlugContourSink();

            sink.BeginFigure(new Point(offset, 0));
            sink.QuadraticBezierTo(new Point(offset + 1, 1), new Point(offset, 0));
            sink.EndFigure(true);

            var data = SlugBandEncoder.Encode(sink);

            Assert.NotNull(data);

            return data!;
        }

        [Fact]
        public void Builds_Once_And_Serves_Hits()
        {
            var cache = new SlugGlyphCache();
            var builds = 0;

            var first = cache.GetOrBuild<object?>(7, null, (g, _) =>
            {
                builds++;
                return MakeData(g);
            });

            var second = cache.GetOrBuild<object?>(7, null, (g, _) =>
            {
                builds++;
                return MakeData(g);
            });

            Assert.Equal(1, builds);
            Assert.NotNull(first);
            Assert.Same(first, second);
        }

        [Fact]
        public void Declines_Are_Memoised()
        {
            var cache = new SlugGlyphCache();
            var builds = 0;

            SlugGlyphData? Build(ushort glyph, object? _)
            {
                builds++;
                return null;
            }

            Assert.Null(cache.GetOrBuild<object?>(3, null, Build));
            Assert.Null(cache.GetOrBuild<object?>(3, null, Build));
            Assert.Equal(1, builds);

            Assert.True(cache.TryGet(3, out var peeked));
            Assert.Null(peeked);
        }

        [Fact]
        public void Eviction_Keeps_Retained_Bytes_Under_Budget()
        {
            var size = MakeData(0).RetainedBytes;
            var cache = new SlugGlyphCache(budgetBytes: size * 3);

            for (ushort glyph = 0; glyph < 6; glyph++)
            {
                cache.GetOrBuild(glyph, glyph, static (g, _) => MakeData(g));
            }

            Assert.True(cache.TotalCost <= size * 3);
            Assert.True(cache.Count <= 3);
        }

        [Fact]
        public void A_Touched_Entry_Survives_Eviction_Over_A_Stale_One()
        {
            var size = MakeData(0).RetainedBytes;
            var cache = new SlugGlyphCache(budgetBytes: size * 3);

            cache.GetOrBuild(1, 1, static (g, _) => MakeData(g));
            cache.GetOrBuild(2, 2, static (g, _) => MakeData(g));
            cache.GetOrBuild(3, 3, static (g, _) => MakeData(g));

            // The first overflow sweeps every insertion-referenced bit clear and evicts the
            // oldest entry; afterwards the surviving bits genuinely differentiate.
            cache.GetOrBuild(4, 4, static (g, _) => MakeData(g));

            Assert.False(cache.TryGet(1, out _));

            // Touch glyph 3, then overflow again: the untouched glyph 2 is the victim.
            cache.GetOrBuild(3, 3, static (g, _) => MakeData(g));
            cache.GetOrBuild(5, 5, static (g, _) => MakeData(g));

            Assert.False(cache.TryGet(2, out _));
            Assert.True(cache.TryGet(3, out var survivor));
            Assert.NotNull(survivor);
            Assert.True(cache.TryGet(4, out _));
        }

        [Fact]
        public void Racing_Builders_Agree_On_The_Published_Payload()
        {
            var cache = new SlugGlyphCache();
            var barrier = new Barrier(2);
            var builds = 0;

            SlugGlyphData? Build(ushort glyph, object? _)
            {
                // Both threads reach the build before either publishes, so both build and
                // exactly one result wins at insertion.
                barrier.SignalAndWait();
                Interlocked.Increment(ref builds);
                return MakeData(glyph);
            }

            var tasks = new[]
            {
                Task.Run(() => cache.GetOrBuild<object?>(9, null, Build)),
                Task.Run(() => cache.GetOrBuild<object?>(9, null, Build)),
            };

            Task.WaitAll(tasks);

            Assert.Equal(2, builds);
            Assert.NotNull(tasks[0].Result);
            Assert.Same(tasks[0].Result, tasks[1].Result);
            Assert.Equal(1, cache.Count);
        }

        [Fact]
        public void TryGet_Peeks_Without_Building()
        {
            var cache = new SlugGlyphCache();

            Assert.False(cache.TryGet(11, out _));
            Assert.Equal(0, cache.Count);

            cache.GetOrBuild(11, 11, static (g, _) => MakeData(g));

            Assert.True(cache.TryGet(11, out var data));
            Assert.NotNull(data);
        }
    }
}
