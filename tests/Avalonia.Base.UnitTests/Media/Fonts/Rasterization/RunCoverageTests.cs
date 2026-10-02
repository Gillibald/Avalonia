using System;
using System.Collections.Generic;
using Avalonia.Media.Fonts.Rasterization;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization
{
    public class RunCoverageTests
    {
        private static readonly RunMaskKey s_key = new(GlyphMaskKey.QuantizeScale(14), 0, GlyphMaskMode.Antialiased, 0);

        [Fact]
        public void Coverage_Equals_The_Pixel_By_Pixel_Build_For_Masks_Of_Every_Width_And_Overlap()
        {
            var random = new Random(2024);

            for (var trial = 0; trial < 400; trial++)
            {
                var count = random.Next(1, 12);
                var masks = new GlyphMask[count];
                var penX = new int[count];
                var penY = new int[count];

                for (var i = 0; i < count; i++)
                {
                    masks[i] = RandomMask(random, random.Next(1, 40), random.Next(1, 24));
                    penX[i] = i * random.Next(0, 12) + random.Next(-3, 4);
                    penY[i] = random.Next(-4, 5);
                }

                AssertSameAsReference(masks, penX, penY, $"trial {trial}");
            }
        }

        [Fact]
        public void A_Pixel_Inked_By_More_Glyphs_Than_A_Byte_Counts_Refuses_The_Run()
        {
            var alpha = new byte[3 * 2];

            alpha.AsSpan().Fill(9);

            var masks = new GlyphMask[256];
            var pens = new int[masks.Length];

            for (var i = 0; i < masks.Length; i++)
            {
                masks[i] = new GlyphMask(alpha, 3, 2, 0, 0);
            }

            Assert.False(RunCoverage.TryBuild(s_key, masks, pens, pens, out _));
            Assert.True(RunCoverage.TryBuild(s_key, masks.AsSpan(0, 255), pens.AsSpan(0, 255), pens.AsSpan(0, 255),
                out var coverage));
            Assert.Equal(6, coverage!.OverlapPixels.Length);
            Assert.Equal(6 * 255, coverage.OverlapCoverage.Length);
        }

        [Fact]
        public void Masks_Without_Ink_Build_No_Coverage()
        {
            var masks = new[] { GlyphMask.Empty, new GlyphMask(new byte[20], 5, 4, 0, 0), GlyphMask.Empty };
            var pens = new int[masks.Length];

            Assert.True(RunCoverage.TryBuild(s_key, masks, pens, pens, out var coverage));

            // An all-zero mask still spans the union, without any inked pixel.
            Assert.NotNull(coverage);
            Assert.All(coverage!.Coverage, value => Assert.Equal(0, value));
            Assert.Empty(coverage.OverlapPixels);
        }

        private static void AssertSameAsReference(GlyphMask[] masks, int[] penX, int[] penY, string label)
        {
            var built = RunCoverage.TryBuild(s_key, masks, penX, penY, out var coverage);
            var expected = Reference(masks, penX, penY);

            Assert.True(built, label);
            Assert.NotNull(coverage);
            Assert.Equal(expected.OffsetX, coverage!.OffsetX);
            Assert.Equal(expected.OffsetY, coverage.OffsetY);
            Assert.Equal(expected.Width, coverage.Width);
            Assert.Equal(expected.Height, coverage.Height);
            Assert.True(expected.Single.AsSpan().SequenceEqual(coverage.Coverage), label + ": coverage");
            Assert.Equal(expected.Pixels, coverage.OverlapPixels);
            Assert.Equal(expected.Starts, coverage.OverlapStarts);
            Assert.Equal(expected.Stacked, coverage.OverlapCoverage);
        }

        private static GlyphMask RandomMask(Random random, int width, int height)
        {
            var alpha = new byte[width * height];

            for (var i = 0; i < alpha.Length;)
            {
                var span = random.Next(1, 20);
                var kind = random.Next(3);

                for (var j = 0; j < span && i < alpha.Length; j++, i++)
                {
                    alpha[i] = kind switch
                    {
                        0 => 0,
                        1 => 255,
                        _ => (byte)random.Next(256),
                    };
                }
            }

            return new GlyphMask(alpha, width, height, -random.Next(0, 3), -height + random.Next(0, 4));
        }

        private sealed record Expected(int OffsetX, int OffsetY, int Width, int Height, byte[] Single, int[] Pixels,
            int[] Starts, byte[] Stacked);

        /// <summary>The coverage a pixel at a time: count, keep the single glyph's byte, stack overlaps.</summary>
        private static Expected Reference(GlyphMask[] masks, int[] penX, int[] penY)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

            for (var i = 0; i < masks.Length; i++)
            {
                if (masks[i].IsEmpty)
                {
                    continue;
                }

                minX = Math.Min(minX, penX[i] + masks[i].Left);
                minY = Math.Min(minY, penY[i] + masks[i].Top);
                maxX = Math.Max(maxX, penX[i] + masks[i].Left + masks[i].Width);
                maxY = Math.Max(maxY, penY[i] + masks[i].Top + masks[i].Height);
            }

            var width = maxX - minX;
            var height = maxY - minY;
            var stacks = new List<byte>[width * height];

            for (var i = 0; i < masks.Length; i++)
            {
                var mask = masks[i];

                for (var row = 0; row < mask.Height; row++)
                {
                    for (var column = 0; column < mask.Width; column++)
                    {
                        var value = mask.Alpha[row * mask.Width + column];

                        if (value == 0)
                        {
                            continue;
                        }

                        var index = (penY[i] + mask.Top - minY + row) * width + penX[i] + mask.Left - minX + column;

                        (stacks[index] ??= new List<byte>()).Add(value);
                    }
                }
            }

            var single = new byte[width * height];
            var pixels = new List<int>();
            var starts = new List<int>();
            var stacked = new List<byte>();

            for (var index = 0; index < stacks.Length; index++)
            {
                if (stacks[index] is not { } stack)
                {
                    continue;
                }

                if (stack.Count == 1)
                {
                    single[index] = stack[0];
                    continue;
                }

                pixels.Add(index);
                starts.Add(stacked.Count);
                stacked.AddRange(stack);
            }

            starts.Add(stacked.Count);

            return new Expected(minX, minY, width, height, single, pixels.ToArray(), starts.ToArray(),
                stacked.ToArray());
        }
    }
}
