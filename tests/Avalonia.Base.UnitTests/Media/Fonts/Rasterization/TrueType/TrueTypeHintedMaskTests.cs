using System;
using System.Buffers.Binary;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Fonts.Rasterization.TrueType;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization.TrueType
{
    /// <summary>
    /// The mask pipeline over the bytecode engine: instructed fonts grid-fit through their
    /// own programs, ineligible fonts stay on the auto-hinter, and a control program that
    /// disables hinting renders unfitted outlines.
    /// </summary>
    public class TrueTypeHintedMaskTests
    {
        private const float PixelsPerEm = 16f;

        // The embedded Inter asset is an uninstructed build; Noto Mono carries the full
        // ttfautohint program set (fpgm, prep, cvt and per-glyph streams), which is what
        // these tests need.
        private static GlyphTypeface CreateNoto(bool stripPrograms = false, byte[]? prepOverride = null)
        {
            var font = SyntheticFont.FromBytes(TestFontFiles.Load("NotoMono-Regular.ttf"));

            if (stripPrograms)
            {
                font.Remove("fpgm");
                font.Remove("prep");
                font.Remove("cvt ");
            }

            if (prepOverride is not null)
            {
                font.Replace("prep", prepOverride);
            }

            return font.CreateGlyphTypeface();
        }

        /// <summary>A simple glyph whose glyf record carries a real instruction stream.</summary>
        private static ushort FindInstructedGlyph(GlyphTypeface typeface)
        {
            var glyfTable = typeface.GlyfTable!;
            var withData = 0;
            var simple = 0;

            for (var glyph = 1; glyph < typeface.GlyphCount; glyph++)
            {
                if (!glyfTable.TryGetGlyphData(glyph, out var data) || data.Length < 12)
                {
                    continue;
                }

                withData++;

                var span = data.Span;
                int contours = BinaryPrimitives.ReadInt16BigEndian(span);

                if (contours <= 0 || span.Length < 12 + contours * 2)
                {
                    continue;
                }

                simple++;

                if (BinaryPrimitives.ReadUInt16BigEndian(span.Slice(10 + contours * 2, 2)) > 0)
                {
                    return (ushort)glyph;
                }
            }

            Assert.Fail(
                $"no instructed glyph found: glyphCount={typeface.GlyphCount} withData={withData} simple={simple}");
            return 0;
        }

        private static GlyphMask BuildMask(
            GlyphTypeface typeface, ushort glyph, GlyphMaskMode mode, bool gridFit, bool stemSnap = false)
        {
            using var scratch = new GlyphPathBuilder();
            var key = new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale(PixelsPerEm), 0, mode, gridFit, stemSnap);

            return GlyphMasks.Build(typeface, scratch, key);
        }

        [Fact]
        public void Instructed_Fonts_Grid_Fit_Through_Their_Programs()
        {
            var typeface = CreateNoto();
            var glyph = FindInstructedGlyph(typeface);

            // The size state must survive the real fpgm and prep; a null hinter here would
            // silently fall back to the auto-hinter and void the comparison below.
            var probe = typeface.GetTrueTypeHinter(
                GlyphMaskKey.QuantizeScale(PixelsPerEm), GlyphMaskMode.Antialiased);

            Assert.NotNull(probe);

            var hinted = BuildMask(typeface, glyph, GlyphMaskMode.Antialiased, gridFit: true);
            var autoHinted = BuildMask(CreateNoto(stripPrograms: true), glyph, GlyphMaskMode.Antialiased, gridFit: true);

            Assert.False(hinted.IsEmpty);
            Assert.False(autoHinted.IsEmpty);

            // The font's own program and the auto-hinter fit differently; identical output
            // would mean the bytecode branch never ran.
            var identical = hinted.Width == autoHinted.Width &&
                            hinted.Height == autoHinted.Height &&
                            hinted.Left == autoHinted.Left &&
                            hinted.Top == autoHinted.Top &&
                            hinted.Alpha.AsSpan().SequenceEqual(autoHinted.Alpha);

            Assert.False(identical);

            // Deterministic: the same build twice is byte-identical.
            var again = BuildMask(typeface, glyph, GlyphMaskMode.Antialiased, gridFit: true);

            Assert.True(hinted.Alpha.AsSpan().SequenceEqual(again.Alpha));
            Assert.Equal(hinted.Left, again.Left);
            Assert.Equal(hinted.Top, again.Top);
        }

        [Fact]
        public void Uninstructed_Fonts_Stay_On_The_Auto_Hinter()
        {
            var typeface = CreateNoto(stripPrograms: true);

            Assert.False(typeface.HasTrueTypeHinting);

            var glyph = typeface.CharacterToGlyphMap['H'];
            var fitted = BuildMask(typeface, glyph, GlyphMaskMode.Antialiased, gridFit: true);
            var unfitted = BuildMask(typeface, glyph, GlyphMaskMode.Antialiased, gridFit: false);

            Assert.False(fitted.IsEmpty);

            // The auto-hinter still fits: grid-fit output differs from the raw outline.
            Assert.False(fitted.Alpha.AsSpan().SequenceEqual(unfitted.Alpha) &&
                         fitted.Top == unfitted.Top &&
                         fitted.Height == unfitted.Height);
        }

        [Fact]
        public void A_Hinting_Disabling_Control_Program_Renders_Unfitted()
        {
            // INSTCTRL selector 1 value 1: the font asks for glyph instructions to be
            // skipped at this size. Honoring it means the grid-fit build matches the
            // unfitted build exactly - not the auto-hinter's fit.
            var prep = new TtAsm().PushB(1, 1).Op(TtAsm.Instctrl).Build();
            var typeface = CreateNoto(prepOverride: prep);
            var glyph = typeface.CharacterToGlyphMap['H'];

            var fitted = BuildMask(typeface, glyph, GlyphMaskMode.Antialiased, gridFit: true);
            var unfitted = BuildMask(typeface, glyph, GlyphMaskMode.Antialiased, gridFit: false);

            Assert.False(fitted.IsEmpty);
            Assert.Equal(unfitted.Left, fitted.Left);
            Assert.Equal(unfitted.Top, fitted.Top);
            Assert.True(fitted.Alpha.AsSpan().SequenceEqual(unfitted.Alpha));
        }

        [Fact]
        public void Identity_Hinting_Lands_Ink_Exactly_Where_The_Unhinted_Build_Does()
        {
            // A glyph without instructions in an eligible font rides the hinted branch as
            // pure identity; composed onto a canvas, its ink must match the unhinted build
            // pixel for pixel. Any offset here is a placement defect in the hinted branch.
            var typeface = CreateNoto();
            var glyfTable = typeface.GlyfTable!;
            ushort quiet = 0;

            for (var glyph = 1; glyph < typeface.GlyphCount; glyph++)
            {
                if (!glyfTable.TryGetGlyphData(glyph, out var data) || data.Length < 12)
                {
                    continue;
                }

                var span = data.Span;
                int contours = BinaryPrimitives.ReadInt16BigEndian(span);

                if (contours > 0 && span.Length >= 12 + contours * 2 &&
                    BinaryPrimitives.ReadUInt16BigEndian(span.Slice(10 + contours * 2, 2)) == 0)
                {
                    quiet = (ushort)glyph;
                    break;
                }
            }

            Assert.NotEqual(0, quiet);

            var hinted = BuildMask(typeface, quiet, GlyphMaskMode.Antialiased, gridFit: true);
            var unhinted = BuildMask(typeface, quiet, GlyphMaskMode.Antialiased, gridFit: false);

            Assert.False(hinted.IsEmpty);
            Assert.False(unhinted.IsEmpty);

            // Identical placement is exact; coverage may wiggle by the 26.6 quantization
            // (the hinted path snaps coordinates to 1/64 px before emission), which moves
            // AA edge bytes slightly but must never shift ink by a pixel.
            Assert.Equal(unhinted.Left, hinted.Left);
            Assert.Equal(unhinted.Top, hinted.Top);

            var composedHinted = Compose(hinted);
            var composedUnhinted = Compose(unhinted);
            var worst = 0;

            for (var i = 0; i < composedHinted.Length; i++)
            {
                worst = Math.Max(worst, Math.Abs(composedHinted[i] - composedUnhinted[i]));
            }

            Assert.True(worst <= 24,
                $"identity-hinted coverage deviates by {worst} - more than quantization can explain");

            static byte[] Compose(GlyphMask mask)
            {
                const int size = 64;
                var canvas = new byte[size * size];

                for (var y = 0; y < mask.Height; y++)
                {
                    var row = 40 + mask.Top + y;

                    if (row < 0 || row >= size)
                    {
                        continue;
                    }

                    for (var x = 0; x < mask.Width; x++)
                    {
                        var column = 8 + mask.Left + x;

                        if (column >= 0 && column < size)
                        {
                            canvas[row * size + column] = mask.Alpha[y * mask.Width + x];
                        }
                    }
                }

                return canvas;
            }
        }

        [Fact]
        public void All_Mask_Modes_Build_Hinted()
        {
            var typeface = CreateNoto();
            var glyph = FindInstructedGlyph(typeface);

            var lcd = BuildMask(typeface, glyph, GlyphMaskMode.Subpixel, gridFit: true);
            var aliased = BuildMask(typeface, glyph, GlyphMaskMode.Aliased, gridFit: true, stemSnap: true);

            Assert.False(lcd.IsEmpty);
            Assert.Equal(3, lcd.Channels);
            Assert.False(aliased.IsEmpty);
        }

        [Theory]
        [InlineData((byte)GlyphMaskMode.Antialiased, 1)]
        [InlineData((byte)GlyphMaskMode.Subpixel, 3)]
        public void Strong_Takes_The_Full_Program_Y_And_Keeps_The_Unhinted_X(byte maskMode, int subpixelFactor)
        {
            var mode = (GlyphMaskMode)maskMode;
            var (typeface, glyph) = CreateNotoWithProgram('H', ShiftBothAxesProgram());

            var strong = BuildOutline(typeface, glyph, mode, strong: true);
            var fullY = HintedOutline(typeface, glyph, mode, backwardCompatibility: 0, unhintedX: true, subpixelFactor);

            // The fixture's y shift only runs under full interpretation, and its x shift
            // there moves the outline, so both halves of the expectation are observable.
            Assert.False(fullY.AsSpan().SequenceEqual(
                HintedOutline(typeface, glyph, mode, backwardCompatibility: 4, unhintedX: true, subpixelFactor)));
            Assert.False(fullY.AsSpan().SequenceEqual(
                HintedOutline(typeface, glyph, mode, backwardCompatibility: 0, unhintedX: false, subpixelFactor)));

            Assert.Equal(fullY, strong);

            // The kept x is the scaled design outline, to the 26.6 quantization.
            var unhinted = UnhintedOutline(typeface, glyph, subpixelFactor);

            Assert.Equal(unhinted.Length, strong.Length);

            for (var i = 0; i < strong.Length; i += 2)
            {
                Assert.True(Math.Abs(strong[i] - unhinted[i]) <= subpixelFactor / 64f + 1e-4f,
                    $"point {i / 2}: x {strong[i]} vs unhinted {unhinted[i]}");
            }
        }

        [Fact]
        public void Aliased_Strong_Keeps_The_Programs_X_Fitting()
        {
            var (typeface, glyph) = CreateNotoWithProgram('H', ShiftBothAxesProgram());

            var aliased = BuildOutline(typeface, glyph, GlyphMaskMode.Aliased, strong: true);
            var full = HintedOutline(typeface, glyph, GlyphMaskMode.Aliased, backwardCompatibility: 0,
                unhintedX: false, subpixelFactor: 1);

            Assert.Equal(full, aliased);
        }

        /// <summary>
        /// Moves point 0 by 40/64 px in x and point 1 by 20/64 px in y. Neither point is touched
        /// first, so the natural class drops the y move (SHPIX moves only points already
        /// touched in y there) and ignores the x move.
        /// </summary>
        private static byte[] ShiftBothAxesProgram() => new TtAsm()
            .Op(0x01).PushB(0, 40).Op(0x38)
            .Op(0x00).PushB(1, 20).Op(0x38)
            .Build();

        /// <summary>
        /// Noto Mono with <paramref name="character"/>'s glyph program replaced in place by
        /// <paramref name="program"/>, padded with CLEAR to the original length.
        /// </summary>
        private static (GlyphTypeface Typeface, ushort Glyph) CreateNotoWithProgram(char character, byte[] program)
        {
            var bytes = TestFontFiles.Load("NotoMono-Regular.ttf");
            var glyph = SyntheticFont.FromBytes(bytes).CreateGlyphTypeface().CharacterToGlyphMap[character];
            var font = SyntheticFont.FromBytes(bytes);
            var longOffsets = BinaryPrimitives.ReadInt16BigEndian(font.GetTable("head").AsSpan(50)) != 0;
            var loca = font.GetTable("loca");
            var offset = longOffsets
                ? (int)BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(glyph * 4))
                : BinaryPrimitives.ReadUInt16BigEndian(loca.AsSpan(glyph * 2)) * 2;

            font.Mutate("glyf", glyf =>
            {
                var contours = BinaryPrimitives.ReadInt16BigEndian(glyf.AsSpan(offset));

                Assert.True(contours > 0, "the fixture glyph must be simple");

                var lengthAt = offset + 10 + contours * 2;
                var length = BinaryPrimitives.ReadUInt16BigEndian(glyf.AsSpan(lengthAt));

                Assert.True(length >= program.Length, "the fixture glyph's program is too short to replace");

                var instructions = glyf.AsSpan(lengthAt + 2, length);

                instructions.Fill(0x22);
                program.CopyTo(instructions);
            });

            return (font.CreateGlyphTypeface(), glyph);
        }

        private static float[] BuildOutline(GlyphTypeface typeface, ushort glyph, GlyphMaskMode mode, bool strong)
        {
            using var scratch = new GlyphPathBuilder();

            GlyphMasks.Build(typeface, scratch,
                new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale(PixelsPerEm), 0, mode, GridFit: true, Strong: strong));

            return scratch.Points.ToArray();
        }

        /// <summary>The glyph's own program result under the given class, optionally with every
        /// outline point's x put back to its scaled original, emitted like the mask builder
        /// emits it.</summary>
        private static float[] HintedOutline(GlyphTypeface typeface, ushort glyph, GlyphMaskMode mode,
            int backwardCompatibility, bool unhintedX, int subpixelFactor)
        {
            var hinter = typeface.GetTrueTypeHinter(GlyphMaskKey.QuantizeScale(PixelsPerEm), mode);

            Assert.NotNull(hinter);

            var rented = hinter!.Rent();

            try
            {
                Assert.True(rented.TryHint(glyph, backwardCompatibility));

                var zone = rented.Zone!;

                if (unhintedX)
                {
                    for (var i = 0; i < zone.PointCount - 4; i++)
                    {
                        zone.CurX[i] = zone.OrgX[i];
                    }
                }

                using var builder = new GlyphPathBuilder();

                TrueTypeGlyphEmitter.Emit(zone, new Matrix(subpixelFactor, 0, 0, -1, 0, 0), builder);

                return builder.Points.ToArray();
            }
            finally
            {
                hinter.Return(rented);
            }
        }

        private static float[] UnhintedOutline(GlyphTypeface typeface, ushort glyph, int subpixelFactor)
        {
            var scale = PixelsPerEm / typeface.Metrics.DesignEmHeight;

            using var builder = new GlyphPathBuilder();

            Assert.True(typeface.TryBuildGlyphContours(glyph, new Matrix(scale * subpixelFactor, 0, 0, -scale, 0, 0),
                builder));

            return builder.Points.ToArray();
        }
    }
}
