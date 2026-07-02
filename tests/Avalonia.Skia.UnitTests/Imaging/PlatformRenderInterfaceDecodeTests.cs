using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Skia.Imaging;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Imaging
{
    /// <summary>
    /// The legacy IPlatformRenderInterface decode methods now route through the active
    /// imaging backend; these tests pin parity with the direct Skia decode they replace.
    /// </summary>
    public class PlatformRenderInterfaceDecodeTests
    {
        private static IDisposable BindBackend()
        {
            var scope = AvaloniaLocator.EnterScope();

            ImagingBackend.Register(new SkiaImagingBackend());

            return scope;
        }

        private static Avalonia.Skia.PlatformRenderInterface CreateRenderInterface() => new(null);

        private static MemoryStream CreateQuadrantPng(int width, int height)
        {
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var color = (x < width / 2, y < height / 2) switch
                    {
                        (true, true) => new SKColor(255, 0, 0),
                        (false, true) => new SKColor(0, 255, 0),
                        (true, false) => new SKColor(0, 0, 255),
                        (false, false) => new SKColor(255, 255, 0),
                    };

                    bitmap.SetPixel(x, y, color);
                }
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);

            return new MemoryStream(data.ToArray());
        }

        private static byte[] ReadAllPixels(IBitmapImpl impl)
        {
            var readable = Assert.IsAssignableFrom<IReadableBitmapImpl>(impl);

            using var framebuffer = readable.Lock();

            var bytes = new byte[framebuffer.RowBytes * framebuffer.Size.Height];

            Marshal.Copy(framebuffer.Address, bytes, 0, bytes.Length);

            return bytes;
        }

        [Fact]
        public void LoadBitmap_Matches_The_Direct_Skia_Decode()
        {
            using var scope = BindBackend();

            var renderInterface = CreateRenderInterface();

            using var stream = CreateQuadrantPng(8, 8);

            using var viaBackend = renderInterface.LoadBitmap(stream);

            stream.Position = 0;

            using var direct = new Avalonia.Skia.ImmutableBitmap(stream);

            Assert.Equal(direct.PixelSize, viaBackend.PixelSize);
            Assert.Equal(direct.Dpi, viaBackend.Dpi);
            Assert.Equal(ReadAllPixels(direct), ReadAllPixels(viaBackend));
        }

        [Fact]
        public void LoadBitmapToWidth_Produces_The_Requested_Size()
        {
            using var scope = BindBackend();

            var renderInterface = CreateRenderInterface();

            using var stream = CreateQuadrantPng(8, 8);
            using var impl = renderInterface.LoadBitmapToWidth(stream, 4);

            Assert.Equal(new PixelSize(4, 4), impl.PixelSize);

            // Flat quadrants survive any downscale filter exactly.
            var pixels = ReadAllPixels(impl);
            var readable = (IReadableBitmapImpl)impl;

            using var framebuffer = readable.Lock();

            var rowBytes = framebuffer.RowBytes;

            Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels[..4]);                       // top-left = red (BGRA)
            Assert.Equal(new byte[] { 0, 255, 0, 255 }, pixels[(2 * 4)..(2 * 4 + 4)]);      // top-right = green
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixels[(2 * rowBytes)..(2 * rowBytes + 4)]); // bottom-left = blue
        }

        [Fact]
        public void LoadBitmapToHeight_Keeps_The_Aspect_Ratio()
        {
            using var scope = BindBackend();

            var renderInterface = CreateRenderInterface();

            using var stream = CreateQuadrantPng(8, 4);
            using var impl = renderInterface.LoadBitmapToHeight(stream, 2);

            Assert.Equal(new PixelSize(4, 2), impl.PixelSize);
        }

        [Fact]
        public void LoadBitmap_Unknown_Data_Keeps_The_Legacy_Exception_Contract()
        {
            using var scope = BindBackend();

            var renderInterface = CreateRenderInterface();

            using var stream = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

            Assert.Throws<ArgumentException>(() => renderInterface.LoadBitmap(stream));
        }

        [Fact]
        public void LoadBitmap_Works_Without_A_Bound_Backend()
        {
            using var scope = AvaloniaLocator.EnterScope();

            var renderInterface = CreateRenderInterface();

            using var stream = CreateQuadrantPng(4, 4);
            using var impl = renderInterface.LoadBitmap(stream);

            Assert.Equal(new PixelSize(4, 4), impl.PixelSize);
        }

        [Fact]
        public void LoadBitmap_From_File_Works()
        {
            using var scope = BindBackend();

            var renderInterface = CreateRenderInterface();

            var path = Path.GetTempFileName();

            try
            {
                using (var stream = CreateQuadrantPng(4, 4))
                using (var file = File.Create(path))
                {
                    stream.CopyTo(file);
                }

                using var impl = renderInterface.LoadBitmap(path);

                Assert.Equal(new PixelSize(4, 4), impl.PixelSize);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
