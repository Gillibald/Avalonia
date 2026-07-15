using System;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization
{
    public class RunMaskComposerTests
    {
        private static GlyphTypeface LoadInter()
        {
            var typeface = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular).TryCreateGlyphTypeface();
            Assert.NotNull(typeface);
            return typeface!;
        }

        private static GlyphMask BuildMask(GlyphTypeface typeface, char c, float ppem)
        {
            var key = new GlyphMaskKey(
                typeface.CharacterToGlyphMap[c], GlyphMaskKey.QuantizeScale(ppem), 0, GlyphMaskMode.Antialiased);

            var mask = GlyphMasks.Build(typeface, new GlyphPathBuilder(), key);
            Assert.False(mask.IsEmpty);
            return mask;
        }

        [Fact]
        public void Composing_A_Single_Mask_Copies_It_Exactly()
        {
            var typeface = LoadInter();
            var mask = BuildMask(typeface, 'g', 32f);

            var destination = new byte[mask.Width * mask.Height];
            RunMaskComposer.ComposeAlpha(mask, -mask.Left, -mask.Top, destination, mask.Width, mask.Height);

            Assert.True(destination.AsSpan().SequenceEqual(mask.Alpha));
        }

        [Fact]
        public void Composed_Run_Matches_Single_Pass_Rasterization()
        {
            // The same three glyphs once as composed cached masks and once as one combined path
            // rasterized in a single pass. Integer pens and phase zero keep the two float paths
            // aligned; tiny deviations can still arise from rounding-order differences at AA
            // edges, so the gate is: at most one level apart anywhere, almost everywhere exact.
            var typeface = LoadInter();
            const float ppem = 32f;
            const int width = 200, height = 64, penY = 44;
            var pens = new[] { 20, 60, 100 };
            var chars = new[] { 'A', 'v', 'g' };

            var composed = new byte[width * height];
            var scale = ppem / typeface.Metrics.DesignEmHeight;
            var combined = new GlyphPathBuilder();

            for (var i = 0; i < chars.Length; i++)
            {
                var mask = BuildMask(typeface, chars[i], ppem);
                RunMaskComposer.ComposeAlpha(mask, pens[i], penY, composed, width, height);

                var capture = new GlyphPathBuilder();
                var glyph = typeface.CharacterToGlyphMap[chars[i]];
                Assert.True(typeface.TryBuildGlyphContours(glyph, new Matrix(scale, 0, 0, -scale, 0, 0), capture));

                // The cached masks are grid-fit; give the direct path the identical warp so
                // this stays a composition test, not a hinting test.
                capture.ApplyVerticalWarp(typeface.GridFit.GetGlyphWarp(capture, GlyphMaskKey.QuantizeScale(ppem), typeface.StemWidths.HorizontalStrokeWidths));
                Replay(capture, combined, pens[i], penY);
            }

            var direct = new byte[width * height];
            GlyphRasterizer.Rasterize(combined, width, height, 0, 0, false, direct);

            var mismatched = 0;

            for (var i = 0; i < direct.Length; i++)
            {
                var delta = Math.Abs(composed[i] - direct[i]);
                Assert.True(delta <= 1, $"pixel {i}: composed {composed[i]} vs direct {direct[i]}");

                if (delta != 0)
                {
                    mismatched++;
                }
            }

            Assert.True(mismatched <= direct.Length / 100,
                $"{mismatched} of {direct.Length} pixels differ by one level");
        }

        [Fact]
        public void Chunked_Compose_Stitches_Exactly()
        {
            var typeface = LoadInter();
            const int width = 120, height = 60, chunk = 60, penY = 40;
            var pens = new[] { 10, 45, 80 };
            var chars = new[] { 'H', 'o', 'w' };

            var whole = new byte[width * height];
            var left = new byte[chunk * height];
            var right = new byte[chunk * height];

            for (var i = 0; i < chars.Length; i++)
            {
                var mask = BuildMask(typeface, chars[i], 28f);

                RunMaskComposer.ComposeAlpha(mask, pens[i], penY, whole, width, height);

                // Chunked path: same masks, pens rebased per chunk; the composer clips glyphs
                // that straddle the seam into each side.
                RunMaskComposer.ComposeAlpha(mask, pens[i], penY, left, chunk, height);
                RunMaskComposer.ComposeAlpha(mask, pens[i] - chunk, penY, right, chunk, height);
            }

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var stitched = x < chunk ? left[y * chunk + x] : right[y * chunk + x - chunk];
                    Assert.Equal(whole[y * width + x], stitched);
                }
            }
        }

        [Fact]
        public void Clipping_At_The_Buffer_Edges_Is_Safe_And_Consistent()
        {
            var typeface = LoadInter();
            var mask = BuildMask(typeface, 'Q', 32f);

            const int width = 24, height = 24;
            var clipped = new byte[width * height];

            // Pen far enough left/up that the mask hangs off both near edges.
            RunMaskComposer.ComposeAlpha(mask, -mask.Width / 2, -mask.Height / 2, clipped, width, height);

            // Reference: compose into a buffer large enough to hold everything, then compare the
            // overlapping window.
            var bigWidth = mask.Width * 2 + width;
            var bigHeight = mask.Height * 2 + height;
            var reference = new byte[bigWidth * bigHeight];
            RunMaskComposer.ComposeAlpha(
                mask, mask.Width - mask.Width / 2, mask.Height - mask.Height / 2, reference, bigWidth, bigHeight);

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    Assert.Equal(reference[(y + mask.Height) * bigWidth + x + mask.Width], clipped[y * width + x]);
                }
            }
        }

        [Fact]
        public void Overlapping_Coverage_Saturates()
        {
            var typeface = LoadInter();
            var mask = BuildMask(typeface, 'o', 24f);

            var destination = new byte[mask.Width * mask.Height];
            RunMaskComposer.ComposeAlpha(mask, -mask.Left, -mask.Top, destination, mask.Width, mask.Height);
            RunMaskComposer.ComposeAlpha(mask, -mask.Left, -mask.Top, destination, mask.Width, mask.Height);

            for (var i = 0; i < destination.Length; i++)
            {
                Assert.Equal(Math.Min(255, mask.Alpha[i] * 2), destination[i]);
            }
        }

        [Fact]
        public void Tinted_Compose_Applies_Premultiplied_Source_Over()
        {
            var mask = new GlyphMask(new byte[] { 255, 128, 0, 64 }, 2, 2, 0, 0);
            var tint = RunMaskComposer.MakeTint(255, 255, 0, 0);   // opaque red

            // Over an empty buffer: each pixel is tint * coverage.
            var empty = new byte[2 * 2 * 4];
            RunMaskComposer.ComposeTinted(mask, 0, 0, tint, empty, 2, 2);

            Assert.Equal(new byte[] { 0, 0, 255, 255 }, empty.AsSpan(0, 4).ToArray());
            Assert.Equal(new byte[] { 0, 0, 128, 128 }, empty.AsSpan(4, 4).ToArray());
            Assert.Equal(new byte[] { 0, 0, 0, 0 }, empty.AsSpan(8, 4).ToArray());

            // Over an opaque blue background: source-over at coverage 128 keeps half the blue.
            var background = new byte[2 * 2 * 4];

            for (var p = 0; p < 4; p++)
            {
                background[p * 4] = 255;
                background[p * 4 + 3] = 255;
            }

            RunMaskComposer.ComposeTinted(mask, 0, 0, tint, background, 2, 2);

            Assert.Equal(new byte[] { 0, 0, 255, 255 }, background.AsSpan(0, 4).ToArray());
            Assert.Equal(new byte[] { 127, 0, 128, 255 }, background.AsSpan(4, 4).ToArray());
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, background.AsSpan(8, 4).ToArray());
        }

        [Fact]
        public void MakeTint_Premultiplies_The_Color()
        {
            var tint = RunMaskComposer.MakeTint(128, 255, 0, 0);

            Assert.Equal(0u, tint & 0xFF);                    // B
            Assert.Equal(0u, (tint >> 8) & 0xFF);             // G
            Assert.Equal(128u, (tint >> 16) & 0xFF);          // R premultiplied by alpha
            Assert.Equal(128u, tint >> 24);                   // A
        }

        private static void Replay(GlyphPathBuilder source, GlyphPathBuilder destination, double dx, double dy)
        {
            var verbs = source.Verbs;
            var points = source.Points;
            var p = 0;

            for (var v = 0; v < verbs.Length; v++)
            {
                switch ((GlyphPathVerb)verbs[v])
                {
                    case GlyphPathVerb.MoveTo:
                        destination.BeginFigure(new Point(points[p++] + dx, points[p++] + dy));
                        break;
                    case GlyphPathVerb.LineTo:
                        destination.LineTo(new Point(points[p++] + dx, points[p++] + dy));
                        break;
                    case GlyphPathVerb.QuadTo:
                        destination.QuadraticBezierTo(
                            new Point(points[p++] + dx, points[p++] + dy),
                            new Point(points[p++] + dx, points[p++] + dy));
                        break;
                    case GlyphPathVerb.CubicTo:
                        destination.CubicBezierTo(
                            new Point(points[p++] + dx, points[p++] + dy),
                            new Point(points[p++] + dx, points[p++] + dy),
                            new Point(points[p++] + dx, points[p++] + dy));
                        break;
                    case GlyphPathVerb.Close:
                        destination.EndFigure(true);
                        break;
                }
            }
        }
    }
}
