using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.TextFormatting;
using Avalonia.UnitTests;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// The light auto-fit of fonts without bytecode keeps the strokes of dense ideographs apart:
    /// over text sizes, the hinted mask keeps the gaps between stacked strokes and the enclosed
    /// counters that the unhinted mask of the same glyph and size has.
    /// </summary>
    public class AutoFitIdeographTests
    {
        private const double MinSize = 10;
        private const double MaxSize = 24;
        private const double SizeStep = 0.5;
        private const int SolidInk = 128;

        /// <summary>The share of separations and enclosed counters the hinted masks may lose.</summary>
        private const double AllowedLoss = 0.03;

        [Fact]
        public void Auto_Fit_Keeps_Dense_Ideograph_Strokes_Apart()
        {
            using var app = StartApp();

            var typeface = DenseIdeographFont.Create(out var glyphs);

            AssertStrokesKept(typeface, glyphs, "synthetic");
        }

        [Fact]
        public void Auto_Fit_Keeps_Latin_Zones_For_Latin_Glyphs_In_A_Font_With_Ideographs()
        {
            // The Latin glyphs of the same font still snap their tops to the zone rows: the cap's
            // top row carries as much ink as the row below it, where the unhinted cap height
            // (9.49 px at 13 px) leaves the top row half covered.
            using var app = StartApp();

            var typeface = DenseIdeographFont.Create(out _);
            var glyph = typeface.CharacterToGlyphMap['H'];

            var hinted = TopRowCoverage(Render(typeface, glyph, 13, TextHintingMode.Light));
            var unhinted = TopRowCoverage(Render(typeface, glyph, 13, TextHintingMode.None));

            Assert.True(hinted >= 0.9, $"the hinted cap's top row is {hinted:P0} covered");
            Assert.True(unhinted < 0.75, $"the unhinted cap's top row is {unhinted:P0} covered");
        }

        public static IEnumerable<object[]> SystemFonts()
        {
            // Fonts without bytecode that take the auto-fit; macOS only.
            yield return new object[] { "/System/Library/Fonts/ヒラギノ角ゴシック W3.ttc", 0, "疆鷹驚鬱" };
            yield return new object[] { "/System/Library/Fonts/ヒラギノ角ゴシック W6.ttc", 0, "疆鷹驚鬱" };
            yield return new object[] { "/System/Library/Fonts/Hiragino Sans GB.ttc", 0, "疆鷹齉驚" };
            yield return new object[] { "/System/Library/Fonts/Supplemental/Songti.ttc", 6, "疆鷹齉驚" };
        }

        [Theory]
        [MemberData(nameof(SystemFonts))]
        public void Auto_Fit_Keeps_Dense_Ideograph_Strokes_Apart_In_System_Fonts(string path, int face, string text)
        {
            Assert.SkipUnless(File.Exists(path), $"{path} is not installed.");

            using var app = StartApp();

            var typeface = TestGlyphTypefaces.FromBytes(File.ReadAllBytes(path), face);

            Assert.False(typeface.HasTrueTypeHinting);

            var glyphs = text.Select(c => typeface.CharacterToGlyphMap[c]).ToArray();

            Assert.DoesNotContain((ushort)0, glyphs);

            AssertStrokesKept(typeface, glyphs, Path.GetFileName(path));
        }

        private static void AssertStrokesKept(GlyphTypeface typeface, IReadOnlyList<ushort> glyphs, string label)
        {
            long referenceSeparations = 0, keptSeparations = 0, referenceHoles = 0, keptHoles = 0;
            var worst = (Loss: 0.0, Glyph: (ushort)0, Size: 0.0);

            foreach (var glyph in glyphs)
            {
                for (var size = MinSize; size <= MaxSize + 0.001; size += SizeStep)
                {
                    var unhinted = Render(typeface, glyph, size, TextHintingMode.None);
                    var hinted = Render(typeface, glyph, size, TextHintingMode.Light);

                    var separations = Separations(unhinted);
                    var holes = Holes(unhinted);
                    var hintedSeparations = Separations(hinted);

                    referenceSeparations += separations;
                    keptSeparations += Math.Min(separations, hintedSeparations);
                    referenceHoles += holes;
                    keptHoles += Math.Min(holes, Holes(hinted));

                    var loss = separations == 0 ? 0 : 1 - Math.Min(separations, hintedSeparations) / (double)separations;

                    if (loss > worst.Loss)
                    {
                        worst = (loss, glyph, size);
                    }
                }
            }

            var separationLoss = 1 - keptSeparations / (double)referenceSeparations;
            var holeLoss = referenceHoles == 0 ? 0 : 1 - keptHoles / (double)referenceHoles;

            Assert.True(separationLoss <= AllowedLoss && holeLoss <= AllowedLoss,
                $"{label}: lost {separationLoss:P1} of {referenceSeparations} stroke separations and " +
                $"{holeLoss:P1} of {referenceHoles} enclosed counters; worst glyph {worst.Glyph} at " +
                $"{worst.Size} px lost {worst.Loss:P0} of its separations");
        }

        /// <summary>A grayscale ink mask: 0 is paper, 255 full ink.</summary>
        private readonly record struct Mask(int Width, int Height, byte[] Ink);

        private static Mask Render(GlyphTypeface typeface, ushort glyph, double size, TextHintingMode hinting)
        {
            var extent = (int)Math.Ceiling(size * 1.6) + 8;
            var info = new SKImageInfo(extent, extent, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(info);

            var scale = size / typeface.Metrics.DesignEmHeight;

            typeface.TryGetGlyphMetrics(glyph, out var metrics);

            using var run = new ManagedGlyphRunImpl(typeface, size,
                new List<GlyphInfo> { new(glyph, 0, metrics.AdvanceWidth * scale) },
                new Point(4, Math.Round(size * 1.15) + 4));

            using (var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
                   {
                       Surface = surface,
                       Canvas = surface.Canvas,
                       Dpi = new Vector(96, 96),
                   }))
            {
                surface.Canvas.Clear(SKColors.White);
                context.PushTextOptions(new TextOptions
                {
                    TextHintingMode = hinting,
                    TextRenderingMode = TextRenderingMode.Antialias,
                });
                context.DrawGlyphRun(Brushes.Black, run);
                context.PopTextOptions();
            }

            using var snapshot = surface.Snapshot();
            using var bitmap = new SKBitmap(info);

            Assert.True(snapshot.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0));

            var ink = new byte[extent * extent];

            for (var y = 0; y < extent; y++)
            {
                for (var x = 0; x < extent; x++)
                {
                    ink[y * extent + x] = (byte)(255 - bitmap.GetPixel(x, y).Green);
                }
            }

            return new Mask(extent, extent, ink);
        }

        /// <summary>The gaps between runs of solid ink, summed over every row and column.</summary>
        private static int Separations(Mask mask)
        {
            var total = 0;

            for (var y = 0; y < mask.Height; y++)
            {
                total += Gaps(mask, y * mask.Width, 1, mask.Width);
            }

            for (var x = 0; x < mask.Width; x++)
            {
                total += Gaps(mask, x, mask.Width, mask.Height);
            }

            return total;
        }

        private static int Gaps(Mask mask, int start, int step, int count)
        {
            var runs = 0;
            var inside = false;

            for (var i = 0; i < count; i++)
            {
                var solid = mask.Ink[start + i * step] >= SolidInk;

                if (solid && !inside)
                {
                    runs++;
                }

                inside = solid;
            }

            return Math.Max(0, runs - 1);
        }

        /// <summary>The regions of paper enclosed by ink, which do not reach the mask's border.</summary>
        private static int Holes(Mask mask)
        {
            var seen = new bool[mask.Ink.Length];
            var holes = 0;
            var stack = new Stack<int>();

            for (var start = 0; start < mask.Ink.Length; start++)
            {
                if (seen[start] || mask.Ink[start] >= SolidInk)
                {
                    continue;
                }

                var border = false;

                seen[start] = true;
                stack.Push(start);

                while (stack.Count > 0)
                {
                    var index = stack.Pop();
                    var x = index % mask.Width;
                    var y = index / mask.Width;

                    border |= x == 0 || y == 0 || x == mask.Width - 1 || y == mask.Height - 1;

                    Visit(x + 1, y);
                    Visit(x - 1, y);
                    Visit(x, y + 1);
                    Visit(x, y - 1);
                }

                if (!border)
                {
                    holes++;
                }
            }

            return holes;

            void Visit(int x, int y)
            {
                if (x < 0 || y < 0 || x >= mask.Width || y >= mask.Height)
                {
                    return;
                }

                var index = y * mask.Width + x;

                if (!seen[index] && mask.Ink[index] < SolidInk)
                {
                    seen[index] = true;
                    stack.Push(index);
                }
            }
        }

        /// <summary>The ink of the topmost inked row relative to the row below it.</summary>
        private static double TopRowCoverage(Mask mask)
        {
            var rows = new double[mask.Height];

            for (var y = 0; y < mask.Height; y++)
            {
                for (var x = 0; x < mask.Width; x++)
                {
                    rows[y] += mask.Ink[y * mask.Width + x];
                }
            }

            var top = Array.FindIndex(rows, row => row > rows.Max() * 0.02);

            return rows[top] / rows[top + 1];
        }

        private static IDisposable StartApp()
            => UnitTestApplication.Start(TestServices.MockPlatformRenderInterface
                .With(renderInterface: new PlatformRenderInterface(null)));

        /// <summary>
        /// A small glyf font without bytecode, built from the tables of the bundled Noto Sans SC
        /// subset: Latin reference glyphs that give the auto-fit its zones (x, H, l, p, o, f), a
        /// plain ideograph spanning the ideographic em box for common Han characters, and two
        /// dense ideographs of stacked horizontal strokes at CJK spacing. Each dense ideograph is
        /// two frames side by side whose bars sit at different heights, so their edges interleave
        /// in y and their counters are enclosed.
        /// </summary>
        private static class DenseIdeographFont
        {
            private const int Em = 1000;

            public static GlyphTypeface Create(out ushort[] ideographs)
            {
                var glyphs = new List<(int CodePoint, short[][] Contours)>
                {
                    ('x', new[] { Box(40, 0, 480, 520) }),
                    ('H', new[] { Box(60, 0, 140, 730), Box(420, 0, 500, 730), Box(60, 330, 500, 400) }),
                    ('l', new[] { Box(60, 0, 140, 760) }),
                    ('p', new[] { Box(60, -210, 140, 520), Box(60, 440, 460, 520), Box(380, 0, 460, 520), Box(60, 0, 460, 80) }),
                    ('o', new[] { Box(40, -12, 120, 532), Box(400, -12, 480, 532), Box(40, -12, 480, 68), Box(40, 452, 480, 532) }),
                    ('f', new[] { Box(60, 0, 140, 770), Box(20, 440, 300, 510) }),
                    (0x7586, Ideograph(barsLeft: 11, pitchLeft: 80, thicknessLeft: 42, barsRight: 9, pitchRight: 95, thicknessRight: 48)),
                    (0x9DF9, Ideograph(barsLeft: 12, pitchLeft: 72, thicknessLeft: 36, barsRight: 10, pitchRight: 86, thicknessRight: 40)),
                };

                foreach (var common in "他们你們到和地")
                {
                    glyphs.Add((common, new[] { Box(80, -70, 920, 830) }));
                }

                var bytes = File.ReadAllBytes(FontPath("NotoSansSC-Subset.ttf"));
                var font = SyntheticFont.FromBytes(bytes);
                var source = font.GetTable("glyf");
                var sourceLoca = font.GetTable("loca");
                var notdefLength = (BinaryPrimitives.ReadUInt16BigEndian(sourceLoca.AsSpan(2)) -
                                    BinaryPrimitives.ReadUInt16BigEndian(sourceLoca)) * 2;

                var glyf = new MemoryStream();
                var offsets = new List<int> { 0 };

                glyf.Write(source, 0, notdefLength);
                offsets.Add((int)glyf.Length);

                foreach (var (_, contours) in glyphs)
                {
                    WriteGlyph(glyf, contours);

                    if (glyf.Length % 2 != 0)
                    {
                        glyf.WriteByte(0);
                    }

                    offsets.Add((int)glyf.Length);
                }

                var numGlyphs = glyphs.Count + 1;
                var loca = new byte[offsets.Count * 2];

                for (var i = 0; i < offsets.Count; i++)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(loca.AsSpan(i * 2), (ushort)(offsets[i] / 2));
                }

                var hmtx = new byte[numGlyphs * 4];

                for (var i = 0; i < numGlyphs; i++)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(i * 4), i is >= 1 and <= 6 ? (ushort)560 : (ushort)Em);
                }

                font.Replace("glyf", glyf.ToArray())
                    .Replace("loca", loca)
                    .Replace("hmtx", hmtx)
                    .Replace("cmap", Cmap(glyphs.Select((g, i) => (g.CodePoint, (ushort)(i + 1))).ToList()))
                    .Mutate("hhea", table => BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(34), (ushort)numGlyphs))
                    .Mutate("maxp", table => BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4), (ushort)numGlyphs));

                foreach (var tag in new[] { "prep", "fpgm", "cvt ", "gasp", "GSUB", "GPOS", "BASE", "vhea", "vmtx" })
                {
                    if (font.Contains(tag))
                    {
                        font.Remove(tag);
                    }
                }

                var typeface = font.TryCreateGlyphTypeface();

                Assert.NotNull(typeface);
                Assert.False(typeface!.HasTrueTypeHinting);

                ideographs = new[] { typeface.CharacterToGlyphMap[0x7586], typeface.CharacterToGlyphMap[0x9DF9] };

                Assert.DoesNotContain((ushort)0, ideographs);

                return typeface;
            }

            private static short[][] Ideograph(int barsLeft, int pitchLeft, int thicknessLeft,
                int barsRight, int pitchRight, int thicknessRight)
            {
                var contours = new List<short[]>();

                Frame(contours, 60, 460, -70, barsLeft, pitchLeft, thicknessLeft);
                Frame(contours, 540, 940, -60, barsRight, pitchRight, thicknessRight);

                return contours.ToArray();
            }

            private static void Frame(List<short[]> contours, int left, int right, int bottom, int bars, int pitch,
                int thickness)
            {
                var top = bottom + (bars - 1) * pitch + thickness;

                contours.Add(Box(left, bottom, left + 50, top));
                contours.Add(Box(right - 50, bottom, right, top));

                for (var i = 0; i < bars; i++)
                {
                    var y = bottom + i * pitch;

                    contours.Add(Box(left, y, right, y + thickness));
                }
            }

            /// <summary>A clockwise rectangle, the winding of an outer TrueType contour.</summary>
            private static short[] Box(int x0, int y0, int x1, int y1)
                => new[] { (short)x0, (short)y0, (short)x0, (short)y1, (short)x1, (short)y1, (short)x1, (short)y0 };

            private static void WriteGlyph(Stream stream, short[][] contours)
            {
                var points = contours.SelectMany(c => Enumerable.Range(0, c.Length / 2).Select(i => (X: c[i * 2], Y: c[i * 2 + 1]))).ToList();
                var buffer = new List<byte>();

                void Int16(int value)
                {
                    buffer.Add((byte)(value >> 8));
                    buffer.Add((byte)value);
                }

                Int16(contours.Length);
                Int16(points.Min(p => p.X));
                Int16(points.Min(p => p.Y));
                Int16(points.Max(p => p.X));
                Int16(points.Max(p => p.Y));

                var end = -1;

                foreach (var contour in contours)
                {
                    end += contour.Length / 2;
                    Int16(end);
                }

                Int16(0);

                foreach (var _ in points)
                {
                    buffer.Add(0x01);
                }

                var previous = 0;

                foreach (var point in points)
                {
                    Int16(point.X - previous);
                    previous = point.X;
                }

                previous = 0;

                foreach (var point in points)
                {
                    Int16(point.Y - previous);
                    previous = point.Y;
                }

                stream.Write(buffer.ToArray());
            }

            /// <summary>A Windows Unicode BMP cmap, format 4, one segment per mapping.</summary>
            private static byte[] Cmap(List<(int CodePoint, ushort Glyph)> mappings)
            {
                var sorted = mappings.OrderBy(m => m.CodePoint).ToList();
                var segments = sorted.Count + 1;
                var subtableLength = 16 + segments * 8;
                var table = new byte[12 + subtableLength];
                var span = table.AsSpan();

                BinaryPrimitives.WriteUInt16BigEndian(span[2..], 1);
                BinaryPrimitives.WriteUInt16BigEndian(span[4..], 3);
                BinaryPrimitives.WriteUInt16BigEndian(span[6..], 1);
                BinaryPrimitives.WriteUInt32BigEndian(span[8..], 12);

                var sub = span[12..];
                var searchRange = 2 * (1 << (int)Math.Floor(Math.Log2(segments)));

                BinaryPrimitives.WriteUInt16BigEndian(sub, 4);
                BinaryPrimitives.WriteUInt16BigEndian(sub[2..], (ushort)subtableLength);
                BinaryPrimitives.WriteUInt16BigEndian(sub[6..], (ushort)(segments * 2));
                BinaryPrimitives.WriteUInt16BigEndian(sub[8..], (ushort)searchRange);
                BinaryPrimitives.WriteUInt16BigEndian(sub[10..], (ushort)Math.Log2(searchRange / 2));
                BinaryPrimitives.WriteUInt16BigEndian(sub[12..], (ushort)(segments * 2 - searchRange));

                var ends = sub[14..];
                var starts = sub[(16 + segments * 2)..];
                var deltas = sub[(16 + segments * 4)..];

                for (var i = 0; i < segments; i++)
                {
                    var (codePoint, glyph) = i < sorted.Count ? sorted[i] : (0xFFFF, (ushort)0);

                    BinaryPrimitives.WriteUInt16BigEndian(ends[(i * 2)..], (ushort)codePoint);
                    BinaryPrimitives.WriteUInt16BigEndian(starts[(i * 2)..], (ushort)codePoint);
                    BinaryPrimitives.WriteUInt16BigEndian(deltas[(i * 2)..],
                        i < sorted.Count ? (ushort)(glyph - codePoint) : (ushort)1);
                }

                return table;
            }

            private static string FontPath(string fileName)
            {
                var directory = new DirectoryInfo(AppContext.BaseDirectory);

                while (directory is not null && directory.Name != "tests")
                {
                    directory = directory.Parent;
                }

                Assert.NotNull(directory);

                return Path.Combine(directory!.FullName, "Avalonia.Skia.UnitTests", "Fonts", fileName);
            }
        }
    }
}
