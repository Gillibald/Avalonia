using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization.TrueType
{
    /// <summary>
    /// Glyph masks of instructed fonts can be built on several threads at once: every thread
    /// hints the same sizes through the typeface's hinters and gets exactly the masks a single
    /// thread builds.
    /// </summary>
    public class TrueTypeConcurrentHintingTests
    {
        private const int Threads = 8;

        [Theory]
        [InlineData("NotoMono-Regular.ttf")]
        [InlineData("NotoSans-Italic.ttf")]
        public void Glyph_Masks_Built_On_Many_Threads_At_Once_Equal_Those_Of_One_Thread(string fileName)
        {
            var typeface = SyntheticFont.FromBytes(TestFontFiles.Load(fileName)).CreateGlyphTypeface();

            Assert.True(typeface.HasTrueTypeHinting, $"{fileName} carries no TrueType programs");

            var keys = Keys(typeface).ToArray();
            var expected = Array.ConvertAll(keys, key => GlyphMasks.Build(typeface, new GlyphPathBuilder(), key));

            // Every thread walks the keys from its own starting point, so threads hint
            // different glyphs of one size at the same time.
            var mismatches = new ConcurrentBag<string>();
            var start = new Barrier(Threads);
            var threads = new Thread[Threads];

            for (var t = 0; t < Threads; t++)
            {
                var offset = t * keys.Length / Threads;

                threads[t] = new Thread(() =>
                {
                    var scratch = new GlyphPathBuilder();

                    start.SignalAndWait();

                    for (var pass = 0; pass < 2; pass++)
                    {
                        for (var i = 0; i < keys.Length; i++)
                        {
                            var index = (offset + i) % keys.Length;

                            try
                            {
                                var mask = GlyphMasks.Build(typeface, scratch, keys[index]);

                                if (!SameMask(expected[index], mask))
                                {
                                    mismatches.Add(keys[index].ToString());
                                }
                            }
                            catch (Exception e)
                            {
                                mismatches.Add($"{keys[index]}: {e.GetType().Name}");
                            }
                        }
                    }
                });
                threads[t].Start();
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }

            Assert.True(mismatches.IsEmpty,
                $"{mismatches.Count} masks differ from the single-threaded build, e.g. {string.Join("; ", mismatches.Take(3))}");
        }

        /// <summary>Hinted keys: glyphs spread over the font, composites among them, at three sizes, natural and strong hinting, grayscale and subpixel.</summary>
        private static IEnumerable<GlyphMaskKey> Keys(GlyphTypeface typeface)
        {
            const int glyphs = 160;

            foreach (var pixelsPerEm in new[] { 11f, 16.5f, 23f })
            {
                var scaleQ = GlyphMaskKey.QuantizeScale(pixelsPerEm);

                foreach (var (mode, strong) in new[]
                         {
                             (GlyphMaskMode.Antialiased, false), (GlyphMaskMode.Antialiased, true),
                             (GlyphMaskMode.Subpixel, false),
                         })
                {
                    for (var glyph = 0; glyph < glyphs; glyph++)
                    {
                        yield return new GlyphMaskKey((ushort)(glyph * typeface.GlyphCount / glyphs), scaleQ, 0, mode,
                            GridFit: true, StemSnap: strong);
                    }
                }
            }
        }

        private static bool SameMask(GlyphMask a, GlyphMask b) =>
            a.Width == b.Width && a.Height == b.Height && a.Left == b.Left && a.Top == b.Top &&
            a.Channels == b.Channels && a.Alpha.AsSpan().SequenceEqual(b.Alpha);
    }
}
