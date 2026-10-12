using System;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Fonts.Tables.Variation;
using Avalonia.Platform;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Tables
{
    public class AvarTableTests
    {
        private const string InterRegularAsset =
            "resm:Avalonia.Base.UnitTests.Assets.Inter-Regular.ttf?assembly=Avalonia.Base.UnitTests";

        private const string InterVariableAsset =
            "resm:Avalonia.Base.UnitTests.Assets.InterVariable.ttf?assembly=Avalonia.Base.UnitTests";

        private static GlyphTypeface LoadTypeface(string assetUri)
        {
            var assetLoader = new StandardAssetLoader();
            using var stream = assetLoader.Open(new Uri(assetUri));
            return new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));
        }

        [Fact]
        public void TryLoad_Returns_False_For_Static_Font()
        {
            var typeface = LoadTypeface(InterRegularAsset);

            Assert.False(AvarTable.TryLoad(typeface, out var avar));
            Assert.Null(avar);
        }

        [Fact]
        public void TryLoad_Returns_True_For_Inter_Variable()
        {
            var typeface = LoadTypeface(InterVariableAsset);

            Assert.True(AvarTable.TryLoad(typeface, out var avar));
            Assert.NotNull(avar);
            Assert.Equal(2, avar!.AxisCount);
        }

        [Fact]
        public void Remap_Is_Identity_For_Linear_Axis()
        {
            // Inter Variable's opsz axis has an identity-only segment map:
            //   (-1 → -1), (0 → 0), (+1 → +1). Any input passes through unchanged.
            var typeface = LoadTypeface(InterVariableAsset);
            Assert.True(AvarTable.TryLoad(typeface, out var avar));

            Assert.Equal(-1f, avar!.Remap(axisIndex: 0, -1f));
            Assert.Equal(0f, avar.Remap(axisIndex: 0, 0f));
            Assert.Equal(0.5f, avar.Remap(axisIndex: 0, 0.5f));
            Assert.Equal(1f, avar.Remap(axisIndex: 0, 1f));
        }

        [Fact]
        public void Remap_Applies_Bend_For_NonLinear_Axis()
        {
            // Inter Variable's wght axis bends through these segment-map entries:
            //   (-1 → -1), (0 → 0), (0.2 → 0.18), (0.4 → 0.36),
            //   (0.6 → 0.54), (0.8 → 0.76), (1.0 → 1.0)
            // Each exact entry must round-trip; intermediate values are linear between
            // the surrounding entries.
            var typeface = LoadTypeface(InterVariableAsset);
            Assert.True(AvarTable.TryLoad(typeface, out var avar));

            const float tolerance = 1e-4f;

            Assert.Equal(0.18f, avar!.Remap(axisIndex: 1, 0.2f), tolerance);
            Assert.Equal(0.36f, avar.Remap(axisIndex: 1, 0.4f), tolerance);
            Assert.Equal(0.54f, avar.Remap(axisIndex: 1, 0.6f), tolerance);
            Assert.Equal(0.76f, avar.Remap(axisIndex: 1, 0.8f), tolerance);

            // Midpoint between (0.4 → 0.36) and (0.6 → 0.54) should be (0.5 → 0.45) under
            // linear interpolation.
            Assert.Equal(0.45f, avar.Remap(axisIndex: 1, 0.5f), tolerance);
        }

        [Fact]
        public void Remap_Scans_Maps_With_Repeated_End_Entries()
        {
            // PingFang UI's weight maps repeat the -1 and +1 inputs: the identity entry the spec
            // requires plus the design's own value. CoreText resolves an exact -1 to the second
            // entry and an exact +1 to the first, which is what the segment scan yields.
            var avar = LoadAvar(Map(
                (-1, -1), (-1, -0.98), (-0.5, -0.482), (0, 0), (0.5, 0.29), (1, 0.736), (1, 1)));

            const float tolerance = 1e-3f;

            Assert.Equal(-0.98f, avar.Remap(axisIndex: 1, -1f), tolerance);
            Assert.Equal(0.736f, avar.Remap(axisIndex: 1, 1f), tolerance);

            // Out-of-range input still lands on the same ends.
            Assert.Equal(-0.98f, avar.Remap(axisIndex: 1, -2f), tolerance);
            Assert.Equal(0.736f, avar.Remap(axisIndex: 1, 5f), tolerance);
        }

        [Fact]
        public void Remap_Scans_Maps_With_Entries_Out_Of_Order_In_Table_Order()
        {
            // PingFang UI Display's weight map, whose entries around 0 are out of order. CoreText
            // scans the entries as stored: weight 400 (-0.2481) maps to -0.2264 and weight 500
            // (0.0025) to 0.0008, measured from the outlines it draws.
            var avar = LoadAvar(Map(
                (-1, -1), (-1, -0.98), (-0.75, -0.8199), (-0.5, -0.482), (-0.25, -0.228), (0, -0.024),
                (-0.0024, 0), (0, 0), (0.25, 0.08), (0.5, 0.29), (0.75, 0.546), (1, 0.736), (1, 1)));

            const float tolerance = 1e-3f;

            Assert.Equal(-0.2264f, avar.Remap(axisIndex: 1, -0.2481f), tolerance);
            Assert.Equal(0.0008f, avar.Remap(axisIndex: 1, 0.0025f), tolerance);
            Assert.Equal(0.2913f, avar.Remap(axisIndex: 1, 0.5012f), tolerance);
            Assert.Equal(-0.98f, avar.Remap(axisIndex: 1, -1f), tolerance);
        }

        [Fact]
        public void Remap_Keeps_The_Ends_Of_Maps_That_Omit_Them()
        {
            var avar = LoadAvar(Map((-0.5, -0.4), (0, 0), (0.5, 0.6)));

            Assert.Equal(-1f, avar.Remap(axisIndex: 1, -1f));
            Assert.Equal(1f, avar.Remap(axisIndex: 1, 1f));
        }

        private static (double From, double To)[] Map(params (double From, double To)[] entries) => entries;

        /// <summary>
        /// Inter Variable (opsz, wght) with an avar whose opsz map is the identity and whose wght
        /// map is <paramref name="weightMap"/>.
        /// </summary>
        private static AvarTable LoadAvar((double From, double To)[] weightMap)
        {
            var buffer = new BigEndianBuffer()
                .UInt16(1).UInt16(0).UInt16(0).UInt16(2)
                .UInt16(3).F2Dot14(-1).F2Dot14(-1).F2Dot14(0).F2Dot14(0).F2Dot14(1).F2Dot14(1)
                .UInt16(weightMap.Length);

            foreach (var (from, to) in weightMap)
            {
                buffer.F2Dot14(from).F2Dot14(to);
            }

            var typeface = SyntheticFont.FromAsset(InterVariableAsset).Replace("avar", buffer.ToArray()).CreateGlyphTypeface();

            Assert.True(AvarTable.TryLoad(typeface, out var avar));

            return avar!;
        }

        [Fact]
        public void Remap_Clamps_Out_Of_Range_Input()
        {
            // The avar domain is exactly [-1, 1]; values outside that range are clamped
            // before lookup. This prevents extrapolation off the ends of the segment list.
            var typeface = LoadTypeface(InterVariableAsset);
            Assert.True(AvarTable.TryLoad(typeface, out var avar));

            Assert.Equal(-1f, avar!.Remap(axisIndex: 1, -2f));
            Assert.Equal(1f, avar.Remap(axisIndex: 1, 5f));
        }

        [Fact]
        public void Remap_Is_Identity_For_Out_Of_Range_Axis_Index()
        {
            // Defensive: an axis index beyond the table's axisCount returns the input
            // unchanged. This matters if a font's avar declares fewer axes than fvar,
            // which the spec forbids but malformed fonts sometimes do.
            var typeface = LoadTypeface(InterVariableAsset);
            Assert.True(AvarTable.TryLoad(typeface, out var avar));

            Assert.Equal(0.5f, avar!.Remap(axisIndex: 99, 0.5f));
            Assert.Equal(0.5f, avar.Remap(axisIndex: -1, 0.5f));
        }
    }
}
