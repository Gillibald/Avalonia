using System;
using System.IO;
using System.Linq;
using Avalonia.Imaging.TestKit.Fixtures;
using Avalonia.Imaging.TestKit.Instrumentation;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Imaging.PixelMan.Tests
{
    public class PixelManBackendTests
    {
        [Fact]
        public void Pixel_Conversion_Should_Match_The_Core_Wic_Formulas()
        {
            var bytes = new byte[64 * 4];

            new Random(3).NextBytes(bytes);

            var source = PixelBuffer.Create(bytes, new PixelSize(64, 1), 64 * 4, PixelFormats.Bgra8888, AlphaFormat.Unpremul);
            var core = source.Convert(PixelFormats.Rgba8888, AlphaFormat.Premul).Pixels.ToArray();

            using var scope = Bind();

            Assert.Equal(core, source.Convert(PixelFormats.Rgba8888, AlphaFormat.Premul).Pixels.ToArray());
        }

        [Fact]
        public void An_Indexed_Target_Should_Map_To_The_Palette()
        {
            var palette = new BitmapPalette(new[] { Colors.Black, Colors.White, Colors.Red, Colors.Blue });
            var source = PixelBuffer.Create(new byte[] { 0, 0, 250, 255, 250, 250, 250, 255, 240, 0, 0, 255, 5, 5, 5, 255 },
                new PixelSize(4, 1), 16, PixelFormats.Bgra8888, AlphaFormat.Unpremul);

            using var scope = Bind();

            var indexed = source.Convert(PixelFormats.Indexed2, AlphaFormat.Opaque, new PixelConversionOptions { Palette = palette });

            Assert.Equal(0b10_01_11_00, indexed.Pixels.Span[0]);
        }

        [Fact]
        public void Optimal_Palette_Should_Hold_The_Colors_Of_A_Four_Color_Image()
        {
            var colors = new[] { Colors.Red, Colors.Lime, Colors.Blue, Colors.White };
            var bytes = new byte[16 * 4];

            for (var i = 0; i < 16; i++)
            {
                var color = colors[i % 4];

                bytes[i * 4] = color.B;
                bytes[i * 4 + 1] = color.G;
                bytes[i * 4 + 2] = color.R;
                bytes[i * 4 + 3] = 255;
            }

            var source = PixelBuffer.Create(bytes, new PixelSize(16, 1), 64, PixelFormats.Bgra8888, AlphaFormat.Unpremul);

            using var scope = Bind();

            var palette = BitmapPalette.CreateOptimal(source, 4);

            foreach (var color in colors)
                Assert.Contains(color, palette.Colors);
        }

        [Fact]
        public void Animated_Gif_Should_Round_Trip_Frames_Origins_And_Timing()
        {
            using var scope = Bind();

            var encoder = new GifBitmapEncoder { LoopCount = 2 };

            encoder.Frames.Add(new BitmapEncoderFrame
            {
                Pixels = Solid(4, 4, Colors.Red),
                Delay = TimeSpan.FromMilliseconds(100),
                Disposal = FrameDisposalMethod.DoNotDispose,
            });
            encoder.Frames.Add(new BitmapEncoderFrame
            {
                Pixels = Solid(2, 2, Colors.Lime),
                Origin = new PixelPoint(2, 1),
                Delay = TimeSpan.FromMilliseconds(250),
                Disposal = FrameDisposalMethod.RestoreToBackground,
            });

            using var stream = new MemoryStream();

            encoder.Save(stream, TestContext.Current.CancellationToken);
            stream.Position = 0;

            using var decoder = BitmapDecoder.Create(stream);

            Assert.True(decoder.IsAnimated(out var loops));
            Assert.Equal(2, loops);

            var frames = decoder.ReadFrames().ToList();

            try
            {
                Assert.Equal(2, frames.Count);
                Assert.True(frames[1].TryGetAnimation(out var animation));
                Assert.Equal(new PixelSize(2, 2), frames[1].PixelSize);
                Assert.Equal(new PixelPoint(2, 1), animation.Origin);
                Assert.Equal(TimeSpan.FromMilliseconds(250), animation.Delay);
                Assert.Equal(FrameDisposalMethod.RestoreToBackground, animation.Disposal);
                Assert.True(frames[0].TryGetPalette(out var palette));
                Assert.Contains(Colors.Red, palette!.Colors);
            }
            finally
            {
                foreach (var frame in frames)
                    frame.Dispose();
            }
        }

        [Fact]
        public void Jpeg_Metadata_Should_Round_Trip_Through_PixelMan_Paths()
        {
            using var scope = Bind();

            var metadata = new BitmapMetadata(BitmapContainerFormats.Jpeg);

            metadata.SetQuery("/app1/ifd/{ushort=271}", "Maker");
            metadata.SetQuery("/app1/ifd/exif/{ushort=36867}", "2026:10:24 12:00:00");
            metadata.SetQuery("/app13/irb/8bimiptc/iptc/Keywords", new[] { "sea", "boat" });
            metadata.XmpPacket = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"></x:xmpmeta>";

            var decoded = RoundTrip(new JpegBitmapEncoder { Metadata = metadata }, BitmapContainerFormats.Jpeg);

            Assert.Equal("Maker", decoded.GetQuery("/app1/ifd/{ushort=271}"));
            Assert.Equal("2026:10:24 12:00:00", decoded.GetQuery("/app1/ifd/exif/{ushort=36867}"));
            Assert.Equal(new[] { "sea", "boat" }, decoded.GetQuery("/app13/irb/8bimiptc/iptc/Keywords"));
            Assert.Equal(metadata.XmpPacket, decoded.XmpPacket);
        }

        [Fact]
        public void Png_Text_Should_Round_Trip_Through_PixelMan_Paths()
        {
            using var scope = Bind();

            var metadata = new BitmapMetadata(BitmapContainerFormats.Png);

            metadata.SetQuery("/tEXt/{str=Title}", "Hello");
            metadata.SetQuery("/iTXt/{str=Comment}/{str=de}/{str=Kommentar}", "Grüße 日本");

            var decoded = RoundTrip(new PngBitmapEncoder { Metadata = metadata }, BitmapContainerFormats.Png);

            Assert.Equal("Hello", decoded.GetQuery("/tEXt/Title"));
            Assert.Equal("Grüße 日本", decoded.GetQuery("/iTXt/{str=Comment}/{str=de}/{str=Kommentar}"));
            Assert.Equal("de", decoded.GetQuery("/iTXt/LanguageTag"));
        }

        [Fact]
        public void Tiff_Pages_Should_Keep_Their_Own_Color_Profiles()
        {
            using var scope = Bind();

            var first = IccProfileFixture.CreateRgb(1.0);
            var second = IccProfileFixture.CreateRgb(2.0);
            var encoder = new TiffBitmapEncoder();

            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = Solid(2, 2, Colors.Red), ColorProfile = first });
            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = Solid(2, 2, Colors.Blue), ColorProfile = second });

            using var stream = new MemoryStream();

            encoder.Save(stream, TestContext.Current.CancellationToken);
            stream.Position = 0;

            using var decoder = BitmapDecoder.Create(stream);
            using var page0 = decoder.GetFrame(0);
            using var page1 = decoder.GetFrame(1);

            Assert.True(page0.TryGetColorProfile(out var profile0));
            Assert.True(page1.TryGetColorProfile(out var profile1));
            Assert.Equal(first, profile0.ToArray());
            Assert.Equal(second, profile1.ToArray());
        }

