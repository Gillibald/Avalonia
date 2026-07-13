using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization.Slug;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization.Slug
{
    public class SlugTexelSerializerTests
    {
        private static SlugGlyphData Encode(Action<SlugContourSink> draw,
            int? horizontalBandCount = null, int? verticalBandCount = null)
        {
            var sink = new SlugContourSink();

            draw(sink);

            var data = SlugBandEncoder.Encode(sink,
                horizontalBandCount: horizontalBandCount, verticalBandCount: verticalBandCount);

            Assert.NotNull(data);

            return data!;
        }

        private static void DrawStar(SlugContourSink sink, int teeth, double centerX, double centerY)
        {
            sink.BeginFigure(Vertex(0));

            for (var i = 1; i < teeth * 2; i++)
            {
                sink.LineTo(Vertex(i));
            }

            sink.EndFigure(true);

            Point Vertex(int i)
            {
                // Shallow teeth keep each edge's extent local; deep teeth would push every
                // horizontal band past the 64-curve decline threshold by design.
                var angle = Math.PI * i / teeth;
                var radius = i % 2 == 0 ? 1.0 : 0.9;

                return new Point(centerX + radius * Math.Cos(angle), centerY + radius * Math.Sin(angle));
            }
        }

        private static void DrawBlob(SlugContourSink sink)
        {
            sink.BeginFigure(new Point(0, 0));
            sink.QuadraticBezierTo(new Point(2, 0.5), new Point(0.2, 1));
            sink.QuadraticBezierTo(new Point(-2, 1.5), new Point(0, 2));
            sink.LineTo(new Point(1, 2));
            sink.QuadraticBezierTo(new Point(3, 1), new Point(1.2, 0.2));
            sink.EndFigure(true);
        }

        private static float Quantize(float value) => (float)(Half)value;

        /// <summary>
        /// Walks every band of a placed glyph exactly like the shader would and compares each
        /// fetched curve against the payload. Every curve sits in at least one band, so this
        /// covers chained end-point reads, row-break duplicates, and contour terminators.
        /// </summary>
        private static void AssertRoundTrip(
            SlugTexelSerializer serializer, SlugGlyphData data, SlugGlyphPlacement placement)
        {
            var curveTexels = serializer.CurveTexels;
            var bandTexels = serializer.BandTexels;
            var headerLength = placement.HorizontalBandCount + placement.VerticalBandCount;

            for (var band = 0; band < headerLength; band++)
            {
                var expected = band < placement.HorizontalBandCount
                    ? data.GetHorizontalBand(band)
                    : data.GetVerticalBand(band - placement.HorizontalBandCount);

                var (count, listX, listY) = SlugTexelDecoder.ReadBandHeader(
                    bandTexels, placement.GlyphLocX, placement.GlyphLocY, band);

                Assert.Equal(expected.Length, count);

                for (var k = 0; k < count; k++)
                {
                    var (x, y) = SlugTexelDecoder.ReadListEntry(bandTexels, listX, listY, k);
                    var decoded = SlugTexelDecoder.ReadCurve(curveTexels, x, y);
                    var source = data.GetCurve(expected[k]);

                    Assert.Equal(Quantize(source.X1), decoded.X1);
                    Assert.Equal(Quantize(source.Y1), decoded.Y1);
                    Assert.Equal(Quantize(source.X2), decoded.X2);
                    Assert.Equal(Quantize(source.Y2), decoded.Y2);
                    Assert.Equal(Quantize(source.X3), decoded.X3);
                    Assert.Equal(Quantize(source.Y3), decoded.Y3);
                }
            }
        }

        [Fact]
        public void Serialized_Payloads_Round_Trip_Through_The_Decoder()
        {
            var serializer = new SlugTexelSerializer();
            var glyphs = new List<(SlugGlyphData Data, SlugGlyphPlacement Placement)>();

            void Add(SlugGlyphData data)
            {
                Assert.True(serializer.TryAdd(data, out var placement));
                glyphs.Add((data, placement));
            }

            for (var i = 0; i < 10; i++)
            {
                var center = i;

                Add(Encode(sink => DrawStar(sink, 120, center, 0)));
            }

            Add(Encode(DrawBlob));

            Add(Encode(sink =>
            {
                sink.BeginFigure(new Point(0, 0));
                sink.LineTo(new Point(1, 0));
                sink.EndFigure(true);
            }));

            Add(Encode(sink =>
            {
                sink.BeginFigure(new Point(0, 0));
                sink.QuadraticBezierTo(new Point(1, 1), new Point(0, 0));
                sink.EndFigure(true);
            }));

            Add(Encode(sink =>
            {
                sink.BeginFigure(new Point(0, 0));
                sink.QuadraticBezierTo(new Point(0.5, 10), new Point(1, 0));
                sink.EndFigure(true);
            }));

            // The star fleet spans several texture rows, so mid-chain row breaks and padded
            // band lists are genuinely exercised, not just possible.
            Assert.True(serializer.CurveRowCount >= 2, "Corpus too small to cross a curve row.");
            Assert.True(serializer.BandRowCount >= 2, "Corpus too small to cross a band row.");

            foreach (var (data, placement) in glyphs)
            {
                AssertRoundTrip(serializer, data, placement);
            }
        }

        [Fact]
        public void Oversized_Band_Lists_Are_Declined()
        {
            // A comb of 70 full-height verticals puts 70 curves into every horizontal band —
            // beyond the shader loop bound, so the glyph must be declined untouched.
            var data = Encode(sink =>
            {
                sink.BeginFigure(new Point(0, 0));

                var x = 0.0;

                for (var i = 0; i < 70; i++)
                {
                    var top = i % 2 == 0;

                    sink.LineTo(new Point(x, top ? 1 : 0));
                    x += 0.01;
                    sink.LineTo(new Point(x, top ? 1 : 0));
                }

                sink.EndFigure(true);
            });

            var serializer = new SlugTexelSerializer();

            Assert.False(serializer.TryAdd(data, out _));
            Assert.Equal(0, serializer.CurveRowCount);
            Assert.Equal(0, serializer.BandRowCount);
        }

        [Fact]
        public void Overspanning_Band_Blobs_Are_Declined()
        {
            // Sixty near-corner-to-corner diagonals overlap every band of both families; with
            // 32 forced bands per axis that is ~3900 list texels — beyond the linear span a
            // half-float header offset can address.
            var data = Encode(sink =>
            {
                sink.BeginFigure(new Point(0, 0));

                for (var i = 1; i < 60; i++)
                {
                    sink.LineTo(i % 2 == 1 ? new Point(1, 1 - i * 1e-6) : new Point(0, i * 1e-6));
                }

                sink.EndFigure(true);
            }, horizontalBandCount: 32, verticalBandCount: 32);

            var serializer = new SlugTexelSerializer();

            Assert.False(serializer.TryAdd(data, out _));
            Assert.Equal(0, serializer.CurveRowCount);
        }

        [Fact]
        public void The_Texture_Row_Cap_Declines_Further_Glyphs()
        {
            var serializer = new SlugTexelSerializer(maxTextureRows: 1);
            var declined = false;

            for (var i = 0; i < 20 && !declined; i++)
            {
                var center = i;

                declined = !serializer.TryAdd(Encode(sink => DrawStar(sink, 120, center, 0)), out _);
            }

            Assert.True(declined, "The one-row serializer never filled up.");
            Assert.True(serializer.CurveRowCount <= 1);
            Assert.True(serializer.BandRowCount <= 1);
        }

        [Fact]
        public void Placement_Carries_The_Band_Transform_And_Fill_Rule()
        {
            var serializer = new SlugTexelSerializer();

            var blob = Encode(sink =>
            {
                sink.SetFillRule(FillRule.EvenOdd);
                DrawBlob(sink);
            });

            Assert.True(serializer.TryAdd(blob, out var placement));
            Assert.True(placement.EvenOdd);
            Assert.Equal(blob.HorizontalBandCount, placement.HorizontalBandCount);
            Assert.Equal(blob.VerticalBandCount, placement.VerticalBandCount);
            Assert.Equal(blob.HorizontalBandCount / (blob.MaxY - blob.MinY), placement.BandScaleY);
            Assert.Equal(blob.VerticalBandCount / (blob.MaxX - blob.MinX), placement.BandScaleX);
            Assert.Equal(-blob.MinY * placement.BandScaleY, placement.BandOffsetY);
            Assert.Equal(-blob.MinX * placement.BandScaleX, placement.BandOffsetX);

            // A flat axis cannot be scaled into band indices; the transform collapses to zero
            // and the shader's clamp keeps the single band selected.
            var flat = Encode(sink =>
            {
                sink.BeginFigure(new Point(0, 0));
                sink.LineTo(new Point(1, 0));
                sink.EndFigure(true);
            });

            Assert.True(serializer.TryAdd(flat, out var flatPlacement));
            Assert.False(flatPlacement.EvenOdd);
            Assert.Equal(0, flatPlacement.BandScaleY);
            Assert.Equal(0, flatPlacement.BandOffsetY);
        }
    }
}
