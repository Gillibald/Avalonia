using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Imaging.TestKit.Instrumentation;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Png.Chunks;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Metadata.Profiles.Icc;
using SixLabors.ImageSharp.Metadata.Profiles.Xmp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;
using ISImage = SixLabors.ImageSharp.Image;

namespace Avalonia.Imaging.ImageSharp.Tests
{
    /// <summary>
    /// Backend-specific behavior beyond the shared contract suite: high bit depth,
    /// metadata and color profiles, multi-page TIFF, transform-on-encode, DPI, straight
    /// alpha, EXIF orientation, the decode concurrency gate and the measured
    /// fused-decode behavior.
    /// </summary>
    public class ImageSharpBackendTests
    {
        [Fact]
        public void OrientedJpeg_DefaultDecode_AppliesExifOrientation()
        {
            var jpeg = CreateOrientedJpeg();
            var backend = new ImageSharpImagingBackend();

            using var decoder = backend.CreateDecoder(new MemoryStream(jpeg), ownsStream: true);

            // Info reports the raw stored size; the frame is display-oriented.
            Assert.Equal(new PixelSize(8, 4), decoder.Info.PixelSize);

            using var frame = decoder.ReadNextFrame()!;

            Assert.Equal(new PixelSize(4, 8), frame.PixelSize);

            var fused = Assert.IsAssignableFrom<ISupportsFusedDecode>(frame);

            Assert.True((fused.FusedParts & FusedDecodeParts.Orientation) != 0);

            // The frame's pixels must match ImageSharp's own oriented decode exactly.
            using var reference = ISImage.Load<Rgb24>(jpeg);

            reference.Mutate(context => context.AutoOrient());

            Assert.Equal(PixelFormats.Rgb24, frame.PixelFormat);

            var bytes = ReadPixelBytes(frame);

            for (var y = 0; y < 8; y++)
            {
                for (var x = 0; x < 4; x++)
                {
                    var offset = (y * 4 + x) * 3;
                    var expected = reference[x, y];

                    Assert.Equal((expected.R, expected.G, expected.B),
                        (bytes[offset], bytes[offset + 1], bytes[offset + 2]));
                }
            }
        }

        [Fact]
        public void OrientedJpeg_RespectExifOrientationFalse_KeepsRawDimensions()
        {
            var jpeg = CreateOrientedJpeg();
            var backend = new ImageSharpImagingBackend();
            var options = new BitmapDecodeOptions { RespectExifOrientation = false };

            using var decoder = backend.CreateDecoder(new MemoryStream(jpeg), ownsStream: true, options);

            Assert.Equal(new PixelSize(8, 4), decoder.Info.PixelSize);

            using var frame = decoder.ReadNextFrame()!;

            Assert.Equal(new PixelSize(8, 4), frame.PixelSize);

            var fused = Assert.IsAssignableFrom<ISupportsFusedDecode>(frame);

            Assert.Equal(FusedDecodeParts.None, fused.FusedParts & FusedDecodeParts.Orientation);

            using var view = frame.Lock();

            Assert.Equal(new PixelSize(8, 4), view.Size);
        }

        [Fact]
        public void OrientedJpeg_TargetSizeComposesInOrientedSpace()
        {
            // Raw 8x4 with orientation 6 displays as 4x8; the plan target is oriented.
            // The loader resizes the raw image before orientation, so the backend swaps
            // the axes on the way in and the frame lands exactly on the oriented target.
            var jpeg = CreateOrientedJpeg();
            var backend = new ImageSharpImagingBackend();
            var options = new BitmapDecodeOptions { TargetSize = new PixelSize(2, 4) };

            using var decoder = backend.CreateDecoder(new MemoryStream(jpeg), ownsStream: true, options);

            Assert.Equal(new PixelSize(8, 4), decoder.Info.PixelSize);

            using var frame = decoder.ReadNextFrame()!;

            Assert.Equal(new PixelSize(2, 4), frame.PixelSize);

            var fused = Assert.IsAssignableFrom<ISupportsFusedDecode>(frame);

            Assert.Equal(FusedDecodeParts.Scale | FusedDecodeParts.Orientation, fused.FusedParts);

            using var view = frame.Lock();

            Assert.Equal(new PixelSize(2, 4), view.Size);
        }

