using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Avalonia.UnitTests;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// A simulated glyph typeface reports the outlines and ink bounds of its simulations, while its
    /// advances and font metrics stay those of the unsimulated face.
    /// </summary>
    public class SimulatedGlyphMetricsTests
    {
        private const string DejaVuSansUri =
            "resm:Avalonia.Skia.UnitTests.Fonts.DejaVuSans.ttf?assembly=Avalonia.Skia.UnitTests";

        // A CFF font, whose outlines are charstrings rather than glyf points.
        private const string SourceCodeProUri =
            "resm:Avalonia.Skia.UnitTests.Fonts.SourceCodePro-Subset.otf?assembly=Avalonia.Skia.UnitTests";

        // A CFF2 variable font.
        private const string AdobeVFPrototypeUri =
            "resm:Avalonia.Skia.UnitTests.Fonts.AdobeVFPrototype-Subset.otf?assembly=Avalonia.Skia.UnitTests";

        // Latin letters, digits and punctuation, plus accented letters that are composite glyphs.
        private const string Text = "!\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`" +
                                    "abcdefghijklmnopqrstuvwxyz{|}~ÅÉéñüŁƒ";

        // The documented strength of simulated bold in size-independent glyph data.
        private const double BoldStrengthPerEm = 1.0 / 24;

        [Theory]
        [InlineData(DejaVuSansUri, GlyphOutlineType.TrueType)]
        [InlineData(SourceCodeProUri, GlyphOutlineType.Cff)]
        [InlineData(AdobeVFPrototypeUri, GlyphOutlineType.Cff2)]
        public void Oblique_Outline_Is_The_Unsimulated_Outline_Sheared(string uri, GlyphOutlineType outlineType)
        {
            using var app = StartWithGeometry();

            var root = Load(uri);

            Assert.Equal(outlineType, root.OutlineType);

            AssertSheared(root, root.WithSimulations(FontSimulations.Oblique), GetMappedGlyphs(root));
        }

        [Theory]
        [InlineData(DejaVuSansUri)]
        [InlineData(SourceCodeProUri)]
        [InlineData(AdobeVFPrototypeUri)]
        public void Bold_Oblique_Outline_Is_The_Bold_Outline_Sheared(string uri)
        {
            using var app = StartWithGeometry();

            var root = Load(uri);

            AssertSheared(root.WithSimulations(FontSimulations.Bold),
                root.WithSimulations(FontSimulations.Bold | FontSimulations.Oblique), GetMappedGlyphs(root));
        }

        private const double ShearTolerance = 1e-3;

        private static void AssertSheared(GlyphTypeface source, GlyphTypeface sheared, ushort[] glyphs)
        {
            var slant = (double)FontSimulationConstants.ObliqueSlant;

            foreach (var glyph in glyphs)
            {
                var expected = OutlineEmboldenReferenceTests.GetOutlinePoints(source.GetGlyphOutline(glyph));
                var actual = OutlineEmboldenReferenceTests.GetOutlinePoints(sheared.GetGlyphOutline(glyph));

                Assert.Equal(expected.Count, actual.Count);

                for (var i = 0; i < expected.Count; i++)
                {
                    // The path stores single-precision points, so a fractional source point is
                    // rounded before the shear here and after it in the sheared outline.
                    Assert.Equal(expected[i].X + slant * expected[i].Y, actual[i].X, ShearTolerance);
                    Assert.Equal(expected[i].Y, actual[i].Y, ShearTolerance);
                }
            }
        }

        [Fact]
        public void Bold_Grows_A_Rectangle_By_Half_The_Documented_Strength_On_Every_Side()
        {
            using var app = StartWithGeometry();

            var root = Load(DejaVuSansUri);
            var glyph = root.CharacterToGlyphMap['I'];
            var halfStrength = root.Metrics.DesignEmHeight * BoldStrengthPerEm / 2;

            var plain = GetBounds(OutlineEmboldenReferenceTests.GetOutlinePoints(root.GetGlyphOutline(glyph)));
            var bold = GetBounds(OutlineEmboldenReferenceTests.GetOutlinePoints(
                root.WithSimulations(FontSimulations.Bold).GetGlyphOutline(glyph)));

            // Right-angle corners have no miter excess, so every side moves by exactly half.
            Assert.Equal(plain.Left - halfStrength, bold.Left, 3);
            Assert.Equal(plain.Top - halfStrength, bold.Top, 3);
            Assert.Equal(plain.Right + halfStrength, bold.Right, 3);
            Assert.Equal(plain.Bottom + halfStrength, bold.Bottom, 3);
        }

        public static IEnumerable<object[]> ReferenceFaces()
        {
            foreach (var face in FreeTypeSimulatedBoundsReference.Faces)
            {
                yield return new object[] { face.Font, face.Simulations };
            }
        }

        /// <summary>
        /// Simulated ink bounds are the smallest box of whole design units that contains the exact
        /// curve extent of the simulated outline, as FreeType's FT_Outline_Get_BBox reports it.
        /// </summary>
        [Theory]
        [MemberData(nameof(ReferenceFaces))]
        public void Simulated_Ink_Bounds_Are_The_Rounded_Out_Exact_Bounds_Of_The_Simulated_Outline(string font,
            FontSimulations simulations)
        {
            // FreeType carries the shear as a 16.16 matrix (0.300003 rather than 0.3), rounds every
            // point to 1 / Scale font units and emboldens in fixed point, so its box can sit up to
            // this far from the exact one; a box edge that close to a whole unit may round either way.
            const double tolerance = 0.03;

            using var app = StartWithGeometry();

            var reference = Array.Find(FreeTypeSimulatedBoundsReference.Faces,
                f => f.Font == font && f.Simulations == simulations)!;
            var variant = Load(UriOf(font)).WithSimulations(simulations);
            var failures = new List<string>();

            for (var i = 0; i < reference.Boxes.Length; i += 5)
            {
                var glyph = (ushort)reference.Boxes[i];
                double Unit(int j) => (double)reference.Boxes[i + j] / FreeTypeSimulatedBoundsReference.Scale;

                var bounds = new GlyphBounds[1];
                Assert.True(variant.TryGetGlyphBounds(new[] { glyph }, bounds));
                var box = bounds[0];

                var ok = IsRoundedDown(box.XMin, Unit(1)) && IsRoundedDown(box.YMin, Unit(2)) &&
                         IsRoundedUp(box.XMax, Unit(3)) && IsRoundedUp(box.YMax, Unit(4));

                foreach (var point in GetOnCurvePoints(variant.GetGlyphOutline(glyph)))
                {
                    ok &= box.XMin <= point.X && point.X <= box.XMax && box.YMin <= point.Y && point.Y <= box.YMax;
                }

                if (!ok)
                {
                    failures.Add($"glyph {glyph}: reported ({box.XMin}, {box.YMin}, {box.XMax}, {box.YMax}) " +
                                 $"FreeType ({Unit(1):F2}, {Unit(2):F2}, {Unit(3):F2}, {Unit(4):F2})");
                }
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

            bool IsRoundedDown(short reported, double exact) =>
                reported == Math.Floor(exact) || reported == Math.Floor(exact - tolerance) ||
                reported == Math.Floor(exact + tolerance);

            bool IsRoundedUp(short reported, double exact) =>
                reported == Math.Ceiling(exact) || reported == Math.Ceiling(exact - tolerance) ||
                reported == Math.Ceiling(exact + tolerance);
        }

        private static IEnumerable<Point> GetOnCurvePoints(IGeometryImpl? geometry)
        {
            Assert.NotNull(geometry);

            var path = Assert.IsAssignableFrom<GeometryImpl>(((ImmutableGeometryImpl)geometry!).Inner).FillPath!;
            var points = new SKPoint[4];
            var result = new List<Point>();

            using var iterator = path.CreateRawIterator();

            SKPathVerb verb;

            while ((verb = iterator.Next(points)) != SKPathVerb.Done)
            {
                var end = verb switch
                {
                    SKPathVerb.Move => 0,
                    SKPathVerb.Line => 1,
                    SKPathVerb.Quad or SKPathVerb.Conic => 2,
                    SKPathVerb.Cubic => 3,
                    _ => -1
                };

                if (end >= 0)
                {
                    result.Add(new Point(points[end].X, points[end].Y));
                }
            }

            return result;
        }

        private static string UriOf(string font) => font switch
        {
            "DejaVuSans" => DejaVuSansUri,
            "SourceCodePro" => SourceCodeProUri,
            _ => AdobeVFPrototypeUri
        };

        [Theory]
        [InlineData(FontSimulations.Bold)]
        [InlineData(FontSimulations.Oblique)]
        [InlineData(FontSimulations.Bold | FontSimulations.Oblique)]
        public void Every_Bounds_Query_Reports_The_Same_Simulated_Bounds(FontSimulations simulations)
        {
            var root = LoadDejaVuSans();
            var variant = root.WithSimulations(simulations);
            var glyphs = GetGlyphs(root);

            var metrics = new GlyphMetrics[glyphs.Length];
            var bounds = new GlyphBounds[glyphs.Length];

            Assert.True(variant.TryGetGlyphMetrics(glyphs, metrics));
            Assert.True(variant.TryGetGlyphBounds(glyphs, bounds));

            for (var i = 0; i < glyphs.Length; i++)
            {
                Assert.True(variant.TryGetGlyphMetrics(glyphs[i], out var single));

                Assert.Equal(metrics[i], single);
                Assert.Equal(bounds[i].XMin, single.XBearing);
                Assert.Equal(bounds[i].YMax, single.YBearing);
                Assert.Equal(bounds[i].Width, single.Width);
                Assert.Equal(bounds[i].Height, single.Height);
            }
        }

        [Theory]
        [InlineData(DejaVuSansUri, FontSimulations.Bold)]
        [InlineData(DejaVuSansUri, FontSimulations.Oblique)]
        [InlineData(DejaVuSansUri, FontSimulations.Bold | FontSimulations.Oblique)]
        [InlineData(SourceCodeProUri, FontSimulations.Bold | FontSimulations.Oblique)]
        public void Simulations_Do_Not_Change_Advances(string uri, FontSimulations simulations)
        {
            var root = Load(uri);
            var variant = root.WithSimulations(simulations);
            var glyphs = GetMappedGlyphs(root);

            var rootMetrics = new GlyphMetrics[glyphs.Length];
            var variantMetrics = new GlyphMetrics[glyphs.Length];

            Assert.True(root.TryGetGlyphMetrics(glyphs, rootMetrics));
            Assert.True(variant.TryGetGlyphMetrics(glyphs, variantMetrics));

            for (var i = 0; i < glyphs.Length; i++)
            {
                Assert.Equal(rootMetrics[i].AdvanceWidth, variantMetrics[i].AdvanceWidth);
                Assert.True(root.TryGetHorizontalGlyphAdvance(glyphs[i], out var rootAdvance));
                Assert.True(variant.TryGetHorizontalGlyphAdvance(glyphs[i], out var variantAdvance));
                Assert.Equal(rootAdvance, variantAdvance);
            }

            Assert.Equal(root.Metrics, variant.Metrics);
        }

        [Fact]
        public void Empty_Glyph_Stays_Empty_When_Simulated()
        {
            var root = LoadDejaVuSans();
            var variant = root.WithSimulations(FontSimulations.Bold | FontSimulations.Oblique);

            Assert.True(root.CharacterToGlyphMap.TryGetGlyph(' ', out var space));
            Assert.True(variant.TryGetGlyphMetrics(space, out var metrics));

            Assert.Equal(0, metrics.Width);
            Assert.Equal(0, metrics.Height);
        }

        private static IDisposable StartWithGeometry() =>
            UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(
                renderInterface: new PlatformRenderInterface()));

        private static Rect GetBounds(List<Point> points)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;

            foreach (var point in points)
            {
                minX = Math.Min(minX, point.X);
                minY = Math.Min(minY, point.Y);
                maxX = Math.Max(maxX, point.X);
                maxY = Math.Max(maxY, point.Y);
            }

            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        internal static ushort[] GetGlyphs(GlyphTypeface glyphTypeface)
        {
            var glyphs = new ushort[Text.Length];

            for (var i = 0; i < Text.Length; i++)
            {
                Assert.True(glyphTypeface.CharacterToGlyphMap.TryGetGlyph(Text[i], out glyphs[i]));
            }

            return glyphs;
        }

        internal static ushort[] GetMappedGlyphs(GlyphTypeface glyphTypeface)
        {
            var glyphs = new List<ushort>();

            foreach (var c in Text)
            {
                if (glyphTypeface.CharacterToGlyphMap.TryGetGlyph(c, out var glyph))
                {
                    glyphs.Add(glyph);
                }
            }

            Assert.NotEmpty(glyphs);

            return glyphs.ToArray();
        }

        private static GlyphTypeface LoadDejaVuSans() => Load(DejaVuSansUri);

        internal static GlyphTypeface Load(string uri)
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(uri));

            Assert.True(SfntFace.TryLoad(stream, out var face));

            return new GlyphTypeface(face);
        }
    }
}
