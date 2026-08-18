using System;
using System.Buffers.Binary;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
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
    }
}