        [Fact]
        public void TargetSize_PeakBelowFullDecode()
        {
            // Decoding a 1024x1024 JPEG to a 64x64 target must not pay for a full-size
            // frame in pooled memory: a full Bgra frame would be 1024 * 1024 * 4 = 4 MiB,
            // while the fused IDCT decode only rents the small destination (64 * 64
            // pixels at up to 4 bytes each, 16 KiB).
            var allocator = new CountingAllocator();
            var backend = new ImageSharpImagingBackend(allocator);

            using var source = new Image<Rgb24>(1024, 1024);

            var jpeg = EncodeImage(source, new JpegEncoder());
            var options = new BitmapDecodeOptions { TargetSize = new PixelSize(64, 64) };

            using (var decoder = backend.CreateDecoder(new MemoryStream(jpeg), ownsStream: true, options))
            using (var frame = decoder.ReadNextFrame()!)
            using (var view = frame.Lock())
            {
                Assert.Equal(new PixelSize(64, 64), view.Size);
            }

            var fullFrameBytes = 1024L * 1024 * 4;

            Assert.True(allocator.PeakLiveBytes < fullFrameBytes,
                $"Peak pooled bytes {allocator.PeakLiveBytes} must stay below a full-size " +
                $"frame of {fullFrameBytes} bytes.");

            allocator.AssertBalanced();
        }

        [Fact]
        public void ConcurrencyLimiter_SingleSlotDecodesComplete()
        {
            using var scope = AvaloniaLocator.EnterScope();

            AvaloniaLocator.CurrentMutable.Bind<ImagingOptions>()
                .ToConstant(new ImagingOptions { MaxConcurrentDecodes = 1 });

            var backend = new ImageSharpImagingBackend();

            using var source = new Image<Rgb24>(8, 8);

            var png = EncodeImage(source, new PngEncoder());

            // Two sequential decodes through a single slot must neither deadlock nor
            // leak the slot.
            for (var i = 0; i < 2; i++)
            {
                using var decoder = backend.CreateDecoder(new MemoryStream(png), ownsStream: true);
                using var frame = decoder.ReadNextFrame()!;
                using var view = frame.Lock();

                Assert.Equal(new PixelSize(8, 8), view.Size);
            }
        }

        [Fact]
        public void SixteenBitPng_DecodesToRgba64_PreservingDepth()
        {
            using var source = new Image<Rgba64>(2, 1);

            source[0, 0] = new Rgba64(0x1234, 0x5678, 0x9ABC, 0xFFFF);
            source[1, 0] = new Rgba64(0x0001, 0x8000, 0xFFFE, 0x7FFF);

            var png = EncodeImage(source, new PngEncoder { BitDepth = PngBitDepth.Bit16 });

            var backend = new ImageSharpImagingBackend();

            using var decoder = backend.CreateDecoder(new MemoryStream(png), ownsStream: true);
            using var frame = decoder.ReadNextFrame()!;

            Assert.Equal(PixelFormats.Rgba64, frame.PixelFormat);

            var bytes = ReadPixelBytes(frame);
            var channels = new ushort[8];

            for (var i = 0; i < channels.Length; i++)
                channels[i] = BitConverter.ToUInt16(bytes, i * 2);

            Assert.Equal(new ushort[] { 0x1234, 0x5678, 0x9ABC, 0xFFFF, 0x0001, 0x8000, 0xFFFE, 0x7FFF }, channels);
        }

        [Fact]
        public void ExifMetadata_ReadsPhotoShortcutsAndQueries()
        {
            var jpeg = CreateJpegWithExif();
            var backend = new ImageSharpImagingBackend();

            using var decoder = backend.CreateDecoder(new MemoryStream(jpeg), ownsStream: true);
            using var frame = decoder.ReadNextFrame()!;

            var source = Assert.IsAssignableFrom<IMetadataSource>(frame);
            var metadata = source.Metadata;
            var photo = Assert.IsAssignableFrom<IPhotoMetadata>(metadata);

            Assert.Equal("TestMake", photo.CameraManufacturer);
            Assert.Equal("TestModel", photo.CameraModel);
            Assert.Equal("A title", photo.Title);
            Assert.Equal(new DateTime(2026, 7, 2, 12, 34, 56), photo.DateTaken);
            Assert.Equal(new[] { "Alice", "Bob" }, photo.Authors);
            Assert.Equal("(c) test", photo.Copyright);

            Assert.Equal("TestMake", metadata.GetQuery("/app1/ifd/{ushort=271}"));
            Assert.Equal("TestModel", metadata.GetQuery("/app1/ifd/exif/{ushort=272}"));
            Assert.True(metadata.ContainsQuery("/app1/ifd/{ushort=36867}"));
            Assert.Null(metadata.GetQuery("/app1/ifd/{ushort=42}"));
        }