#if PIXELMAN_FRAME_ALLOCATOR
        [Fact]
        public void A_Frame_Decoded_Into_The_Pool_Should_Be_Installed_Without_A_Copy()
        {
            var allocator = new CountingAllocator();
            using var stream = new MemoryStream();
            using var scope = AvaloniaLocator.EnterScope();

            ImagingBackend.Register(new PixelManImagingBackend(allocator));

            var encoder = new PngBitmapEncoder();

            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = Solid(5, 3, Colors.Red) });
            encoder.Save(stream, TestContext.Current.CancellationToken);
            stream.Position = 0;

            var rentsBeforeDecode = allocator.RentCount;

            using var decoder = BitmapDecoder.Create(stream);
            using var frame = decoder.ReadNextFrame()!;

            var decodedInto = Assert.Single(allocator.LiveAddresses);

            using (var pixels = frame.Lock())
            {
                Assert.Equal(decodedInto, pixels.Address);
                Assert.Equal(rentsBeforeDecode + 1, allocator.RentCount);
                Assert.Equal(Colors.Red.R, System.Runtime.InteropServices.Marshal.ReadByte(pixels.Address));
            }

            frame.Dispose();

            Assert.Equal(0, allocator.OutstandingRentals);
        }
#endif

        private static BitmapMetadata RoundTrip(BitmapEncoder encoder, Guid containerFormat)
        {
            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = Solid(4, 4, Colors.Gray) });

            using var stream = new MemoryStream();

            encoder.Save(stream, TestContext.Current.CancellationToken);
            stream.Position = 0;

            using var decoder = BitmapDecoder.Create(stream);
            using var frame = decoder.ReadNextFrame()!;

            Assert.True(frame.TryGetMetadata(out var metadata));
            Assert.Equal(containerFormat, metadata!.ContainerFormat);

            return metadata;
        }

        private static PixelBuffer Solid(int width, int height, Color color)
        {
            var bytes = new byte[width * height * 4];

            for (var i = 0; i < width * height; i++)
            {
                bytes[i * 4] = color.B;
                bytes[i * 4 + 1] = color.G;
                bytes[i * 4 + 2] = color.R;
                bytes[i * 4 + 3] = color.A;
            }

            return PixelBuffer.Create(bytes, new PixelSize(width, height), width * 4, PixelFormats.Bgra8888, AlphaFormat.Unpremul);
        }

        private static IDisposable Bind()
        {
            var scope = AvaloniaLocator.EnterScope();

            ImagingBackend.Register(new PixelManImagingBackend());

            return scope;
        }
    }
}
