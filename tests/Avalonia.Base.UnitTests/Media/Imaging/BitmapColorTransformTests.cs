using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Imaging
{
    public class BitmapColorTransformTests
    {
        private static readonly byte[] s_rgb = Profile("RGB ");
        private static readonly byte[] s_gray = Profile("GRAY");
        private static readonly byte[] s_cmyk = Profile("CMYK");

        [Fact]
        public void Create_Should_Throw_When_The_Backend_Has_No_Color_Management()
        {
            using var scope = AvaloniaLocator.EnterScope();

            ImagingBackend.Register(new PlainBackend());

            Assert.False(BitmapColorTransform.IsSupported);
            Assert.Throws<NotSupportedException>(() => BitmapColorTransform.Create(s_rgb, s_rgb));
        }

        [Fact]
        public void Profiles_Should_Be_Checked_Before_The_Backend_Is_Asked()
        {
            using var scope = AvaloniaLocator.EnterScope();

            var backend = Register(Identity);

            Assert.Throws<ArgumentException>(() => BitmapColorTransform.Create(new byte[127], s_rgb));
            Assert.Throws<ArgumentException>(() => BitmapColorTransform.Create(s_rgb, new byte[128]));
            Assert.Throws<NotSupportedException>(() => BitmapColorTransform.Create(Profile("Lab "), s_rgb));
            Assert.Equal(0, backend.Created);
        }

        [Fact]
        public void The_Source_Header_Should_Supply_The_Default_Intent()
        {
            using var scope = AvaloniaLocator.EnterScope();

            var backend = Register(Identity);

            using var stored = BitmapColorTransform.Create(Profile("RGB ", intent: 2), s_rgb);
            using var explicitIntent = BitmapColorTransform.Create(Profile("RGB ", intent: 2), s_rgb,
                ColorRenderingIntent.AbsoluteColorimetric);

            Assert.Equal(ColorRenderingIntent.Saturation, stored.Intent);
            Assert.Equal(ColorRenderingIntent.AbsoluteColorimetric, backend.LastIntent);
        }

        [Fact]
        public void Apply_Should_Transform_Color_Channels_And_Keep_Alpha()
        {
            using var scope = AvaloniaLocator.EnterScope();

            Register(Invert);

            using var transform = BitmapColorTransform.Create(s_rgb, s_rgb);

            var source = PixelBuffer.Create(new byte[] { 10, 20, 30, 128 }, new PixelSize(1, 1), 4,
                PixelFormats.Bgra8888, AlphaFormat.Unpremul, new Vector(144, 72));
            var result = transform.Apply(source, PixelFormats.Bgra8888);

            Assert.Equal(AlphaFormat.Unpremul, result.AlphaFormat);
            Assert.Equal(new Vector(144, 72), result.Dpi);
            Assert.Equal(new byte[] { 245, 235, 225, 128 }, result.Pixels.ToArray());
        }

        [Fact]
        public void Apply_Should_Keep_A_Premultiplied_Source_Premultiplied()
        {
            using var scope = AvaloniaLocator.EnterScope();

            Register(Identity);

            using var transform = BitmapColorTransform.Create(s_rgb, s_rgb);

            var pixels = new byte[] { 0, 32, 64, 128, 255, 255, 255, 255 };
            var source = PixelBuffer.Create(pixels, new PixelSize(2, 1), 8, PixelFormats.Bgra8888, AlphaFormat.Premul);
            var result = transform.Apply(source, PixelFormats.Bgra8888);

            Assert.Equal(AlphaFormat.Premul, result.AlphaFormat);
            AssertClose(pixels, result.Pixels.ToArray(), 1);
        }

        [Fact]
        public void Apply_Should_Expand_An_Indexed_Source_Through_Its_Palette()
        {
            using var scope = AvaloniaLocator.EnterScope();

            Register(Invert);

            using var transform = BitmapColorTransform.Create(s_rgb, s_rgb);

            var palette = new BitmapPalette(new[] { Color.FromRgb(0, 0, 0), Color.FromRgb(255, 0, 51) });
            var source = PixelBuffer.Create(new byte[] { 0b0100_0000 }, new PixelSize(2, 1), 1,
                PixelFormats.Indexed1, AlphaFormat.Opaque, palette: palette);
            var result = transform.Apply(source, PixelFormats.Rgb24);

            Assert.Equal(new byte[] { 255, 255, 255, 0, 255, 204 }, result.Pixels.ToArray());
        }

        [Fact]
        public void Apply_Should_Write_Cmyk_Channels_Without_Passing_Through_Rgb()
        {
            using var scope = AvaloniaLocator.EnterScope();

            Register(ToCmyk);

            using var transform = BitmapColorTransform.Create(s_rgb, s_cmyk);

            var source = PixelBuffer.Create(new byte[] { 255, 0, 51 }, new PixelSize(1, 1), 3,
                PixelFormats.Rgb24, AlphaFormat.Opaque);

            Assert.Equal(new byte[] { 0, 255, 204, 128 }, transform.Apply(source, PixelFormats.Cmyk32).Pixels.ToArray());

            var wide = transform.Apply(source, PixelFormats.Cmyk64).Pixels.ToArray();

            Assert.Equal(new ushort[] { 0, 65535, 52428, 32768 },
                new[] { BitConverter.ToUInt16(wide, 0), BitConverter.ToUInt16(wide, 2),
                    BitConverter.ToUInt16(wide, 4), BitConverter.ToUInt16(wide, 6) });
        }

        [Fact]
        public void Apply_Should_Read_Cmyk_Channels_As_Stored()
        {
            using var scope = AvaloniaLocator.EnterScope();

            Register(FromCmyk);

            using var transform = BitmapColorTransform.Create(s_cmyk, s_rgb);

            var source = PixelBuffer.Create(new byte[] { 0, 255, 204, 17 }, new PixelSize(1, 1), 4,
                PixelFormats.Cmyk32, AlphaFormat.Opaque);

            Assert.Equal(new byte[] { 255, 0, 51 }, transform.Apply(source, PixelFormats.Rgb24).Pixels.ToArray());
        }

        [Fact]
        public void Apply_Should_Transform_Gray_Levels()
        {
            using var scope = AvaloniaLocator.EnterScope();

            Register(Invert);

            using var transform = BitmapColorTransform.Create(s_gray, s_gray);

            var source = PixelBuffer.Create(new byte[] { 0, 64, 255 }, new PixelSize(3, 1), 3,
                PixelFormats.Gray8, AlphaFormat.Opaque);

            Assert.Equal(new byte[] { 255, 191, 0 }, transform.Apply(source, PixelFormats.Gray8).Pixels.ToArray());
        }

        [Fact]
        public void Apply_Should_Reject_Formats_Of_Another_Color_Model()
        {
            using var scope = AvaloniaLocator.EnterScope();

            Register(Identity);

            using var transform = BitmapColorTransform.Create(s_rgb, s_rgb);

            var gray = PixelBuffer.Create(new byte[] { 0 }, new PixelSize(1, 1), 1, PixelFormats.Gray8, AlphaFormat.Opaque);
            var rgb = PixelBuffer.Create(new byte[] { 0, 0, 0 }, new PixelSize(1, 1), 3, PixelFormats.Rgb24,
                AlphaFormat.Opaque);

            Assert.Throws<ArgumentException>(() => transform.Apply(gray, PixelFormats.Rgb24));
            Assert.Throws<ArgumentException>(() => transform.Apply(rgb, PixelFormats.Cmyk32));
            Assert.Throws<ArgumentException>(() => transform.Apply(rgb, PixelFormats.Indexed8));
        }

        [Fact]
        public void TransformColors_Should_Clamp_Both_Ends()
        {
            using var scope = AvaloniaLocator.EnterScope();

            Register(Scale2);

            using var transform = BitmapColorTransform.Create(s_rgb, s_rgb);

            var destination = new float[3];

            transform.TransformColors(new[] { 1.5f, 0.25f, -1f }, destination);

            Assert.Equal(new[] { 1f, 0.5f, 0f }, destination);
            Assert.Throws<ArgumentException>(() => transform.TransformColors(new float[4], new float[6]));
            Assert.Throws<ArgumentException>(() => transform.TransformColors(new float[6], new float[3]));
        }

        [Fact]
        public void Dispose_Should_Release_The_Backend_Transform()
        {
            using var scope = AvaloniaLocator.EnterScope();

            var backend = Register(Identity);
            var transform = BitmapColorTransform.Create(s_rgb, s_rgb);

            transform.Dispose();

            Assert.True(backend.LastImpl!.Disposed);
            Assert.Throws<ObjectDisposedException>(() => transform.TransformColors(new float[3], new float[3]));
        }

        private static void Identity(float[] source, float[] destination) => source.CopyTo(destination, 0);

        private static void Invert(float[] source, float[] destination)
        {
            for (var i = 0; i < source.Length; i++)
                destination[i] = 1 - source[i];
        }

        private static void Scale2(float[] source, float[] destination)
        {
            for (var i = 0; i < source.Length; i++)
                destination[i] = source[i] * 2;
        }

        private static void ToCmyk(float[] rgb, float[] cmyk)
        {
            cmyk[0] = 1 - rgb[0];
            cmyk[1] = 1 - rgb[1];
            cmyk[2] = 1 - rgb[2];
            cmyk[3] = 0.5f;
        }

        private static void FromCmyk(float[] cmyk, float[] rgb)
        {
            rgb[0] = 1 - cmyk[0];
            rgb[1] = 1 - cmyk[1];
            rgb[2] = 1 - cmyk[2];
        }

        private static ColorBackend Register(Action<float[], float[]> map)
        {
            var backend = new ColorBackend(map);

            ImagingBackend.Register(backend);

            return backend;
        }

        private static byte[] Profile(string colorSpace, uint intent = 0)
        {
            var bytes = new byte[128];

            bytes[3] = 128;

            for (var i = 0; i < 4; i++)
            {
                bytes[16 + i] = (byte)colorSpace[i];
                bytes[36 + i] = (byte)"acsp"[i];
            }

            bytes[67] = (byte)intent;

            return bytes;
        }

        private static int ChannelCount(ReadOnlySpan<byte> profile) => (char)profile[16] switch
        {
            'G' => 1,
            'C' => 4,
            _ => 3,
        };

        private static void AssertClose(byte[] expected, byte[] actual, int tolerance)
        {
            Assert.Equal(expected.Length, actual.Length);

            for (var i = 0; i < expected.Length; i++)
                Assert.True(Math.Abs(expected[i] - actual[i]) <= tolerance,
                    $"Byte {i}: expected {expected[i]}, got {actual[i]}.");
        }

        private sealed class FakeTransform : IColorTransformImpl
        {
            private readonly Action<float[], float[]> _map;
            private readonly int _sourceChannels;
            private readonly int _destinationChannels;

            public FakeTransform(Action<float[], float[]> map, int sourceChannels, int destinationChannels)
            {
                _map = map;
                _sourceChannels = sourceChannels;
                _destinationChannels = destinationChannels;
            }

            public bool Disposed { get; private set; }

            public void Transform(ReadOnlySpan<float> source, Span<float> destination, int pixelCount)
            {
                var input = new float[_sourceChannels];
                var output = new float[_destinationChannels];

                for (var x = 0; x < pixelCount; x++)
                {
                    source.Slice(x * _sourceChannels, _sourceChannels).CopyTo(input);
                    _map(input, output);
                    output.CopyTo(destination.Slice(x * _destinationChannels, _destinationChannels));
                }
            }

            public void Dispose() => Disposed = true;
        }

        private class PlainBackend : IImagingBackend
        {
            public string Name => "Plain";

            public IReadOnlyList<IBitmapCodecInfo> SupportedCodecs => Array.Empty<IBitmapCodecInfo>();

            public int IdentifyPrefixLength => 0;

            public bool TryIdentify(Stream stream, out BitmapImageInfo info) => throw new NotSupportedException();

            public bool TryIdentify(ReadOnlySpan<byte> data, out BitmapImageInfo info) => throw new NotSupportedException();

            public IBitmapDecoder CreateDecoder(Stream stream, bool ownsStream, BitmapDecodeOptions? options = null)
                => throw new NotSupportedException();

            public IBitmapEncoderImpl CreateEncoder(Guid containerFormat) => throw new NotSupportedException();
        }

        private sealed class ColorBackend : PlainBackend, IColorTransformProvider
        {
            private readonly Action<float[], float[]> _map;

            public ColorBackend(Action<float[], float[]> map) => _map = map;

            public int Created { get; private set; }

            public ColorRenderingIntent LastIntent { get; private set; }

            public FakeTransform? LastImpl { get; private set; }

            public IColorTransformImpl CreateColorTransform(ReadOnlySpan<byte> sourceProfile,
                ReadOnlySpan<byte> destinationProfile, ColorRenderingIntent intent)
            {
                Created++;
                LastIntent = intent;
                LastImpl = new FakeTransform(_map, ChannelCount(sourceProfile), ChannelCount(destinationProfile));

                return LastImpl;
            }
        }
    }
}