        [Fact]
        public void ExifMetadata_RoundTripsThroughEncode()
        {
            var jpeg = CreateJpegWithExif();
            var backend = new ImageSharpImagingBackend();

            BitmapMetadata metadata;

            using (var decoder = backend.CreateDecoder(new MemoryStream(jpeg), ownsStream: true))
            using (var frame = decoder.ReadNextFrame()!)
            {
                metadata = ((IMetadataSource)frame).Metadata.Clone();
            }

            ((IPhotoMetadata)metadata).CameraModel = "Repacked";
            metadata.SetQuery("/app1/ifd/{ushort=270}", "New title");

            var encoder = new JpegBitmapEncoder
            {
                Metadata = metadata,
            };

            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = CreatePixelBuffer(4, 4, (255, 0, 0, 255)) });

            using var stream = new MemoryStream();

            backend.CreateEncoder(BitmapContainerFormats.Jpeg).Encode(encoder, stream, TestContext.Current.CancellationToken);
            stream.Position = 0;

            using var roundTripped = backend.CreateDecoder(stream, ownsStream: false);
            using var page = roundTripped.ReadNextFrame()!;

            var photo = Assert.IsAssignableFrom<IPhotoMetadata>(((IMetadataSource)page).Metadata);

            Assert.Equal("TestMake", photo.CameraManufacturer);
            Assert.Equal("Repacked", photo.CameraModel);
            Assert.Equal("New title", photo.Title);
        }

        [Fact]
        public void XmpPacket_RoundTripsThroughEncode()
        {
            const string packet = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"></x:xmpmeta>";

            using var source = new Image<Rgb24>(4, 4);

            source.Metadata.XmpProfile = new XmpProfile(System.Text.Encoding.UTF8.GetBytes(packet));

            var jpeg = EncodeImage(source, new JpegEncoder());
            var backend = new ImageSharpImagingBackend();

            BitmapMetadata metadata;

            using (var decoder = backend.CreateDecoder(new MemoryStream(jpeg), ownsStream: true))
            using (var frame = decoder.ReadNextFrame()!)
            {
                metadata = ((IMetadataSource)frame).Metadata.Clone();
            }

            Assert.Equal(packet, metadata.XmpPacket);

            const string updated = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><!-- updated --></x:xmpmeta>";

            metadata.XmpPacket = updated;

            var encoder = new JpegBitmapEncoder { Metadata = metadata };

            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = CreatePixelBuffer(4, 4, (0, 255, 0, 255)) });

            using var stream = new MemoryStream();

            backend.CreateEncoder(BitmapContainerFormats.Jpeg).Encode(encoder, stream, TestContext.Current.CancellationToken);
            stream.Position = 0;

            using var roundTripped = backend.CreateDecoder(stream, ownsStream: false);
            using var page = roundTripped.ReadNextFrame()!;

            Assert.Equal(updated, ((IMetadataSource)page).Metadata.XmpPacket);
        }

        [Fact]
        public void IccProfile_SurfacesAndRoundTripsThroughEncode()
        {
            var profileBytes = CreateIccProfileBytes();

            using var source = new Image<Rgb24>(4, 4);

            source.Metadata.IccProfile = new IccProfile(profileBytes);

            var png = EncodeImage(source, new PngEncoder());
            var backend = new ImageSharpImagingBackend();

            using (var decoder = backend.CreateDecoder(new MemoryStream(png), ownsStream: true))
            using (var frame = decoder.ReadNextFrame()!)
            {
                var colorProfileSource = Assert.IsAssignableFrom<IColorProfileSource>(frame);

                Assert.Equal(profileBytes, colorProfileSource.IccProfile.ToArray());
            }

            var encoder = new PngBitmapEncoder { ColorProfile = profileBytes };

            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = CreatePixelBuffer(4, 4, (0, 0, 255, 255)) });

            using var stream = new MemoryStream();

            backend.CreateEncoder(BitmapContainerFormats.Png).Encode(encoder, stream, TestContext.Current.CancellationToken);
            stream.Position = 0;

            using var roundTripped = backend.CreateDecoder(stream, ownsStream: false);
            using var page = roundTripped.ReadNextFrame()!;

            var roundTrippedProfile = Assert.IsAssignableFrom<IColorProfileSource>(page);

            Assert.Equal(profileBytes, roundTrippedProfile.IccProfile.ToArray());
        }

        [Fact]
        public void PngTextMetadata_AnswersQueries()
        {
            using var source = new Image<Rgb24>(4, 4);

            source.Metadata.GetPngMetadata().TextData.Add(
                new PngTextData("Title", "Hello PNG", string.Empty, string.Empty));

            var png = EncodeImage(source, new PngEncoder());
            var backend = new ImageSharpImagingBackend();

            using var decoder = backend.CreateDecoder(new MemoryStream(png), ownsStream: true);
            using var frame = decoder.ReadNextFrame()!;

            var metadata = Assert.IsAssignableFrom<IMetadataSource>(frame).Metadata;

            Assert.Equal("PNG", metadata.Format);
            Assert.Equal("Hello PNG", metadata.GetQuery("/text/{str=Title}"));
            Assert.True(metadata.ContainsQuery("/text/{str=Title}"));
            Assert.Null(metadata.GetQuery("/text/{str=Missing}"));
        }

        [Fact]
        public void MultiPageTiff_DecodesFramesIndependently()
        {
            var tiff = CreateTwoPageTiff();
            var backend = new ImageSharpImagingBackend();

            using var decoder = backend.CreateDecoder(new MemoryStream(tiff), ownsStream: true);

            Assert.Equal(2, decoder.FrameCount);

            var seekable = Assert.IsAssignableFrom<ISeekableFrameDecoder>(decoder);

            using var second = seekable.GetFrame(1);
            using var first = seekable.GetFrame(0);

            AssertSolidColor(first, 255, 0, 0);
            AssertSolidColor(second, 0, 255, 0);
        }

        [Fact]
        public void MultiPageTiff_SaveRoundTripsBothPages()
        {
            var backend = new ImageSharpImagingBackend();

            using var scope = AvaloniaLocator.EnterScope();

            ImagingBackend.Register(backend);

            var encoder = new TiffBitmapEncoder();

            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = CreatePixelBuffer(4, 4, (255, 0, 0, 255)) });
            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = CreatePixelBuffer(4, 4, (0, 255, 0, 255)) });

            using var stream = new MemoryStream();

            encoder.Save(stream, TestContext.Current.CancellationToken);
            stream.Position = 0;

            using var decoder = backend.CreateDecoder(stream, ownsStream: false);

            Assert.Equal(2, decoder.FrameCount);

            var seekable = (ISeekableFrameDecoder)decoder;

            using var first = seekable.GetFrame(0);
            using var second = seekable.GetFrame(1);

            AssertSolidColor(first, 255, 0, 0);
            AssertSolidColor(second, 0, 255, 0);
        }

        [Fact]
        public void Rotate90OnEncode_MovesPixelsClockwise()
        {
            var backend = new ImageSharpImagingBackend();

            // Left red, right green; a clockwise quarter turn puts red on top.
            var pixels = CreatePixelBuffer(2, 1, (x, _) => x == 0
                ? ((byte)255, (byte)0, (byte)0, (byte)255)
                : ((byte)0, (byte)255, (byte)0, (byte)255));

            var encoder = new PngBitmapEncoder
            {
                Transform = new BitmapTransform(BitmapRotation.Rotate90, FlipHorizontal: false, FlipVertical: false),
            };

            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = pixels });

            using var stream = new MemoryStream();

            backend.CreateEncoder(BitmapContainerFormats.Png).Encode(encoder, stream, TestContext.Current.CancellationToken);
            stream.Position = 0;

            using var decoder = backend.CreateDecoder(stream, ownsStream: false);
            using var frame = decoder.ReadNextFrame()!;

            Assert.Equal(new PixelSize(1, 2), frame.PixelSize);

            var rgba = ReadRgba(frame);

            Assert.Equal((255, 0, 0, 255), rgba[0]);
            Assert.Equal((0, 255, 0, 255), rgba[1]);
        }

        [Fact]
        public void Jpeg300Dpi_SurfacesInfoAndFrameDpi()
        {
            using var source = new Image<Rgb24>(4, 4);

            source.Metadata.ResolutionUnits = SixLabors.ImageSharp.Metadata.PixelResolutionUnit.PixelsPerInch;
            source.Metadata.HorizontalResolution = 300;
            source.Metadata.VerticalResolution = 300;

            var jpeg = EncodeImage(source, new JpegEncoder());
            var backend = new ImageSharpImagingBackend();

            using (var stream = new MemoryStream(jpeg))
            {
                Assert.True(backend.TryIdentify(stream, out var info));
                Assert.Equal(new Vector(300, 300), info.Dpi);
            }

            using var decoder = backend.CreateDecoder(new MemoryStream(jpeg), ownsStream: true);
            using var frame = decoder.ReadNextFrame()!;

            Assert.Equal(new Vector(300, 300), frame.Dpi);
        }

        [Fact]
        public void AlphaPng_DecodesStraightAlphaValues()
        {
            using var source = new Image<Rgba32>(1, 1);

            source[0, 0] = new Rgba32(100, 150, 200, 128);

            var png = EncodeImage(source, new PngEncoder());
            var backend = new ImageSharpImagingBackend();

            using var decoder = backend.CreateDecoder(new MemoryStream(png), ownsStream: true);
            using var frame = decoder.ReadNextFrame()!;

            Assert.Equal(AlphaFormat.Unpremul, frame.AlphaFormat);
            Assert.Equal(PixelFormats.Rgba8888, frame.PixelFormat);

            var rgba = ReadRgba(frame);

            Assert.Equal((100, 150, 200, 128), rgba[0]);
        }

        [Fact]
        public void Backend_DoesNotMutateConfigurationDefault()
        {
            Assert.False(Configuration.Default.PreferContiguousImageBuffers);

            using var source = new Image<Rgb24>(4, 4);

            var png = EncodeImage(source, new PngEncoder());
            var backend = new ImageSharpImagingBackend();

            using (var decoder = backend.CreateDecoder(new MemoryStream(png), ownsStream: true))
            using (var frame = decoder.ReadNextFrame()!)
            using (frame.Lock())
            {
            }

            Assert.False(Configuration.Default.PreferContiguousImageBuffers);
        }

        [Fact]
        public void TargetSize_FusesJpegOnly_FramesAlwaysExact()
        {
            var backend = new ImageSharpImagingBackend();

            // Measured on ImageSharp 4.0: TargetSize reduces the decode itself for JPEG
            // (IDCT scaling); PNG decodes at native size and resizes afterwards, so the
            // shared pipeline applies its scaling instead.
            var jpegCodec = backend.SupportedCodecs.First(c => c.FormatName == "JPEG");
            var pngCodec = backend.SupportedCodecs.First(c => c.FormatName == "PNG");

            Assert.NotEqual(0, (int)(jpegCodec.Capabilities & BitmapCodecCapabilities.FusedDecode));
            Assert.Equal(0, (int)(pngCodec.Capabilities & BitmapCodecCapabilities.FusedDecode));

            using var source = new Image<Rgb24>(8, 8);

            var options = new BitmapDecodeOptions { TargetSize = new PixelSize(4, 4) };

            using (var decoder = backend.CreateDecoder(
                new MemoryStream(EncodeImage(source, new JpegEncoder())), ownsStream: true, options))
            using (var frame = decoder.ReadNextFrame()!)
            {
                Assert.Equal(new PixelSize(4, 4), frame.PixelSize);

                var fused = Assert.IsAssignableFrom<ISupportsFusedDecode>(frame);

                Assert.Equal(FusedDecodeParts.Scale, fused.FusedParts);

                using var view = frame.Lock();

                Assert.Equal(new PixelSize(4, 4), view.Size);
            }

            using (var decoder = backend.CreateDecoder(
                new MemoryStream(EncodeImage(source, new PngEncoder())), ownsStream: true, options))
            using (var frame = decoder.ReadNextFrame()!)
            {
                Assert.Equal(new PixelSize(4, 4), frame.PixelSize);

                var fused = Assert.IsAssignableFrom<ISupportsFusedDecode>(frame);

                Assert.Equal(FusedDecodeParts.None, fused.FusedParts);

                using var view = frame.Lock();

                Assert.Equal(new PixelSize(4, 4), view.Size);
            }
        }

        private static byte[] CreateJpegWithExif()
        {
            using var source = new Image<Rgb24>(4, 4);

            var exif = new ExifProfile();

            exif.SetValue(ExifTag.Make, "TestMake");
            exif.SetValue(ExifTag.Model, "TestModel");
            exif.SetValue(ExifTag.ImageDescription, "A title");
            exif.SetValue(ExifTag.Artist, "Alice;Bob");
            exif.SetValue(ExifTag.Copyright, "(c) test");
            exif.SetValue(ExifTag.DateTimeOriginal, "2026:07:02 12:34:56");

            source.Metadata.ExifProfile = exif;

            return EncodeImage(source, new JpegEncoder());
        }

        private static byte[] CreateOrientedJpeg()
        {
            // Raw 8x4, left half dark and right half bright, tagged EXIF orientation 6
            // (the raw image must rotate 90 degrees clockwise to display upright).
            using var source = new Image<Rgb24>(8, 4);

            source.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);

                    for (var x = 0; x < row.Length; x++)
                        row[x] = x < 4 ? new Rgb24(10, 10, 10) : new Rgb24(240, 240, 240);
                }
            });

            var exif = new ExifProfile();

            exif.SetValue(ExifTag.Orientation, (ushort)6);
            source.Metadata.ExifProfile = exif;

            return EncodeImage(source, new JpegEncoder());
        }

        private static byte[] CreateTwoPageTiff()
        {
            using var first = new Image<Rgba32>(4, 4, new Rgba32(255, 0, 0));
            using var second = new Image<Rgba32>(4, 4, new Rgba32(0, 255, 0));

            first.Frames.AddFrame(second.Frames.RootFrame);

            return EncodeImage(first, new TiffEncoder());
        }

        private static byte[] CreateIccProfileBytes()
        {
            // A minimal header-only profile: 132 bytes, 'acsp' signature, no tags.
            var bytes = new byte[132];

            bytes[3] = 132;
            bytes[36] = (byte)'a';
            bytes[37] = (byte)'c';
            bytes[38] = (byte)'s';
            bytes[39] = (byte)'p';

            return bytes;
        }

        private static byte[] EncodeImage(ISImage image, IImageEncoder encoder)
        {
            using var stream = new MemoryStream();

            image.Save(stream, encoder);

            return stream.ToArray();
        }

        private static PixelBuffer CreatePixelBuffer(int width, int height, (byte R, byte G, byte B, byte A) color) =>
            CreatePixelBuffer(width, height, (_, _) => color);

        private static PixelBuffer CreatePixelBuffer(int width, int height,
            Func<int, int, (byte R, byte G, byte B, byte A)> pixelAt)
        {
            var bytes = new byte[width * height * 4];

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var (r, g, b, a) = pixelAt(x, y);
                    var offset = (y * width + x) * 4;

                    bytes[offset] = r;
                    bytes[offset + 1] = g;
                    bytes[offset + 2] = b;
                    bytes[offset + 3] = a;
                }
            }

            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);

            try
            {
                using var framebuffer = new LockedFramebuffer(handle.AddrOfPinnedObject(),
                    new PixelSize(width, height), width * 4, new Vector(96, 96),
                    PixelFormats.Rgba8888, AlphaFormat.Unpremul, null);

                return PixelBuffer.CopyFrom(framebuffer);
            }
            finally
            {
                handle.Free();
            }
        }

        private static byte[] ReadPixelBytes(IBitmapFrameSource frame)
        {
            using var view = frame.Lock();

            var count = view.RowBytes * view.Size.Height;
            var bytes = new byte[count];

            Marshal.Copy(view.Address, bytes, 0, count);

            return bytes;
        }

        private static (byte R, byte G, byte B, byte A)[] ReadRgba(IBitmapFrameSource frame)
        {
            Assert.Equal(PixelFormats.Rgba8888, frame.PixelFormat);

            var bytes = ReadPixelBytes(frame);
            var pixels = new (byte R, byte G, byte B, byte A)[bytes.Length / 4];

            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = (bytes[i * 4], bytes[i * 4 + 1], bytes[i * 4 + 2], bytes[i * 4 + 3]);

            return pixels;
        }

        private static void AssertSolidColor(IBitmapFrameSource frame, byte r, byte g, byte b)
        {
            foreach (var pixel in ReadRgba(frame))
            {
                Assert.Equal((r, g, b, (byte)255), pixel);
            }
        }
    }
}
