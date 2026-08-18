using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Fonts.Rasterization.TrueType;
using Avalonia.Media.Fonts.Tables;
using Avalonia.Media.Fonts.Tables.Variation;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization.TrueType
{
    /// <summary>
    /// Variable-font hinting end to end on real instructed VFs: variation instances get
    /// their own size states, the varied outline runs the same programs, cvar-adjusted
    /// control values flow into the snapshot, and varied hinting stays deterministic.
    /// Windows-gated - Bahnschrift hints without cvar (default CVT, spec-conformant) and
    /// Segoe UI Variable carries a real cvar, so both integration halves run.
    /// </summary>
    public class TrueTypeVariationHintingTests
    {
        [Theory]
        [InlineData(@"C:\Windows\Fonts\bahnschrift.ttf")]
        [InlineData(@"C:\Windows\Fonts\SegUIVar.ttf")]
        public void Varied_Instances_Hint_Through_Their_Own_Size_States(string fontPath)
        {
            Assert.SkipWhen(!OperatingSystem.IsWindows() || !File.Exists(fontPath),
                "needs the instructed variable system font");

            var typeface = SyntheticFont.FromBytes(File.ReadAllBytes(fontPath)).CreateGlyphTypeface();

            Assert.SkipWhen(!typeface.HasTrueTypeHinting, "this build ships no hinting machinery");

            var bold = typeface.WithVariation(typeface.CreateNormalizedPosition(new FontVariationSettings(
                new[] { new FontVariation(OpenTypeTag.Parse("wght"), 700) })));

            Assert.NotSame(typeface, bold);

            var scaleQ = GlyphMaskKey.QuantizeScale(14f);
            var defaultHinter = typeface.GetTrueTypeHinter(scaleQ, GlyphMaskMode.Antialiased);
            var boldHinter = bold.GetTrueTypeHinter(scaleQ, GlyphMaskMode.Antialiased);

            Assert.NotNull(defaultHinter);
            Assert.NotNull(boldHinter);

            // Instance-keyed: each variation clone carries its own hinter and size state.
            Assert.NotSame(defaultHinter, boldHinter);

            var glyph = typeface.CharacterToGlyphMap['H'];

            Assert.True(defaultHinter!.TryHint(glyph, backwardCompatibility: 4));

            var defaultZone = Snapshot(defaultHinter.Zone!);

            Assert.True(boldHinter!.TryHint(glyph, backwardCompatibility: 4));

            var boldZone = Snapshot(boldHinter.Zone!);

            // wght 700 widens stems: the varied outline must hint to different metal.
            Assert.NotEqual(defaultZone, boldZone);

            // And deterministically so.
            Assert.True(boldHinter.TryHint(glyph, backwardCompatibility: 4));
            Assert.Equal(boldZone, Snapshot(boldHinter.Zone!));

            // When the font carries cvar, an instance that activates its tuples must see
            // adjusted control values. Tuples peak where the designer put them (Segoe UI
            // Variable's single tuple peaks at the optical-size minimum, not on weight),
            // so drive every axis to its minimum - peaks at normalized -1 all engage.
            if (typeface.PlatformTypeface.TryGetTable(CvarTable.Tag, out _))
            {
                var minimums = new List<FontVariation>();

                foreach (var axis in typeface.VariationAxes)
                {
                    minimums.Add(new FontVariation(axis.Tag, axis.MinimumValue));
                }

                var minInstance = typeface.WithVariation(
                    typeface.CreateNormalizedPosition(new FontVariationSettings(minimums)));
                var minHinter = minInstance.GetTrueTypeHinter(scaleQ, GlyphMaskMode.Antialiased);

                Assert.NotNull(minHinter);
                Assert.NotEqual(
                    ToArray(defaultHinter.State.Interpreter!.PristineCvt),
                    ToArray(minHinter!.State.Interpreter!.PristineCvt));
            }
        }

        private static string Snapshot(TrueTypeZone zone)
        {
            var builder = new System.Text.StringBuilder();

            for (var i = 0; i < zone.PointCount; i++)
            {
                builder.Append(zone.CurX[i]).Append(',').Append(zone.CurY[i]).Append(';');
            }

            return builder.ToString();
        }

        private static int[] ToArray(ReadOnlySpan<int> values) => values.ToArray();
    }
}
