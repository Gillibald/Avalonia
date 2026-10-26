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
        public void An_Indexed_Source_Should_Remap_To_A_New_Palette_Of_The_Same_Format()
        {
            var palette = new BitmapPalette(new[] { Colors.Black, Colors.White, Colors.Red, Colors.Blue });
            var reversed = new BitmapPalette(new[] { Colors.Blue, Colors.Red, Colors.White, Colors.Black });
            var source = PixelBuffer.Create(new byte[] { 0, 1, 2, 3 }, new PixelSize(4, 1), 4, PixelFormats.Indexed8,
                AlphaFormat.Opaque, palette: palette);

            using var scope = Bind();

            var remapped = source.Convert(PixelFormats.Indexed8, AlphaFormat.Opaque,
                new PixelConversionOptions { Palette = reversed });

            Assert.Same(reversed, remapped.Palette);
            Assert.Equal(new byte[] { 3, 2, 1, 0 }, remapped.Pixels.ToArray());
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
        public void Truncated_Pixel_Data_Should_Decode_Only_When_Recovery_Is_Allowed()
        {
            using var scope = Bind();

            var bytes = FixtureImages.TruncatedPng.EncodedBytes.ToArray();

            Assert.Throws<InvalidDataException>(() =>
            {
                using var decoder = BitmapDecoder.Create(new MemoryStream(bytes));
                using var frame = decoder.ReadNextFrame();
            });

            using (var decoder = BitmapDecoder.Create(new MemoryStream(bytes),
                       new BitmapDecodeOptions { AllowRecoveredPixelData = true }))
            using (var frame = decoder.ReadNextFrame()!)
            {
                Assert.Equal(FixtureImages.TruncatedPng.ExpectedSize, frame.PixelSize);
            }
        }

        [Fact]
        public void A_Gif_Decoder_Should_Read_Frames_In_Any_Order()
        {
            using var scope = Bind();

            var colors = new[] { Colors.Red, Colors.Lime, Colors.Blue };
            var encoder = new GifBitmapEncoder();

            foreach (var color in colors)
                encoder.Frames.Add(new BitmapEncoderFrame { Pixels = Solid(2, 2, color) });

            using var stream = new MemoryStream();

            encoder.Save(stream, TestContext.Current.CancellationToken);
            stream.Position = 0;

            using var decoder = BitmapDecoder.Create(stream);

            Assert.True(decoder.CanSeekFrames);
            Assert.Equal(3, decoder.FrameCount);

            foreach (var index in new[] { 2, 0, 1 })
            {
                using var frame = decoder.GetFrame(index);
                var pixel = frame.ToPixelBuffer().Convert(PixelFormats.Bgra8888, AlphaFormat.Unpremul).Pixels.Span;

                Assert.Equal(colors[index], Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]));
            }
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
        public void Photoshop_Resources_Should_Round_Trip_Through_Jpeg()
        {
            using var scope = Bind();

            // An unparsed resource as WIC exposes it: signature, id, empty name, size 3, "abc".
            var stored = new byte[] { 0x38, 0x42, 0x49, 0x4D, 0x0B, 0xB7, 0, 0, 0, 0, 0, 3, 0x61, 0x62, 0x63 };
            var metadata = new BitmapMetadata(BitmapContainerFormats.Jpeg);

            metadata.SetQuery("/app13/irb/8bimResInfo/PString", "Res");
            metadata.SetQuery("/app13/irb/8bimResInfo/HResolution", 300u << 16);
            metadata.SetQuery("/app13/{ushort=0}/{ulonglong=61857348783031}/{}", new BitmapMetadataBlob(stored));
            metadata.SetQuery("/app13/irb/8bimiptc/iptc/Caption", "Hello");

            var decoded = RoundTrip(new JpegBitmapEncoder { Metadata = metadata }, BitmapContainerFormats.Jpeg);

            Assert.Equal("Res", decoded.GetQuery("/app13/{ushort=0}/{ulonglong=61857348781037}/PString"));
            Assert.Equal(300u << 16, decoded.GetQuery("/app13/irb/8bimResInfo/HResolution"));
            // Fields left unset take WIC's defaults.
            Assert.Equal((ushort)1, decoded.GetQuery("/app13/irb/8bimResInfo/HeightUnit"));
            Assert.Equal(stored, Assert.IsType<BitmapMetadataBlob>(
                decoded.GetQuery("/app13/{ushort=0}/{ulonglong=61857348783031}/{}")).GetBlobValue());
            Assert.Equal("Hello", decoded.GetQuery("/app13/{ushort=0}/{ulonglong=61857348781060}/iptc/{str=Caption}"));
            Assert.Equal(string.Empty, decoded.GetQuery("/app13/irb/8bimiptc/PString"));
            Assert.Equal(new[] { "/{ulonglong=61857348781037}", "/{ulonglong=61857348781060}", "/{ulonglong=61857348783031}" },
                decoded.GetChildren("/app13/{ushort=0}", MetadataPathSpelling.Container).OrderBy(p => p, StringComparer.Ordinal));
        }

        [Fact]
        public void Tiff_Resource_Iptc_Should_Round_Trip_Apart_From_Tag_33723()
        {
            using var scope = Bind();

            var metadata = new BitmapMetadata(BitmapContainerFormats.Tiff);

            metadata.SetQuery("/ifd/iptc/Caption", "Tag");
            metadata.SetQuery("/ifd/irb/8bimiptc/iptc/Caption", "Resource");

            var decoded = RoundTrip(new TiffBitmapEncoder { Metadata = metadata }, BitmapContainerFormats.Tiff);

            Assert.Equal("Tag", decoded.GetQuery("/ifd/{ushort=33723}/{str=Caption}"));
            Assert.Equal("Resource", decoded.GetQuery("/ifd/{ushort=34377}/{ulonglong=61857348781060}/iptc/{str=Caption}"));
            Assert.Equal(string.Empty, decoded.GetQuery("/ifd/irb/8bimiptc/PString"));
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

        [Theory]
        [InlineData(BitmapFormatProfile.RenderBackend, (ushort)1)]
        [InlineData(BitmapFormatProfile.Wic, (ushort)2)]
        public void The_Wic_Profile_Should_Encode_With_Wic_Choices(BitmapFormatProfile profile, ushort? predictor)
        {
            using var scope = Bind();

            // WIC writes LZW TIFFs with horizontal differencing for 8-bit samples.
            var encoder = new TiffBitmapEncoder { FormatProfile = profile, Compression = TiffCompression.Lzw };

            encoder.Frames.Add(new BitmapEncoderFrame
            {
                Pixels = Solid(4, 4, Colors.Red).Convert(PixelFormats.Rgb24, AlphaFormat.Opaque)
            });

            using var stream = new MemoryStream();

            encoder.Save(stream, TestContext.Current.CancellationToken);

            Assert.Equal(predictor, ReadTiffTag(stream.ToArray(), 317));
        }

        // Reads a SHORT tag of a little-endian TIFF's first IFD.
        private static ushort? ReadTiffTag(byte[] tiff, ushort tag)
        {
            Assert.Equal((byte)'I', tiff[0]);

            var ifd = BitConverter.ToInt32(tiff, 4);
            var count = BitConverter.ToUInt16(tiff, ifd);

            for (var i = 0; i < count; i++)
            {
                var entry = ifd + 2 + i * 12;

                if (BitConverter.ToUInt16(tiff, entry) == tag)
                    return BitConverter.ToUInt16(tiff, entry + 8);
            }

            return null;
        }

        [Fact]
        public void A_Transform_Without_An_Intent_Should_Leave_The_Choice_To_The_Color_Management()
        {
            using var scope = Bind();

            // The header names absolute colorimetric, which PixelMan does not offer as an explicit
            // intent; without one it picks from what the profiles hold, as WIC does.
            var source = IccProfileFixture.CreateRgb(2.2);
            source[67] = 3;

            using var transform = BitmapColorTransform.Create(source, IccProfileFixture.CreateRgb(1.0));
            var result = new float[3];

            transform.TransformColors(new[] { 1f, 1f, 1f }, result);

            Assert.Null(transform.Intent);
            Assert.Equal(new[] { 1f, 1f, 1f }, result.Select(v => MathF.Round(v, 3)).ToArray());
        }

        [Fact]
        public void A_Cursor_Should_Decode_Unless_Decoding_As_Wic()
        {
            using var scope = Bind();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = Solid(2, 2, Colors.Red) });

            using var png = new MemoryStream();
            encoder.Save(png, TestContext.Current.CancellationToken);

            // An icon file of type 2 with one PNG entry; the entry holds the hotspot (1, 1).
            var image = png.ToArray();
            var cursor = new byte[6 + 16 + image.Length];
            cursor[2] = 2;
            cursor[4] = 1;
            cursor[6] = 2;
            cursor[7] = 2;
            cursor[10] = 1;
            cursor[12] = 1;
            BitConverter.GetBytes(image.Length).CopyTo(cursor, 14);
            BitConverter.GetBytes(22).CopyTo(cursor, 18);
            image.CopyTo(cursor, 22);

            using (var decoder = BitmapDecoder.Create(new MemoryStream(cursor)))
            using (var frame = decoder.ReadNextFrame()!)
            {
                var pixel = frame.ToPixelBuffer().Convert(PixelFormats.Bgra8888, AlphaFormat.Unpremul).Pixels.Span;

                Assert.Equal(new PixelSize(2, 2), frame.PixelSize);
                Assert.Equal(Colors.Red, Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]));
            }

            // WIC's icon decoder takes icons only.
            Assert.ThrowsAny<Exception>(() =>
            {
                using var decoder = BitmapDecoder.Create(new MemoryStream(cursor),
                    new BitmapDecodeOptions { FormatProfile = BitmapFormatProfile.Wic });
                using var frame = decoder.ReadNextFrame();
            });
        }

        [Fact]
        public void A_64_Bpp_Bmp_Should_Keep_Its_Linear_Fixed_Point_Samples()
        {
            using var scope = Bind();

            // Blue, green, red, alpha in s2.13 linear light: a GDI+ Format64bppArgb pixel.
            var samples = new short[] { 1000, 2000, 8192, 4096 };
            var bytes = new byte[8];
            Buffer.BlockCopy(samples, 0, bytes, 0, 8);

            var encoder = new BmpBitmapEncoder();
            encoder.Frames.Add(new BitmapEncoderFrame
            {
                Pixels = PixelBuffer.Create(bytes, new PixelSize(1, 1), 8, PixelFormats.Bgra64Linear, AlphaFormat.Unpremul)
            });

            using var stream = new MemoryStream();

            encoder.Save(stream, TestContext.Current.CancellationToken);
            stream.Position = 0;

            using var decoder = BitmapDecoder.Create(stream, new BitmapDecodeOptions { PreservePixelFormat = true });
            using var frame = decoder.ReadNextFrame()!;

            Assert.Equal(PixelFormats.Bgra64Linear, frame.Format);

            var decoded = new short[4];
            Buffer.BlockCopy(frame.ToPixelBuffer().Pixels.ToArray(), 0, decoded, 0, 8);

            Assert.Equal(samples, decoded);
        }

        [Fact]
        public void A_Gif_Should_Take_Container_Metadata_And_Leave_Out_The_Loop_Block_When_Asked()
        {
            using var scope = Bind();

            var metadata = new BitmapMetadata(BitmapContainerFormats.Gif);

            metadata.SetQuery("/commentext/TextEntry", "made by a test");

            var encoder = new GifBitmapEncoder { LoopCount = null, Metadata = metadata };

            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = Solid(2, 2, Colors.Red) });
            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = Solid(2, 2, Colors.Blue) });

            using var stream = new MemoryStream();

            encoder.Save(stream, TestContext.Current.CancellationToken);
            stream.Position = 0;

            using var decoder = BitmapDecoder.Create(stream);

            Assert.True(decoder.TryGetMetadata(out var container));
            Assert.Equal("made by a test", container.GetQuery("/commentext/TextEntry"));
            Assert.False(container.ContainsQuery("/appext/Application"));
        }

        [Theory]
        [InlineData("bmp")]
        [InlineData("tiff")]
        public void Bmp_And_Tiff_Should_Encode_A_Frame_Palette(string format)
        {
            using var scope = Bind();

            var palette = new BitmapPalette(new[] { Colors.Red, Colors.Blue });
            BitmapEncoder encoder = format == "bmp" ? new BmpBitmapEncoder() : new TiffBitmapEncoder();

            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = Solid(2, 2, Colors.Blue), Palette = palette });

            using var stream = new MemoryStream();

            encoder.Save(stream, TestContext.Current.CancellationToken);
            stream.Position = 0;

            using var decoder = BitmapDecoder.Create(stream, new BitmapDecodeOptions { PreservePixelFormat = true });
            using var frame = decoder.ReadNextFrame()!;

            Assert.True(frame.Format.IsIndexed);
            Assert.True(frame.TryGetPalette(out var written));
            Assert.Equal(Colors.Blue, written.Colors[frame.ToPixelBuffer().Pixels.Span[0] >> (8 - frame.Format.BitsPerPixel)]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_Profile_In_Frame_Metadata_Should_Be_Written(bool asBlob)
        {
            using var scope = Bind();

            var profile = IccProfileFixture.CreateRgb(1.0);
            var metadata = new BitmapMetadata(BitmapContainerFormats.Png);

            metadata.SetQuery("/iCCP/ProfileData", asBlob ? new BitmapMetadataBlob(profile) : profile);

            var encoder = new PngBitmapEncoder();

            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = Solid(2, 2, Colors.Red), Metadata = metadata });

            using var stream = new MemoryStream();

            encoder.Save(stream, TestContext.Current.CancellationToken);
            stream.Position = 0;

            using var decoder = BitmapDecoder.Create(stream);
            using var frame = decoder.ReadNextFrame()!;

            Assert.True(frame.TryGetColorProfile(out var written));
            Assert.Equal(profile, written.ToArray());
        }

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

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_Frame_Converted_For_The_Renderer_Should_Decode_Straight_Into_The_Pool(bool jpeg)
        {
            var allocator = new CountingAllocator();
            using var stream = new MemoryStream();
            using var scope = AvaloniaLocator.EnterScope();

            ImagingBackend.Register(new PixelManImagingBackend(allocator));

            BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { Quality = 100 } : new PngBitmapEncoder();

            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = Solid(64, 48, Color.FromArgb(128, 255, 0, 0)) });
            encoder.Save(stream, TestContext.Current.CancellationToken);
            stream.Position = 0;

            var rentsBeforeDecode = allocator.RentCount;

            using var decoder = BitmapDecoder.Create(stream, new BitmapDecodeOptions
            {
                TargetFormat = PixelFormats.Bgra8888,
                TargetAlphaFormat = AlphaFormat.Premul,
            });
            using var frame = decoder.ReadNextFrame()!;
            using var pixels = frame.Lock();

            // PixelMan writes the premultiplied BGRA itself, so the pool holds only the frame.
            Assert.Equal(rentsBeforeDecode + 1, allocator.RentCount);
            Assert.Equal(Assert.Single(allocator.LiveAddresses), pixels.Address);
            Assert.Equal(PixelFormats.Bgra8888, pixels.Format);
            Assert.Equal(AlphaFormat.Premul, pixels.AlphaFormat);

            if (!jpeg)
            {
                var first = new byte[4];

                System.Runtime.InteropServices.Marshal.Copy(pixels.Address, first, 0, 4);
                Assert.Equal(new byte[] { 0, 0, 128, 128 }, first);
            }
        }

        [Fact]
        public void A_Frame_The_Pool_Cannot_Hold_Should_Fail_To_Lock()
        {
            using var stream = new MemoryStream();

            using (var scope = Bind())
            {
                var encoder = new PngBitmapEncoder();

                encoder.Frames.Add(new BitmapEncoderFrame { Pixels = Solid(4, 4, Colors.Red) });
                encoder.Save(stream, TestContext.Current.CancellationToken);
            }

            stream.Position = 0;

            // PixelMan decodes into its own memory when renting fails; that frame is never
            // installed outside the pool.
            using var decoder = new PixelManImagingBackend(new ExhaustedPool()).CreateDecoder(stream, ownsStream: false);
            using var frame = decoder.ReadNextFrame()!;

            Assert.Throws<OutOfMemoryException>(() => frame.Lock());
        }

        private sealed class ExhaustedPool : IBitmapMemoryAllocator
        {
            public IBitmapMemory Rent(long byteCount) => throw new OutOfMemoryException();
        }

        [Theory]
        [InlineData(1, 8, 8, 6, 8)]
        [InlineData(6, 6, 6, 8, 6)]
        public void A_Jpeg_Should_Decode_Reduced_Towards_The_Oriented_Target(int orientation, int targetWidth,
            int expectedWidth, int expectedHeight, int nearestWidth)
        {
            using var stream = new MemoryStream();

            using (var source = Avalonia.PixelMan.PmBitmapFrame.CreateFromCopy(Avalonia.PixelMan.PmPixelFormat.Bgr24,
                       64, 48, 64 * 3, Enumerable.Repeat((byte)0x80, 64 * 48 * 3).ToArray()))
            using (var metadata = Avalonia.PixelMan.PmMetadata.Create())
            {
                metadata.Orientation = (ushort)orientation;
                source.SetMetadata(metadata);
                source.Encode(stream, Avalonia.PixelMan.PmCodecKind.Jpeg, cancellationToken: TestContext.Current.CancellationToken);
            }

            stream.Position = 0;

            var backend = new PixelManImagingBackend(new CountingAllocator());

            using var decoder = backend.CreateDecoder(stream, ownsStream: false,
                new BitmapDecodeOptions { TargetSize = new PixelSize(targetWidth, 0) });
            using var frame = decoder.ReadNextFrame()!;

            // The output matches a full-size decode to the same target; PixelMan decoded at 1/8.
            Assert.Equal(new PixelSize(expectedWidth, expectedHeight), frame.PixelSize);

            var fused = Assert.IsAssignableFrom<ISupportsFusedDecode>(frame);

            Assert.Equal(FusedDecodeParts.Scale, fused.FusedParts);
            Assert.Equal(nearestWidth, fused.GetNearestDecodeSize(frame.PixelSize).Width);

            using var pixels = frame.Lock();

            Assert.Equal(new PixelSize(expectedWidth, expectedHeight), pixels.Size);
        }

        [Fact]
        public void A_Source_Region_Should_Decode_At_Full_Size()
        {
            using var stream = new MemoryStream();

            using (var source = Avalonia.PixelMan.PmBitmapFrame.CreateFromCopy(Avalonia.PixelMan.PmPixelFormat.Bgr24,
                       64, 48, 64 * 3, new byte[64 * 48 * 3]))
                source.Encode(stream, Avalonia.PixelMan.PmCodecKind.Jpeg, cancellationToken: TestContext.Current.CancellationToken);

            stream.Position = 0;

            using var decoder = new PixelManImagingBackend(new CountingAllocator()).CreateDecoder(stream, ownsStream: false,
                new BitmapDecodeOptions { TargetSize = new PixelSize(8, 6), SourceRegion = new PixelRect(0, 0, 32, 24) });
            using var frame = decoder.ReadNextFrame()!;

            Assert.Equal(new PixelSize(8, 6), frame.PixelSize);
            Assert.Equal(FusedDecodeParts.None, Assert.IsAssignableFrom<ISupportsFusedDecode>(frame).FusedParts);
        }

        [Fact]
        public void A_Gif_Decoder_Should_Expose_The_Global_Color_Table()
        {
            using var scope = Bind();
            using var stream = new MemoryStream();
            var palette = new BitmapPalette(new[] { Colors.Black, Colors.White, Colors.Red, Colors.Blue });
            var encoder = new GifBitmapEncoder { GlobalPalette = palette };

            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = Solid(2, 2, Colors.Red) });
            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = Solid(2, 2, Colors.Blue) });
            encoder.Save(stream, TestContext.Current.CancellationToken);
            stream.Position = 0;

            using var decoder = BitmapDecoder.Create(stream);

            Assert.True(decoder.TryGetGlobalPalette(out var global));
            Assert.Equal(palette.Colors.Take(4), global!.Colors.Take(4));

            using var png = new MemoryStream();
            var pngEncoder = new PngBitmapEncoder();

            pngEncoder.Frames.Add(new BitmapEncoderFrame { Pixels = Solid(2, 2, Colors.Red) });
            pngEncoder.Save(png, TestContext.Current.CancellationToken);
            png.Position = 0;

            using var pngDecoder = BitmapDecoder.Create(png);

            Assert.False(pngDecoder.TryGetGlobalPalette(out _));
        }

        [Fact]
        public void An_Icon_Should_Decode_Every_Entry_In_Wic_Order()
        {
            using var scope = Bind();
            using var decoder = BitmapDecoder.Create(File.OpenRead(Asset("i_order.ico")));

            Assert.Equal(BitmapContainerFormats.Ico, decoder.CodecInfo.ContainerFormat);
            Assert.Equal(5, decoder.FrameCount);

            using var first = decoder.ReadNextFrame()!;

            // The directory lists a 32x32 entry first; WIC orders by depth, then size.
            Assert.Equal(new PixelSize(48, 48), first.PixelSize);
            Assert.Equal(PixelFormats.Bgra8888, first.Format);
            Assert.Throws<NotSupportedException>(() => ImagingBackend.Current.CreateEncoder(BitmapContainerFormats.Ico));
        }

        [Fact]
        public void A_Webp_Should_Decode_Straight_Rgba_And_Composited_Animation_Frames()
        {
            using var scope = Bind();

            using (var still = BitmapDecoder.Create(File.OpenRead(Asset("w_lossy_alpha.webp"))))
            using (var frame = still.ReadNextFrame()!)
            {
                Assert.Equal(new PixelSize(37, 23), frame.PixelSize);
                Assert.Equal(PixelFormats.Rgba8888, frame.Format);
                Assert.Equal(AlphaFormat.Unpremul, frame.AlphaFormat);
                // WebP stores no resolution, so Avalonia's default applies, not WIC's 72.
                Assert.Equal(new Vector(96, 96), frame.Dpi);
            }

            using (var still = BitmapDecoder.Create(File.OpenRead(Asset("w_lossy_alpha.webp"))))
                Assert.False(still.IsAnimated(out _));

            using var animation = BitmapDecoder.Create(File.OpenRead(Asset("w_anim.webp")));

            Assert.Equal(3, animation.FrameCount);
            Assert.True(animation.IsAnimated(out var loops));
            Assert.Equal(3, loops);

            var delays = new[] { 100, 250, 40 };

            for (var i = 0; i < 3; i++)
            {
                using var frame = animation.ReadNextFrame()!;

                Assert.Equal(new PixelSize(20, 14), frame.PixelSize);
                Assert.True(frame.TryGetAnimation(out var timing));
                // Composited frames replace the canvas whole.
                Assert.Equal(new BitmapFrameAnimation(TimeSpan.FromMilliseconds(delays[i]),
                    FrameDisposalMethod.DoNotDispose, default, FrameBlendMode.Source), timing);
            }
        }

        [Fact]
        public void A_Jpeg_Should_Expose_Its_Exif_Thumbnail()
        {
            var allocator = new CountingAllocator();
            using var stream = new MemoryStream();

            using (var thumbnail = Avalonia.PixelMan.PmBitmapFrame.CreateFromCopy(Avalonia.PixelMan.PmPixelFormat.Bgr24,
                       4, 3, 12, Enumerable.Repeat((byte)0x40, 36).ToArray()))
            using (var thumbnailBytes = new MemoryStream())
            using (var main = Avalonia.PixelMan.PmBitmapFrame.CreateFromCopy(Avalonia.PixelMan.PmPixelFormat.Bgr24,
                       16, 12, 48, new byte[16 * 12 * 3]))
            using (var metadata = Avalonia.PixelMan.PmMetadata.Create())
            {
                thumbnail.Encode(thumbnailBytes, Avalonia.PixelMan.PmCodecKind.Jpeg, cancellationToken: TestContext.Current.CancellationToken);

                using var blob = Avalonia.PixelMan.PmMetadataValue.Create(Avalonia.PixelMan.PmMetadataValueType.Undefined,
                    thumbnailBytes.ToArray());

                metadata.Write("/app1/thumb/{}", blob);
                main.SetMetadata(metadata);
                main.Encode(stream, Avalonia.PixelMan.PmCodecKind.Jpeg, cancellationToken: TestContext.Current.CancellationToken);
            }

            stream.Position = 0;

            using var scope = AvaloniaLocator.EnterScope();

            ImagingBackend.Register(new PixelManImagingBackend(allocator));

            using var decoder = BitmapDecoder.Create(stream);
            using var frame = decoder.ReadNextFrame()!;

            Assert.True(frame.TryGetThumbnail(out var thumb));
            Assert.Equal(new PixelSize(4, 3), thumb!.PixelSize);

            using (var pixels = thumb.Lock())
            {
                // Installed from the rental PixelMan decoded into, like any frame.
                Assert.Contains(pixels.Address, allocator.LiveAddresses);
            }

            Assert.True(frame.TryGetThumbnail(out var again));
            Assert.Same(thumb, again);
        }

        [Fact]
        public void A_Png_Should_Have_No_Thumbnail()
        {
            using var scope = Bind();
            using var stream = new MemoryStream();
            var encoder = new PngBitmapEncoder();

            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = Solid(2, 2, Colors.Red) });
            encoder.Save(stream, TestContext.Current.CancellationToken);
            stream.Position = 0;

            using var decoder = BitmapDecoder.Create(stream);
            using var frame = decoder.ReadNextFrame()!;

            Assert.False(frame.TryGetThumbnail(out _));
        }

        private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);

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
