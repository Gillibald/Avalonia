using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Imaging.PixelMan.Tests
{
    public class PixelManCmykProfileTests
    {
        /// <summary>L* 50 gray through sRGB.</summary>
        private const byte MidGray = 119;

        private static readonly byte[] s_midGray = { MidGray, MidGray, MidGray, 255, MidGray, MidGray, MidGray, 255 };

        [Fact]
        public void Cmyk_Conversion_Should_Use_The_Cmyk_Profile()
        {
            using var scope = Bind(MidGrayProfile());

            Assert.Equal(s_midGray, ToBgra(CmykPixels()));
        }

        [Fact]
        public void Cmyk_Conversion_Without_A_Profile_Should_Stay_Analytic()
        {
            using var scope = Bind(null);

            Assert.Equal(new byte[] { 255, 255, 255, 255, 0, 0, 0, 255 }, ToBgra(CmykPixels()));
        }

        [Fact]
        public void Decoding_Cmyk_To_Bgra_Should_Use_The_Cmyk_Profile()
        {
            byte[] tiff;

            using (Bind(null))
                tiff = Encode(new TiffBitmapEncoder());

            using var scope = Bind(MidGrayProfile());
            using var decoder = BitmapDecoder.Create(new MemoryStream(tiff),
                new BitmapDecodeOptions { TargetFormat = PixelFormats.Bgra8888, TargetAlphaFormat = AlphaFormat.Unpremul });
            using var frame = decoder.GetFrame(0);

            Assert.Equal(s_midGray, ToBgra(frame.ToPixelBuffer()));
        }

        [Fact]
        public void Encoding_A_Cmyk_Frame_As_Gif_Should_Use_The_Cmyk_Profile()
        {
            using var scope = Bind(MidGrayProfile());
            var gif = Encode(new GifBitmapEncoder());
            using var decoder = BitmapDecoder.Create(new MemoryStream(gif));
            using var frame = decoder.GetFrame(0);

            Assert.Equal(s_midGray, ToBgra(frame.ToPixelBuffer()));
        }

        [Fact]
        public void A_Profile_That_Is_Not_Cmyk_Should_Throw()
        {
            var rgb = MidGrayProfile();

            "RGB "u8.CopyTo(rgb.AsSpan(16));

            using var scope = Bind(rgb);

            Assert.ThrowsAny<ArgumentException>(() => ToBgra(CmykPixels()));
        }

        private static PixelBuffer CmykPixels() => PixelBuffer.Create(new byte[] { 0, 0, 0, 0, 0, 0, 0, 255 },
            new PixelSize(2, 1), 8, PixelFormats.Cmyk32, AlphaFormat.Opaque);

        private static byte[] ToBgra(PixelBuffer pixels) =>
            pixels.Convert(PixelFormats.Bgra8888, AlphaFormat.Unpremul).Pixels.ToArray();

        private static byte[] Encode(BitmapEncoder encoder)
        {
            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = CmykPixels() });

            using var stream = new MemoryStream();

            encoder.Save(stream, TestContext.Current.CancellationToken);

            return stream.ToArray();
        }

        /// <summary>A CMYK printer profile whose lut16 A2B0 maps every CMYK value to L* 50 gray.</summary>
        private static byte[] MidGrayProfile()
        {
            var lut = new List<byte>("mft2\0\0\0\0"u8.ToArray()) { 4, 3, 2, 0 };

            void U16(ushort v)
            {
                var b = new byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(b, v);
                lut.AddRange(b);
            }

            for (var r = 0; r < 3; r++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var b = new byte[4];
                    BinaryPrimitives.WriteInt32BigEndian(b, r == c ? 65536 : 0);
                    lut.AddRange(b);
                }
            }

            U16(2);
            U16(2);

            for (var i = 0; i < 4; i++)
            {
                U16(0);
                U16(65535);
            }

            for (var i = 0; i < 16; i++)
            {
                U16(0x7F80);
                U16(0x8000);
                U16(0x8000);
            }

            for (var i = 0; i < 3; i++)
            {
                U16(0);
                U16(65535);
            }

            var profile = new byte[144 + lut.Count];

            BinaryPrimitives.WriteUInt32BigEndian(profile, (uint)profile.Length);
            BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(8), 0x0210_0000);
            "prtrCMYKLab "u8.CopyTo(profile.AsSpan(12));
            "acsp"u8.CopyTo(profile.AsSpan(36));
            BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(68), (int)Math.Round(0.9642 * 65536));
            BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(72), 65536);
            BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(76), (int)Math.Round(0.8249 * 65536));
            BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(128), 1);
            "A2B0"u8.CopyTo(profile.AsSpan(132));
            BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(136), 144);
            BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(140), (uint)lut.Count);
            lut.CopyTo(profile, 144);

            return profile;
        }

        private static IDisposable Bind(byte[]? cmykProfile)
        {
            var scope = AvaloniaLocator.EnterScope();

            ImagingBackend.Register(new PixelManImagingBackend());
            AvaloniaLocator.CurrentMutable.Bind<ImagingOptions>().ToConstant(new ImagingOptions { CmykProfile = cmykProfile });

            return scope;
        }
    }
}
