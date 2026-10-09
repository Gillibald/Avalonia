using System;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization
{
    /// <summary>
    /// One limit bounds every glyph cache together. A frame trims what earlier frames drew,
    /// oldest and cheapest to rebuild first; what the current frame draws stays, and what each
    /// window drew in its previous frame stays up to a quarter over the limit. Each test uses a
    /// budget of its own.
    /// </summary>
    public class GlyphCacheLimitTests
    {
        private const string NotoMonoUri =
            "resm:Avalonia.Base.UnitTests.Assets.NotoMono-Regular.ttf?assembly=Avalonia.Base.UnitTests";

        private const int Kb = 1024;

        private static GlyphMask MakeMask(int side) => new(new byte[side * side], side, side, 0, 0);

        private static GlyphMaskKey Key(int glyph, int scale = 128)
            => new((ushort)glyph, (ushort)scale, 0, GlyphMaskMode.Antialiased);

        private static void Frames(GlyphCacheBudget budget, int count)
        {
            for (var i = 0; i < count; i++)
            {
                using (budget.BeginFrame())
                {
                }
            }
        }

        [Fact]
        public void Glyph_Masks_Of_All_Typefaces_Stay_Within_One_Global_Limit()
        {
            const long limit = 1024 * Kb;
            var budget = new GlyphCacheBudget(limit);
            var faces = new[] { new GlyphMaskCache(budget), new GlyphMaskCache(budget), new GlyphMaskCache(budget) };

            // Each face draws 40 new glyphs a frame; over the frames each face alone draws more
            // than the whole limit.
            for (var frame = 0; frame < 30; frame++)
            {
                using (budget.BeginFrame())
                {
                    if (frame > 0)
                    {
                        Assert.True(budget.UsedBytes <= limit,
                            $"frame {frame} began with {budget.UsedBytes} bytes over the limit of {limit}");
                    }

                    foreach (var face in faces)
                    {
                        for (var i = 0; i < 40; i++)
                        {
                            face.GetOrBuild(Key(frame * 40 + i), static _ => MakeMask(32));
                        }
                    }
                }
            }

            Frames(budget, 1);

            Assert.True(budget.UsedBytes <= limit, $"{budget.UsedBytes} bytes over the limit of {limit}");
            Assert.All(faces, face => Assert.True(face.TotalCost > 0, "a face lost all its masks"));
        }

        [Fact]
        public void A_Zoom_Revisit_Builds_No_Mask_When_The_Sweep_Fits_The_Global_Limit()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var cache = new GlyphMaskCache(budget);
            var builds = 0;

            GlyphMask Build(GlyphMaskKey key)
            {
                builds++;
                return MakeMask(40);
            }

            // A zoom through 120 scales of 60 glyphs: about 12 MB of masks, more than a typeface
            // could keep on its own before, well within the global limit.
            void Sweep()
            {
                for (var scale = 0; scale < 120; scale++)
                {
                    using (budget.BeginFrame())
                    {
                        for (var glyph = 0; glyph < 60; glyph++)
                        {
                            cache.GetOrBuild(Key(glyph, 100 + scale), Build);
                        }
                    }
                }
            }

            Sweep();

            Assert.True(budget.UsedBytes > 8 * 1024 * Kb, $"the sweep holds only {budget.UsedBytes} bytes");

            builds = 0;
            Sweep();

            Assert.Equal(0, builds);
        }

        [Fact]
        public void At_Equal_Age_Atlas_Pages_Go_Before_Glyph_Masks()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var cache = new GlyphMaskCache(budget);
            var atlas = new GlyphMaskAtlas(budget);

            using (budget.BeginFrame())
            {
                for (var glyph = 0; glyph < 100; glyph++)
                {
                    cache.GetOrBuild(Key(glyph), static _ => MakeMask(32));
                    Assert.True(atlas.TryAdd(Key(glyph), MakeMask(40), atlas.Tick(), out _));
                }
            }

            Frames(budget, 2);

            var masks = cache.TotalCost;

            // Room for the masks and a little more: one pool has to go.
            budget.SetLimit(masks + 16 * Kb);
            Frames(budget, 1);

            Assert.Equal(masks, cache.TotalCost);
            Assert.Equal(0, atlas.AllocatedBytes);
            Assert.True(budget.UsedBytes <= budget.LimitBytes);
        }

        [Fact]
        public void An_Old_Glyph_Mask_Goes_Before_An_Atlas_Page_Drawn_Recently()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var cache = new GlyphMaskCache(budget);
            var atlas = new GlyphMaskAtlas(budget);

            using (budget.BeginFrame())
            {
                for (var glyph = 0; glyph < 100; glyph++)
                {
                    cache.GetOrBuild(Key(glyph), static _ => MakeMask(32));
                    Assert.True(atlas.TryAdd(Key(glyph), MakeMask(40), atlas.Tick(), out _));
                }
            }

            Frames(budget, 48);

            // Fifty frames later the atlas is drawn again; the masks are not.
            using (budget.BeginFrame())
            {
                Assert.True(atlas.TryGet(Key(1), atlas.Tick(), out _));
            }

            Frames(budget, 48);

            var pages = atlas.AllocatedBytes;

            budget.SetLimit(pages + 16 * Kb);
            Frames(budget, 1);

            Assert.Equal(pages, atlas.AllocatedBytes);
            Assert.True(cache.TotalCost <= 16 * Kb, $"{cache.TotalCost} bytes of old masks were kept");
        }

        [Fact]
        public void The_Previous_Frame_Is_Kept_Up_To_A_Quarter_Over_The_Limit()
        {
            const long limit = 1024 * Kb;
            var mask = MakeMask(32).ByteCost;
            var budget = new GlyphCacheBudget(limit);
            var cache = new GlyphMaskCache(budget);

            // A frame drawing a fifth more than the limit keeps it while the next frame begins.
            using (budget.BeginFrame())
            {
                for (var glyph = 0; glyph < limit * 6 / 5 / mask; glyph++)
                {
                    cache.GetOrBuild(Key(glyph), static _ => MakeMask(32));
                }
            }

            var drawn = budget.UsedBytes;

            Frames(budget, 1);

            Assert.Equal(drawn, budget.UsedBytes);

            // Once a frame has passed without drawing it, it is trimmed to the limit.
            Frames(budget, 1);

            Assert.True(budget.UsedBytes <= limit, $"{budget.UsedBytes} bytes over the limit of {limit}");
        }

        [Fact]
        public void The_Previous_Frame_Is_Trimmed_To_A_Quarter_Over_The_Limit()
        {
            const long limit = 1024 * Kb;
            var mask = MakeMask(32).ByteCost;
            var budget = new GlyphCacheBudget(limit);
            var cache = new GlyphMaskCache(budget);

            using (budget.BeginFrame())
            {
                for (var glyph = 0; glyph < limit * 7 / 5 / mask; glyph++)
                {
                    cache.GetOrBuild(Key(glyph), static _ => MakeMask(32));
                }
            }

            Frames(budget, 1);

            Assert.True(budget.UsedBytes <= limit * 5 / 4, $"{budget.UsedBytes} bytes over a quarter past the limit");
        }

        [Fact]
        public void A_Frame_Past_Half_Over_The_Limit_Evicts_Earlier_Frames_Of_Its_Own_Pool()
        {
            const long limit = 1024 * Kb;
            var mask = MakeMask(32).ByteCost;
            var budget = new GlyphCacheBudget(limit);
            var cache = new GlyphMaskCache(budget);
            var glyph = 0;

            using (budget.BeginFrame())
            {
                while (budget.UsedBytes + mask <= limit)
                {
                    cache.GetOrBuild(Key(glyph++), static _ => MakeMask(32));
                }
            }

            using (budget.BeginFrame())
            {
                for (var i = 0; i < limit * 3 / 5 / mask; i++)
                {
                    cache.GetOrBuild(Key(glyph++), static _ => MakeMask(32));

                    Assert.True(budget.UsedBytes <= limit * 3 / 2 + mask,
                        $"{budget.UsedBytes} bytes in the frame, past half over the limit");
                }
            }
        }

        [Fact]
        public void A_Typeface_Drawn_Every_Second_Keeps_Its_Masks_While_Another_Streams_New_Ones()
        {
            const long limit = 1024 * Kb;
            var budget = new GlyphCacheBudget(limit);
            var tooltip = new GlyphMaskCache(budget);
            var zoom = new GlyphMaskCache(budget);
            var rebuilds = 0;

            GlyphMask BuildTooltip(GlyphMaskKey key)
            {
                rebuilds++;
                return MakeMask(32);
            }

            // A tooltip of 60 glyphs, far below an eighth of the limit, shown once a second while
            // a zoom draws 40 new glyphs every frame, so the zoom's masks of the last 25 frames
            // fill the limit.
            for (var frame = 0; frame < 300; frame++)
            {
                using (budget.BeginFrame())
                {
                    if (frame % 60 == 0)
                    {
                        for (var glyph = 0; glyph < 60; glyph++)
                        {
                            tooltip.GetOrBuild(Key(glyph), BuildTooltip);
                        }
                    }

                    for (var i = 0; i < 40; i++)
                    {
                        zoom.GetOrBuild(Key(frame * 40 + i), static _ => MakeMask(32));
                    }
                }
            }

            Assert.Equal(60, rebuilds);
            Assert.True(budget.UsedBytes <= budget.SoftLimitBytes, $"{budget.UsedBytes} bytes over the limit");
        }

        [Fact]
        public void Secondary_Run_Masks_Of_Old_Frames_Are_Evicted_And_The_Primary_Kept()
        {
            const long limit = 10 * Kb;
            var budget = new GlyphCacheBudget(limit);
            var cache = new RunMaskCache(budget);

            using (budget.BeginFrame())
            {
                for (ushort scale = 1; scale <= 4; scale++)
                {
                    cache.Add(RunKey(scale), RunMaskOf(4 * Kb));
                }
            }

            Frames(budget, 2);

            Assert.True(budget.UsedBytes <= limit, $"{budget.UsedBytes} bytes over the limit of {limit}");
            Assert.True(cache.TryGet(RunKey(1), out _), "the primary run mask was evicted");
        }

        [Fact]
        public void Secondary_Sprite_Sets_Of_Old_Frames_Are_Evicted_And_The_Primary_Kept()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var state = new TransformedRunState(budget);
            var sets = new TransformedGlyphSprites[4];

            using (budget.BeginFrame())
            {
                for (var i = 0; i < sets.Length; i++)
                {
                    sets[i] = new TransformedGlyphSprites(RunKey((ushort)(i + 1)), 0, false, new TransformedSprite[200]);
                    state.Add(sets[i]);
                }
            }

            Frames(budget, 2);

            budget.SetLimit(sets[0].ByteCost + sets[1].ByteCost);
            Frames(budget, 1);

            Assert.True(budget.UsedBytes <= budget.LimitBytes, $"{budget.UsedBytes} bytes over the limit");
            Assert.True(state.TryGet(RunKey(1), out var primary) && !primary.IsDisposed,
                "the primary sprite set was evicted");
        }

        [Fact]
        public void Hinter_Size_States_Of_A_Zoom_Stay_Within_The_Limit()
        {
            var typeface = SyntheticFont.FromAsset(NotoMonoUri).CreateGlyphTypeface();
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);

            typeface.CacheBudget = budget;
            budget.SetLimit(4 * typeface.TrueTypeHinterBytes);

            // A zoom asks for a new size every frame.
            for (var size = 8f; size < 48f; size += 1f)
            {
                using (budget.BeginFrame())
                {
                    Assert.True(budget.UsedBytes <= budget.LimitBytes * 5 / 4,
                        $"{budget.UsedBytes} bytes of hinters, past a quarter over the limit of {budget.LimitBytes}");

                    typeface.GetTrueTypeHinter(GlyphMaskKey.QuantizeScale(size), GlyphMaskMode.Antialiased);
                }
            }

            // The size in use stays cached.
            var current = GlyphMaskKey.QuantizeScale(47f);

            Assert.Same(typeface.GetTrueTypeHinter(current, GlyphMaskMode.Antialiased),
                typeface.GetTrueTypeHinter(current, GlyphMaskMode.Antialiased));
        }

        private static RunMaskKey RunKey(ushort scaleQ) => new(scaleQ, 0, GlyphMaskMode.Antialiased, 0);

        private static RunMask RunMaskOf(long bytes)
            => new(new[] { new RunMaskPart(new Disposable(), 0, 0, 1, 1, bytes) });

        private sealed class Disposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
