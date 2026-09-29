using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Fonts.Tables.Glyf;
using Avalonia.Platform;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// <see cref="OutlineEmbolden"/> and simulated bold outlines against FreeType's
    /// FT_Outline_EmboldenXY, recorded in <see cref="FreeTypeEmboldenReference"/>.
    /// </summary>
    /// <remarks>
    /// FreeType moves every point by half the strength towards the upper right after the miter
    /// offset, so the outline's lower-left extreme stays put. The port grows the outline evenly on
    /// every side instead, so the reference is compared after undoing that shift.
    /// </remarks>
    public class OutlineEmboldenReferenceTests
    {
        // FreeType works in integer FT_Pos with 16.16 unit vectors: each shift is rounded to one
        // FT_Pos (1 / Scale font units here) and the edge directions carry about 2^-16 relative
        // error, which the collapse limiter's division by the turn amplifies to a little over two
        // FT_Pos at 1/8 em. The double-precision port matches within that.
        private const double Tolerance = 4.0 / FreeTypeEmboldenReference.Scale;

        private const string DejaVuSansUri =
            "resm:Avalonia.Skia.UnitTests.Fonts.DejaVuSans.ttf?assembly=Avalonia.Skia.UnitTests";

        private const string SourceCodeProUri =
            "resm:Avalonia.Skia.UnitTests.Fonts.SourceCodePro-Subset.otf?assembly=Avalonia.Skia.UnitTests";

        public static IEnumerable<object[]> Cases()
        {
            foreach (var glyph in FreeTypeEmboldenReference.Glyphs)
            {
                foreach (var emboldened in glyph.Emboldened)
                {
                    yield return new object[] { glyph.Font, glyph.Character.ToString(), emboldened.Divisor };
                }
            }
        }

        public static IEnumerable<object[]> Glyphs() =>
            FreeTypeEmboldenReference.Glyphs.Select(g => new object[] { g.Font, g.Character.ToString() });

        [Theory]
        [MemberData(nameof(Cases))]
        public void Embolden_Matches_FreeType(string font, string character, int divisor)
        {
            var glyph = Find(font, character);
            var strength = (double)glyph.UnitsPerEm / divisor;

            var points = new Point[glyph.Points.Length / 2];

            for (var i = 0; i < points.Length; i++)
            {
                points[i] = new Point(glyph.Points[2 * i], glyph.Points[2 * i + 1]);
            }

            OutlineEmbolden.Embolden(points, glyph.ContourEnds, strength, strength);

            var expected = GetReference(glyph, divisor);
            var failures = new List<string>();

            for (var i = 0; i < points.Length; i++)
            {
                if (!IsClose(points[i], expected[i]))
                {
                    failures.Add($"point {i}: expected {expected[i]}, got {points[i]}");
                }
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        /// <summary>
        /// The bold variant's outline is the FreeType-emboldened outline at 1/24 em, the strength the
        /// outline documents as the renderer's strongest.
        /// </summary>
        [Theory]
        [MemberData(nameof(Glyphs))]
        public void Bold_Outline_Matches_FreeType(string font, string character)
        {
            using var app = UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(
                renderInterface: new PlatformRenderInterface()));

            var glyph = Find(font, character);
            var root = Load(font == "DejaVuSans" ? DejaVuSansUri : SourceCodeProUri);

            Assert.Equal(glyph.UnitsPerEm, root.Metrics.DesignEmHeight);
            Assert.Equal(glyph.GlyphIndex, root.CharacterToGlyphMap[glyph.Character]);

            var variant = root.WithSimulations(FontSimulations.Bold);
            var outline = GetOutlinePoints(variant.GetGlyphOutline((ushort)glyph.GlyphIndex));

            var reference = GetReference(glyph, 24);
            var expected = WithImpliedOnCurvePoints(glyph, reference);
            var failures = new List<string>();

            foreach (var point in outline)
            {
                if (!expected.Any(e => IsClose(point, e)))
                {
                    failures.Add($"outline point {point} is not in the reference outline");
                }
            }

            foreach (var point in reference)
            {
                if (!outline.Any(o => IsClose(o, point)))
                {
                    failures.Add($"reference point {point} is missing from the outline");
                }
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        internal static List<Point> GetOutlinePoints(IGeometryImpl? geometry)
        {
            Assert.NotNull(geometry);

            var inner = Assert.IsAssignableFrom<GeometryImpl>(((ImmutableGeometryImpl)geometry!).Inner);

            return inner.FillPath!.Points.Select(p => new Point(p.X, p.Y)).ToList();
        }

        private static Point[] GetReference(FreeTypeEmboldenReference.Glyph glyph, int divisor)
        {
            var emboldened = glyph.Emboldened.Single(e => e.Divisor == divisor);
            var halfStrength = (double)glyph.UnitsPerEm / divisor / 2;
            var points = new Point[emboldened.Points.Length / 2];

            for (var i = 0; i < points.Length; i++)
            {
                points[i] = new Point(
                    (double)emboldened.Points[2 * i] / FreeTypeEmboldenReference.Scale - halfStrength,
                    (double)emboldened.Points[2 * i + 1] / FreeTypeEmboldenReference.Scale - halfStrength);
            }

            return points;
        }

        // A TrueType contour places an implied on-curve point midway between consecutive conic
        // control points, which the path carries as an explicit point.
        private static List<Point> WithImpliedOnCurvePoints(FreeTypeEmboldenReference.Glyph glyph, Point[] points)
        {
            const byte conic = 0;

            var result = new List<Point>(points);
            var first = 0;

            foreach (var last in glyph.ContourEnds)
            {
                for (var i = first; i <= last; i++)
                {
                    var next = i == last ? first : i + 1;

                    if (glyph.Tags[i] == conic && glyph.Tags[next] == conic)
                    {
                        result.Add(new Point((points[i].X + points[next].X) / 2, (points[i].Y + points[next].Y) / 2));
                    }
                }

                first = last + 1;
            }

            return result;
        }

        private static bool IsClose(Point a, Point b) =>
            Math.Abs(a.X - b.X) <= Tolerance && Math.Abs(a.Y - b.Y) <= Tolerance;

        private static FreeTypeEmboldenReference.Glyph Find(string font, string character) =>
            FreeTypeEmboldenReference.Glyphs.Single(g => g.Font == font && g.Character.ToString() == character);

        private static GlyphTypeface Load(string uri)
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(uri));

            Assert.True(SfntFace.TryLoad(stream, out var face));

            return new GlyphTypeface(face);
        }
    }
}
