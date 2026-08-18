using System;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Fonts.Rasterization.TrueType;
using Avalonia.Media.Fonts.Tables;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization.TrueType
{
    /// <summary>
    /// Hostile-input soaks: random and bit-flipped instruction streams, program tables and
    /// glyph records must always terminate as rendered-or-vetoed - never throw, never hang,
    /// and never leak state into the pristine size snapshots. The committed runs use a
    /// fixed seed so failures reproduce; set TRUETYPE_FUZZ=&lt;iterations&gt; for a longer
    /// soak with fresh randomness per run.
    /// </summary>
    public class TrueTypeFuzzTests
    {
        private const int CommittedIterations = 150;
        private const int Seed = 20260817;

        private static int SoakIterations =>
            int.TryParse(Environment.GetEnvironmentVariable("TRUETYPE_FUZZ"), out var n) ? n : 0;

        [Fact]
        public void Arbitrary_Glyph_Streams_Terminate_Without_Throwing()
        {
            FuzzGlyphStreams(new Random(Seed), CommittedIterations);
        }

        [Fact]
        public void Mutated_Program_Tables_Terminate_Without_Throwing()
        {
            FuzzProgramTables(new Random(Seed), CommittedIterations);
        }

        [Fact]
        public void Mutated_Glyph_Records_Terminate_Without_Throwing()
        {
            FuzzGlyphRecords(new Random(Seed), CommittedIterations);
        }

        [Fact]
        public void Extended_Soak_When_Requested()
        {
            var iterations = SoakIterations;

            if (iterations <= 0)
            {
                return;
            }

            var rng = new Random();

            FuzzGlyphStreams(rng, iterations);
            FuzzProgramTables(rng, iterations);
            FuzzGlyphRecords(rng, iterations);
        }

        /// <summary>
        /// Random byte blobs and bit-flipped real streams run as glyph programs against a
        /// real zone; the pristine size state must come out untouched every time.
        /// </summary>
        private static void FuzzGlyphStreams(Random rng, int iterations)
        {
            var typeface = SyntheticFont.FromBytes(TestFontFiles.Load("NotoMono-Regular.ttf")).CreateGlyphTypeface();
            var maxp = MaxpTable.Load(typeface);

            var state = TrueTypeSizeState.Create(
                typeface.ProgramTables,
                typeface.Metrics.DesignEmHeight,
                pixelsPerEm26Dot6: 16 * 64,
                maxp.MaxStorage,
                maxp.MaxFunctionDefs,
                maxp.MaxInstructionDefs,
                maxp.MaxStackElements,
                maxp.MaxTwilightPoints,
                TrueTypeRenderClass.Grayscale,
                isVariation: false);

            Assert.True(state.IsValid);

            var pristineCvt = state.Interpreter!.PristineCvt.ToArray();
            var pristineStorage = state.Interpreter.PristineStorage.ToArray();

            var glyph = typeface.CharacterToGlyphMap['H'];

            Assert.True(typeface.TryGetGlyphMetrics(glyph, out var metrics));

            var loader = new TrueTypeGlyphLoader();

            for (var i = 0; i < iterations; i++)
            {
                Assert.True(loader.TryLoadSimple(
                    typeface.GlyfTable!, glyph, null, default,
                    metrics.XBearing, metrics.AdvanceWidth,
                    typeface.Metrics.DesignEmHeight, state.Scale));

                byte[] program;

                if (i % 3 == 0 && !loader.Instructions.IsEmpty)
                {
                    // A real stream with a few flipped bytes.
                    program = loader.Instructions.ToArray();

                    for (var flips = rng.Next(1, 5); flips > 0; flips--)
                    {
                        program[rng.Next(program.Length)] ^= (byte)(1 << rng.Next(8));
                    }
                }
                else
                {
                    program = new byte[rng.Next(1, 300)];
                    rng.NextBytes(program);
                }

                state.Interpreter.SetGlyphZone(loader.Zone);
                state.RunGlyphProgram(program, rng.Next(2) == 0 ? 0 : 4);
                state.Interpreter.SetGlyphZone(null);

                // Copy-on-write means no fuzzed run can dirty the size snapshot.
                Assert.True(state.Interpreter.PristineCvt.SequenceEqual(pristineCvt));
                Assert.True(state.Interpreter.PristineStorage.SequenceEqual(pristineStorage));
            }
        }

        /// <summary>Bit-flipped fpgm/prep/cvt tables through the full production path.</summary>
        private static void FuzzProgramTables(Random rng, int iterations)
        {
            var notoBytes = TestFontFiles.Load("NotoMono-Regular.ttf");
            string[] tables = { "fpgm", "prep", "cvt " };

            for (var i = 0; i < iterations; i++)
            {
                var font = SyntheticFont.FromBytes(notoBytes);
                var table = tables[rng.Next(tables.Length)];

                font.Mutate(table, bytes =>
                {
                    for (var flips = rng.Next(1, 9); flips > 0; flips--)
                    {
                        bytes[rng.Next(bytes.Length)] ^= (byte)(1 << rng.Next(8));
                    }
                });

                var typeface = font.CreateGlyphTypeface();
                var hinter = typeface.GetTrueTypeHinter(
                    GlyphMaskKey.QuantizeScale(16f), GlyphMaskMode.Antialiased);

                // A corrupted program either survives or memoises a veto; both are fine,
                // throwing is not.
                hinter?.TryHint(typeface.CharacterToGlyphMap['H'], backwardCompatibility: 4);
            }
        }

        /// <summary>Bit-flipped glyf records through mask builds, hinted and not.</summary>
        private static void FuzzGlyphRecords(Random rng, int iterations)
        {
            var notoBytes = TestFontFiles.Load("NotoMono-Regular.ttf");

            for (var i = 0; i < iterations; i++)
            {
                var font = SyntheticFont.FromBytes(notoBytes);

                font.Mutate("glyf", bytes =>
                {
                    for (var flips = rng.Next(1, 9); flips > 0; flips--)
                    {
                        bytes[rng.Next(bytes.Length)] ^= (byte)(1 << rng.Next(8));
                    }
                });

                var typeface = font.CreateGlyphTypeface();
                using var scratch = new GlyphPathBuilder();

                foreach (var c in "Hox")
                {
                    var key = new GlyphMaskKey(
                        typeface.CharacterToGlyphMap[c], GlyphMaskKey.QuantizeScale(16f),
                        0, GlyphMaskMode.Antialiased);

                    GlyphMasks.Build(typeface, scratch, key);
                }

                var randomGlyph = (ushort)rng.Next(typeface.GlyphCount);
                var randomKey = new GlyphMaskKey(
                    randomGlyph, GlyphMaskKey.QuantizeScale(16f), 0, GlyphMaskMode.Antialiased);

                GlyphMasks.Build(typeface, scratch, randomKey);
            }
        }
    }
}
