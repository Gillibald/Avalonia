using System;
using System.IO;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Skia.Helpers;
using Avalonia.UnitTests;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// CBDT strike bitmaps through the managed mask path: decoded via the registered Skia
    /// decoder and composed server-side into the run mask, with outline fallback preserved
    /// for glyphs the strike does not cover.
    /// </summary>
    public class BitmapGlyphRenderingTests
    {
        [Fact]
        public void Strike_Bitmaps_Compose_Into_The_Run_Mask()
        {
            using var scope = CreateEnvironment();
            var typeface = CreateBitmapTypeface(out var bitmapGlyph, out var plainGlyph);

            var run = CreateRun(typeface, new[] { plainGlyph, bitmapGlyph }, emSize: 32);

            var info = new SKImageInfo(120, 56, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var context = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));

            canvas.Clear(SKColors.White);

            Assert.True(MaskGlyphRunRenderer.TryDraw(context, (ManagedGlyphRunImpl)run, Brushes.Black, aliased: false),
                "the mask path rejected a bitmap-strike draw");

            var pixels = bitmap.GetPixelSpan();
            var green = 0;
            var black = 0;

            for (var i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 1] > 150 && pixels[i + 2] < 100 && pixels[i] < 100)
                {
                    green++;
                }
                else if (pixels[i] < 60 && pixels[i + 1] < 60 && pixels[i + 2] < 60 && pixels[i + 3] == 255)
                {
                    black++;
                }
            }

            // The strike image is solid green (decoded and blitted at the pen), the neighboring
            // outline glyph renders black through the ordinary mask path in the same run.
            Assert.True(green > 20, $"expected green strike pixels, found {green}");
            Assert.True(black > 8, $"expected black outline pixels, found {black}");
        }

        private static GlyphTypeface CreateBitmapTypeface(out ushort bitmapGlyph, out ushort plainGlyph)
        {
            var font = SyntheticFont.FromBytes(LoadFontBytes("Inter-Regular.ttf"));
            var probe = font.TryCreateGlyphTypeface();
            Assert.NotNull(probe);

            bitmapGlyph = probe!.CharacterToGlyphMap['H'];
            plainGlyph = probe.CharacterToGlyphMap['A'];

            // One 32px strike covering just the bitmap glyph, image format 17, real PNG bytes
            // (16x16 solid green) so the Skia decoder path runs for real.
            var png = EncodeSolidPng(16, 16, SKColors.Lime);

            var cbdt = new BigEndianBuffer();
            cbdt.UInt16(3).UInt16(0);
            cbdt.UInt8(16).UInt8(16).Int8(0).Int8(16).UInt8(18);
            cbdt.UInt32((uint)png.Length);
            foreach (var b in png) cbdt.UInt8(b);
            var imageLength = 5 + 4 + png.Length;

            const int header = 8;
            const int records = 48;
            var arrayOffset = header + records;

            var cblc = new BigEndianBuffer();
            cblc.UInt16(3).UInt16(0).UInt32(1);
            cblc.UInt32((uint)arrayOffset).UInt32(24).UInt32(1).UInt32(0);
            for (var i = 0; i < 24; i++) cblc.UInt8(0);
            cblc.UInt16(bitmapGlyph).UInt16(bitmapGlyph).UInt8(32).UInt8(32).UInt8(32).Int8(1);
            cblc.UInt16(bitmapGlyph).UInt16(bitmapGlyph).UInt32(8);
            cblc.UInt16(1).UInt16(17).UInt32(4);
            cblc.UInt32(0).UInt32((uint)imageLength);

            var grafted = font.Replace("CBLC", cblc.ToArray()).Replace("CBDT", cbdt.ToArray()).ToBytes();

            using var skData = SKData.CreateCopy(grafted);
            var skTypeface = SKTypeface.FromData(skData);
            Assert.NotNull(skTypeface);

            var typeface = new GlyphTypeface(new SkiaTypeface(skTypeface!, FontSimulations.None));
            Assert.NotNull(typeface.BitmapTable);
            return typeface;
        }

        private static byte[] EncodeSolidPng(int width, int height, SKColor color)
        {
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            bitmap.Erase(color);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }

        private static IGlyphRunImpl CreateRun(GlyphTypeface typeface, ushort[] glyphs, double emSize)
        {
            var scale = emSize / typeface.Metrics.DesignEmHeight;
            var infos = new List<GlyphInfo>();
            var cluster = 0;

            foreach (var glyph in glyphs)
            {
                typeface.TryGetGlyphMetrics(glyph, out var metrics);
                infos.Add(new GlyphInfo(glyph, cluster++, Math.Max(metrics.AdvanceWidth * scale, 20)));
            }

            return new ManagedGlyphRunImpl(typeface, emSize, infos, new Point(8, 40));
        }

        private static IDisposable CreateEnvironment()
        {
            var scope = AvaloniaLocator.EnterScope();

            AvaloniaLocator.CurrentMutable
                .Bind<IPlatformRenderInterface>().ToConstant(new PlatformRenderInterface());
            AvaloniaLocator.CurrentMutable
                .Bind<IBitmapGlyphDecoder>().ToConstant(new SkiaBitmapGlyphDecoder());

            return scope;
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
