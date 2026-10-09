using System;
using System.Collections.Generic;
using Avalonia.Media.Fonts.Rasterization;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization
{
    /// <summary>
    /// Content that stops being drawn ages out to the retain target: at the start of a frame once
    /// it is older than the idle period, and once after the last frame when frames stop. Each
    /// test uses a budget of its own.
    /// </summary>
    public class GlyphCacheIdleTests
    {
        private const long Kb = 1024;

        private static GlyphMask MakeMask(int side) => new(new byte[side * side], side, side, 0, 0);

        private static GlyphMaskKey Key(int glyph)
            => new((ushort)glyph, 128, 0, GlyphMaskMode.Antialiased);

        /// <summary>Fills <paramref name="cache"/> in one frame with about <paramref name="bytes"/> of masks.</summary>
        private static void Fill(GlyphCacheBudget budget, GlyphMaskCache cache, long bytes, int firstGlyph = 0)
        {
            using (budget.BeginFrame())
            {
                for (var glyph = firstGlyph; budget.UsedBytes < bytes; glyph++)
                {
                    cache.GetOrBuild(Key(glyph), static _ => MakeMask(32));
                }
            }
        }

        [Fact]
        public void Content_Not_Drawn_For_The_Idle_Period_Is_Trimmed_To_The_Retain_Target()
        {
            var budget = new GlyphCacheBudget(1024 * Kb);
            var zoom = new GlyphMaskCache(budget);
            var label = new GlyphMaskCache(budget);

            // A finished zoom leaves most of the limit filled; a label is drawn every frame.
            Fill(budget, zoom, 900 * Kb);

            for (var frame = 0; frame < GlyphCacheBudget.IdleFrames + 2; frame++)
            {
                using (budget.BeginFrame())
                {
                    if (frame < GlyphCacheBudget.IdleFrames - 1)
                    {
                        Assert.True(budget.UsedBytes > 900 * Kb, $"frame {frame} trimmed content that was not idle yet");
                    }

                    for (var glyph = 0; glyph < 20; glyph++)
                    {
                        label.GetOrBuild(Key(glyph), static _ => MakeMask(32));
                    }
                }
            }

            Assert.True(budget.UsedBytes <= budget.RetainBytes,
                $"{budget.UsedBytes} bytes over the retain target of {budget.RetainBytes}");
            Assert.Equal(20, label.Count);
        }

        [Fact]
        public void An_Idle_App_Trims_Once_After_Its_Last_Frame()
        {
            var timer = new ManualTimer();
            var now = TimeSpan.Zero;
            var budget = new GlyphCacheBudget(1024 * Kb, timer, () => now);
            var zoom = new GlyphMaskCache(budget);
            var label = new GlyphMaskCache(budget);

            Fill(budget, zoom, 900 * Kb);

            void DrawLabel()
            {
                using (budget.BeginFrame())
                {
                    for (var glyph = 0; glyph < 20; glyph++)
                    {
                        label.GetOrBuild(Key(glyph), static _ => MakeMask(32));
                    }
                }
            }

            // Two frames of the label after the zoom, then nothing changes on screen.
            DrawLabel();
            now += TimeSpan.FromMilliseconds(16);
            DrawLabel();

            Assert.Single(timer.Scheduled);
            Assert.True(budget.UsedBytes > budget.RetainBytes);

            // A second later the timer finds frames stopped only a second ago and waits on.
            now += TimeSpan.FromSeconds(1);
            timer.RunNext();

            Assert.Single(timer.Scheduled);
            Assert.True(budget.UsedBytes > budget.RetainBytes);

            now += TimeSpan.FromSeconds(1.5);
            timer.RunNext();

            Assert.True(budget.UsedBytes <= budget.RetainBytes,
                $"{budget.UsedBytes} bytes over the retain target of {budget.RetainBytes}");
            Assert.Equal(20, label.Count);
            Assert.Empty(timer.Scheduled);

            // Within the retain target, frames arm no timer.
            DrawLabel();

            Assert.Empty(timer.Scheduled);
        }

        private sealed class ManualTimer : IGlyphCacheIdleTimer
        {
            public List<Action> Scheduled { get; } = new();

            public void Schedule(TimeSpan delay, Action callback) => Scheduled.Add(callback);

            public void RunNext()
            {
                var callback = Scheduled[0];

                Scheduled.RemoveAt(0);
                callback();
            }
        }
    }
}
