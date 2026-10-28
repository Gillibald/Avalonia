using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Skia.Helpers;
using Avalonia.UnitTests;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Bitmap strike glyphs under backend rasterization: text records its colour glyphs on the UI
    /// thread without knowing the device scale, so the strike is chosen where the glyph is drawn,
    /// from the device transform, the same way the managed mask path chooses it.
    /// </summary>
    public class BackendBitmapStrikeTests
    {
        private const int SmallStrike = 20;
        private const int LargeStrike = 40;
        private const double EmSize = 20;
        private static readonly Point s_pen = new(8, 40);

        [Fact]
        public void Recording_A_Bitmap_Glyph_Decodes_No_Strike_Image()
        {
            using var scope = CreateEnvironment(out var decoder);
            var typeface = CreateTwoStrikeTypeface(out var glyph);
            using var run = CreateRun(typeface, glyph);

            using var recording = DrawingRecording.Create(context =>
                Assert.True(ColorGlyphRunSplitter.TryDraw(context, run, Brushes.Black),
                    "backend mode declined a bitmap-strike run"));

            Assert.Empty(decoder.DecodedWidths);
        }

        [Fact]
        public void Asking_Whether_A_Bitmap_Glyph_Draws_As_Colour_Decodes_Nothing()
        {
            using var scope = CreateEnvironment(out var decoder);
            var typeface = CreateTwoStrikeTypeface(out var glyph);

            Assert.True(ColorGlyphRunSplitter.IsDrawnAsColor(typeface, glyph));
            Assert.Empty(decoder.DecodedWidths);
        }

        [Theory]
        [InlineData(1.0, SmallStrike)]
        [InlineData(1.5, LargeStrike)]
        [InlineData(2.0, LargeStrike)]
        public void Replay_Decodes_The_Smallest_Strike_At_Or_Above_The_Device_Size(double scale, int strike)
        {
            using var scope = CreateEnvironment(out var decoder);
            var typeface = CreateTwoStrikeTypeface(out var glyph);
            using var run = CreateRun(typeface, glyph);
            using var recording = Record(run);

            Replay(recording, scale);

            Assert.Equal(new[] { strike }, decoder.DecodedWidths);
        }

        [Fact]
        public void A_Recording_Replayed_At_Another_Scale_Decodes_That_Scales_Strike_Once()
        {
            using var scope = CreateEnvironment(out var decoder);
            var typeface = CreateTwoStrikeTypeface(out var glyph);
            using var run = CreateRun(typeface, glyph);
            using var recording = Record(run);

            // A window moving between monitors replays the same render data at the new scale.
            Replay(recording, 1.0);
            Replay(recording, 1.0);
            Replay(recording, 2.0);
            Replay(recording, 2.0);
            Replay(recording, 1.0);

            Assert.Equal(new[] { SmallStrike, LargeStrike }, decoder.DecodedWidths);
        }

        [Fact]
        public void Replay_Draws_The_Strike_Filtered_To_The_Device_Size()
        {
            using var scope = CreateEnvironment(out _);
            var typeface = CreateTwoStrikeTypeface(out var glyph);
            using var run = CreateRun(typeface, glyph);
            using var recording = Record(run);

            // 20 px em at 1.5x is 30 device px: the 40 ppem strike, scaled by 0.75 with the
            // filtered strike scaler, placed on the device pixel grid at the pen (12, 60).
            var pixels = Replay(recording, 1.5);

            AssertStrikeAt(pixels, LargeStrike, 30, x: 12, y: 30);
        }

        [Fact]
        public void Drawing_Directly_On_A_Platform_Context_Uses_The_Device_Size_Strike()
        {
            using var scope = CreateEnvironment(out var decoder);
            var typeface = CreateTwoStrikeTypeface(out var glyph);
            using var run = CreateRun(typeface, glyph);

            var pixels = Draw(1.5, context => Assert.True(ColorGlyphRunSplitter.TryDraw(context, run, Brushes.Black)));

            AssertStrikeAt(pixels, LargeStrike, 30, x: 12, y: 30);
            Assert.Equal(new[] { LargeStrike }, decoder.DecodedWidths);
        }

        [Fact]
        public void The_Strike_At_The_Device_Size_Is_Copied_Unfiltered()
        {
            using var scope = CreateEnvironment(out _);
            var typeface = CreateTwoStrikeTypeface(out var glyph);
            using var run = CreateRun(typeface, glyph);
            using var recording = Record(run);

            var pixels = Replay(recording, 1.0);

            AssertStrikeAt(pixels, SmallStrike, SmallStrike, x: 8, y: 20);
        }

        [Fact]
        public void Recorded_Bounds_Cover_The_Glyph_At_Every_Strike()
        {
            using var scope = CreateEnvironment(out _);
            var typeface = CreateTwoStrikeTypeface(out var glyph);
            using var run = CreateRun(typeface, glyph);
            using var recording = Record(run);

            // Both strikes cover the em square above the baseline: 20 x 20 DIPs at the pen.
            Assert.True(recording.Bounds.Contains(new Rect(s_pen.X, s_pen.Y - EmSize, EmSize, EmSize)),
                $"recording bounds {recording.Bounds} miss the glyph");
        }

        [Fact]
        public void Recording_An_Sbix_Glyph_Takes_Its_Bounds_From_The_Png_Header()
        {
            using var scope = CreateEnvironment(out var decoder);

            // One 32 ppem strike with a 16 x 16 PNG whose lower left corner sits on the pen.
            var typeface = BitmapGlyphRenderingTests.CreateSbixTypeface(out var glyph);
            using var run = new GlyphRun(typeface, 32, default, new List<GlyphInfo> { new(glyph, 0, 24) }, s_pen);
            using var recording = Record(run);

            Assert.Empty(decoder.DecodedWidths);
            Assert.True(recording.Bounds.Contains(new Rect(s_pen.X, s_pen.Y - 16, 16, 16)),
                $"recording bounds {recording.Bounds} miss the glyph");

            var pixels = Replay(recording, 1.0);

            AssertSolidAt(pixels, x: 8, y: 24, size: 16);
            Assert.Equal(new[] { 16 }, decoder.DecodedWidths);
        }

        [Fact]
        public void A_Glyph_The_Device_Size_Strike_Lacks_Draws_From_The_Next_Strike_That_Holds_It()
        {
            using var scope = CreateEnvironment(out var decoder);
            var typeface = CreateIncompleteSbixTypeface(out var glyph);
            using var run = CreateRun(typeface, glyph);
            using var recording = Record(run);

            // 20 px em at 1.5x is 30 device px: the 40 ppem strike has no image for the glyph, so
            // the 80 ppem strike draws it, scaled to 30 px.
            var pixels = Replay(recording, 1.5);

            AssertStrikeAt(pixels, IncompleteLargeStrike, 30, x: 12, y: 30);
            Assert.Equal(new[] { IncompleteLargeStrike }, decoder.DecodedWidths);
        }

        [Fact]
        public void A_Glyph_The_Device_Size_Strike_Holds_Still_Draws_From_That_Strike()
        {
            using var scope = CreateEnvironment(out var decoder);
            var typeface = CreateIncompleteSbixTypeface(out var glyph);
            using var run = CreateRun(typeface, glyph);
            using var recording = Record(run);

            var pixels = Replay(recording, 1.0);

            AssertStrikeAt(pixels, SmallStrike, SmallStrike, x: 8, y: 20);
            Assert.Equal(new[] { SmallStrike }, decoder.DecodedWidths);
        }

        [Fact]
        public void A_Glyph_Drawing_At_A_Size_Whose_Strike_Lacks_The_Glyph_Uses_Another_Strike()
        {
            using var scope = CreateEnvironment(out _);
            var typeface = CreateIncompleteSbixTypeface(out var glyph);

            var drawing = typeface.GetGlyphDrawing(glyph, new GlyphDrawingOptions { PixelSize = 30 });

            Assert.NotNull(drawing);
            Assert.Equal(GlyphDrawingType.Bitmap, drawing!.Type);
        }

        [Fact]
        public void A_Strike_Without_An_Image_For_The_Glyph_Leaves_Its_Ink_Box_Known()
        {
            using var scope = CreateEnvironment(out _);
            var typeface = CreateIncompleteSbixTypeface(out var glyph);

            Assert.NotNull(typeface.StrikeInkBoxes);
            Assert.True(typeface.StrikeInkBoxes!.TryGet(glyph, out var box), "the ink box was reported unknown");

            // Full-em images whose lower left corner sits on the pen.
            Assert.True(box.Contains(new Rect(0, -1, 1, 1)), $"ink box {box} misses the em square");
        }

        private static void AssertSolidAt(byte[] pixels, int x, int y, int size)
        {
            var ink = 0;
            var outside = 0;

            for (var py = 0; py < CanvasSize; py++)
            {
                for (var px = 0; px < CanvasSize; px++)
                {
                    var alpha = pixels[(py * CanvasSize + px) * 4 + 3];
                    var inside = px >= x && px < x + size && py >= y && py < y + size;

                    if (inside && alpha == 255)
                    {
                        ink++;
                    }
                    else if (!inside && alpha != 0)
                    {
                        outside++;
                    }
                }
            }

            Assert.Equal(size * size, ink);
            Assert.Equal(0, outside);
        }

        private static void AssertStrikeAt(byte[] pixels, int strike, int size, int x, int y)
        {
            var source = new DecodedGlyphBitmap(CreateStrikePixels(strike), strike, strike);
            var expected = new byte[size * size * 4];

            if (size == strike)
            {
                source.Bgra.CopyTo(expected, 0);
            }
            else
            {
                BitmapGlyphScaler.Scale(source, size, size, expected);
            }

            var mismatches = 0;
            var outside = 0;
            var first = "";

            for (var py = 0; py < CanvasSize; py++)
            {
                for (var px = 0; px < CanvasSize; px++)
                {
                    var i = (py * CanvasSize + px) * 4;
                    var inside = px >= x && px < x + size && py >= y && py < y + size;

                    if (!inside)
                    {
                        if (pixels[i + 3] != 0)
                        {
                            outside++;
                        }

                        continue;
                    }

                    var e = ((py - y) * size + (px - x)) * 4;

                    for (var c = 0; c < 4; c++)
                    {
                        if (pixels[i + c] != expected[e + c])
                        {
                            if (mismatches == 0)
                            {
                                first = $"({px}, {py}) channel {c}: {pixels[i + c]} expected {expected[e + c]}";
                            }

                            mismatches++;
                        }
                    }
                }
            }

            Assert.True(mismatches == 0, $"{mismatches} channel values differ from the scaled strike, first {first}");
            Assert.Equal(0, outside);
        }

        private const int CanvasSize = 80;

        private static DrawingRecording Record(GlyphRun run)
            => DrawingRecording.Create(context => Assert.True(ColorGlyphRunSplitter.TryDraw(context, run, Brushes.Black)));

        private static byte[] Replay(DrawingRecording recording, double scale)
            => Draw(scale, context => context.DrawRecording(recording));

        private static byte[] Draw(double scale, Action<DrawingContext> draw)
        {
            var info = new SKImageInfo(CanvasSize, CanvasSize, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var contextImpl = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));

            canvas.Clear(SKColors.Transparent);
            contextImpl.Transform = Matrix.CreateScale(scale, scale);

            using (var context = new PlatformDrawingContext(contextImpl, ownsImpl: false))
            {
                draw(context);
            }

            canvas.Flush();
            return bitmap.GetPixelSpan().ToArray();
        }

        private static GlyphRun CreateRun(GlyphTypeface typeface, ushort glyph)
            => new(typeface, EmSize, default, new List<GlyphInfo> { new(glyph, 0, 24) }, s_pen);

        // Opaque, so premultiplied and straight texels agree and the PNG round trip is exact: blue
        // ramps across, green ramps down, red tells the strikes apart.
        private static byte[] CreateStrikePixels(int size)
        {
            var pixels = new byte[size * size * 4];

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var i = (y * size + x) * 4;

                    pixels[i] = (byte)(x * 255 / (size - 1));
                    pixels[i + 1] = (byte)(y * 255 / (size - 1));
                    pixels[i + 2] = (byte)(size == LargeStrike ? 200 : 60);
                    pixels[i + 3] = 255;
                }
            }

            return pixels;
        }

        private static byte[] EncodePng(int size)
        {
            var pixels = CreateStrikePixels(size);
            using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));

            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);

            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }

        /// <summary>
        /// Inter with a CBLC/CBDT pair of two strikes, 20 and 40 ppem, each holding one full-em
        /// image (format 17, top on the em line) for the glyph of 'H'.
        /// </summary>
        internal static GlyphTypeface CreateTwoStrikeTypeface(out ushort glyph)
        {
            var font = SyntheticFont.FromBytes(LoadFontBytes("Inter-Regular.ttf"));
            var probe = font.TryCreateGlyphTypeface();
            Assert.NotNull(probe);

            glyph = probe!.CharacterToGlyphMap['H'];

            var strikes = new[] { SmallStrike, LargeStrike };
            var images = new List<byte[]>();
            var cbdt = new BigEndianBuffer();
            cbdt.UInt16(3).UInt16(0);

            var imageOffsets = new List<int>();
            var imageLengths = new List<int>();
            var offset = 4;

            foreach (var strike in strikes)
            {
                var png = EncodePng(strike);

                cbdt.UInt8((byte)strike).UInt8((byte)strike).Int8(0).Int8((sbyte)strike).UInt8((byte)strike);
                cbdt.UInt32((uint)png.Length);
                foreach (var b in png) cbdt.UInt8(b);

                imageOffsets.Add(offset);
                imageLengths.Add(5 + 4 + png.Length);
                offset += 5 + 4 + png.Length;
            }

            // Header, one 48-byte size record per strike, then each strike's 24-byte index array:
            // one subtable entry (8) plus a format 1 subtable (8 header + two offsets).
            const int header = 8;
            const int record = 48;
            const int arraySize = 24;
            var firstArray = header + record * strikes.Length;

            var cblc = new BigEndianBuffer();
            cblc.UInt16(3).UInt16(0).UInt32((uint)strikes.Length);

            for (var s = 0; s < strikes.Length; s++)
            {
                cblc.UInt32((uint)(firstArray + s * arraySize)).UInt32(arraySize).UInt32(1).UInt32(0);
                for (var i = 0; i < 24; i++) cblc.UInt8(0);
                cblc.UInt16(glyph).UInt16(glyph).UInt8((byte)strikes[s]).UInt8((byte)strikes[s]).UInt8(32).Int8(1);
            }

            for (var s = 0; s < strikes.Length; s++)
            {
                cblc.UInt16(glyph).UInt16(glyph).UInt32(8);
                cblc.UInt16(1).UInt16(17).UInt32((uint)imageOffsets[s]);
                cblc.UInt32(0).UInt32((uint)imageLengths[s]);
            }

            var grafted = font.Replace("CBLC", cblc.ToArray()).Replace("CBDT", cbdt.ToArray()).ToBytes();

            using var skData = SKData.CreateCopy(grafted);
            var skTypeface = SKTypeface.FromData(skData);
            Assert.NotNull(skTypeface);

            var typeface = TestGlyphTypefaces.FromSKTypeface(skTypeface!);
            Assert.NotNull(typeface.BitmapTable);
            Assert.Equal(2, typeface.BitmapTable!.StrikeCount);
            return typeface;
        }

        private const int IncompleteLargeStrike = 80;

        /// <summary>
        /// Inter with an sbix table of three strikes, 20, 40 and 80 ppem, where the glyph of 'H' has
        /// a full-em PNG (lower left corner on the pen) in the 20 and 80 ppem strikes and no record
        /// in the 40 ppem strike, as Apple Color Emoji leaves some glyphs out of some strikes.
        /// </summary>
        internal static GlyphTypeface CreateIncompleteSbixTypeface(out ushort glyph)
        {
            var font = SyntheticFont.FromBytes(LoadFontBytes("Inter-Regular.ttf"));
            var probe = font.TryCreateGlyphTypeface();
            Assert.NotNull(probe);

            glyph = probe!.CharacterToGlyphMap['H'];

            var glyphCount = probe.GlyphCount;
            var strikes = new[] { (Ppem: SmallStrike, Png: EncodePng(SmallStrike)), (Ppem: LargeStrike, Png: (byte[]?)null),
                (Ppem: IncompleteLargeStrike, Png: EncodePng(IncompleteLargeStrike)) };
            var strikeHeaderSize = 4 + (glyphCount + 1) * 4;

            var sbix = new BigEndianBuffer();
            sbix.UInt16(1).UInt16(0).UInt32((uint)strikes.Length);

            var strikeOffset = 8 + 4 * strikes.Length;

            foreach (var (_, png) in strikes)
            {
                sbix.UInt32((uint)strikeOffset);
                strikeOffset += strikeHeaderSize + (png is null ? 0 : 8 + png.Length);
            }

            foreach (var (ppem, png) in strikes)
            {
                sbix.UInt16((ushort)ppem).UInt16(72);

                // Offsets from the strike start; equal neighbours mean no record for that glyph, so
                // only the offsets after the glyph's own move past its record.
                for (var g = 0; g <= glyphCount; g++)
                {
                    var pastRecord = png is not null && g > glyph;
                    sbix.UInt32((uint)(pastRecord ? strikeHeaderSize + 8 + png!.Length : strikeHeaderSize));
                }

                if (png is not null)
                {
                    sbix.Int16(0).Int16(0);
                    foreach (var c in "png ") sbix.UInt8((byte)c);
                    foreach (var b in png) sbix.UInt8(b);
                }
            }

            var grafted = font.Replace("sbix", sbix.ToArray()).ToBytes();

            using var skData = SKData.CreateCopy(grafted);
            var skTypeface = SKTypeface.FromData(skData);
            Assert.NotNull(skTypeface);

            var typeface = TestGlyphTypefaces.FromSKTypeface(skTypeface!);
            Assert.NotNull(typeface.BitmapSource);
            Assert.Equal(3, typeface.BitmapSource!.StrikeCount);
            return typeface;
        }

        private static IDisposable CreateEnvironment(out RecordingDecoder decoder)
        {
            var scope = TextTestApplication.Enter();

            decoder = new RecordingDecoder(new SkiaBitmapGlyphDecoder());

            AvaloniaLocator.CurrentMutable
                .Bind<IPlatformRenderInterface>().ToConstant(new DecodingRenderInterface(decoder));
            AvaloniaLocator.CurrentMutable
                .Bind<GlyphRasterizationOptions>().ToConstant(new GlyphRasterizationOptions
                {
                    TextRasterizationMode = TextRasterizationMode.Backend,
                });

            return scope;
        }

        private sealed class RecordingDecoder : IBitmapGlyphDecoder
        {
            private readonly IBitmapGlyphDecoder _inner;

            public RecordingDecoder(IBitmapGlyphDecoder inner) => _inner = inner;

            /// <summary>The width of every image decoded, in decode order; strike images are square.</summary>
            public List<int> DecodedWidths { get; } = new();

            public bool TryDecode(ReadOnlyMemory<byte> encoded, out DecodedGlyphBitmap bitmap)
            {
                var decoded = _inner.TryDecode(encoded, out bitmap);

                if (decoded)
                {
                    DecodedWidths.Add(bitmap.Width);
                }

                return decoded;
            }
        }

        private static byte[] LoadFontBytes(string fileName)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && directory.Name != "tests")
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);

            return File.ReadAllBytes(Path.Combine(directory!.FullName, "Avalonia.RenderTests", "Assets", fileName));
        }
    }
}
