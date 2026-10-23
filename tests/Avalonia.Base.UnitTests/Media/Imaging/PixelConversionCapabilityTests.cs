using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Imaging
{
    public class PixelConversionCapabilityTests
    {
        private static PixelBuffer Bgra() => PixelBuffer.Create(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 },
            new PixelSize(2, 1), 8, PixelFormats.Bgra8888, AlphaFormat.Unpremul);

        [Fact]
        public void A_Backend_Converter_Should_Do_The_Conversion_When_Available()
        {
            using var scope = AvaloniaLocator.EnterScope();

            var backend = new ConvertingBackend(accept: true);

            ImagingBackend.Register(backend);

            var dest = new byte[6];

            Bgra().CopyPixels(new PixelRect(0, 0, 2, 1), dest, 6, PixelFormats.Rgb24, AlphaFormat.Opaque);

            Assert.Equal(1, backend.Calls);
            Assert.Equal(new PixelRect(0, 0, 2, 1), backend.LastRect);
            Assert.Equal(new byte[] { 0xC0, 0xC0, 0xC0, 0xC0, 0xC0, 0xC0 }, dest);
        }

        [Fact]
        public void A_Declining_Backend_Should_Fall_Back_To_The_Core_Converter()
        {
            using var scope = AvaloniaLocator.EnterScope();

            var backend = new ConvertingBackend(accept: false);

            ImagingBackend.Register(backend);

            var dest = new byte[6];

            Bgra().CopyPixels(new PixelRect(0, 0, 2, 1), dest, 6, PixelFormats.Rgb24, AlphaFormat.Opaque);

            Assert.Equal(1, backend.Calls);
            Assert.Equal(new byte[] { 30, 20, 10, 60, 50, 40 }, dest);
        }

        [Fact]
        public void Copies_Between_Identical_Formats_Should_Not_Reach_The_Backend()
        {
            using var scope = AvaloniaLocator.EnterScope();

            var backend = new ConvertingBackend(accept: true);

            ImagingBackend.Register(backend);

            var dest = new byte[8];

            Bgra().CopyPixels(new PixelRect(0, 0, 2, 1), dest, 8, PixelFormats.Bgra8888, AlphaFormat.Unpremul);

            Assert.Equal(0, backend.Calls);
            Assert.Equal(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 }, dest);
        }

        [Fact]
        public void Invalid_Arguments_Should_Be_Rejected_Before_The_Backend_Is_Asked()
        {
            using var scope = AvaloniaLocator.EnterScope();

            var backend = new ConvertingBackend(accept: true);

            ImagingBackend.Register(backend);

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                Bgra().CopyPixels(new PixelRect(0, 0, 3, 1), new byte[9], 9, PixelFormats.Rgb24, AlphaFormat.Opaque));

            Assert.Equal(0, backend.Calls);
        }

        private sealed class ConvertingBackend : IImagingBackend, ISupportsPixelConversion
        {
            private readonly bool _accept;

            public ConvertingBackend(bool accept) => _accept = accept;

            public int Calls { get; private set; }

            public PixelRect LastRect { get; private set; }

            public string Name => "Converting";

            public IReadOnlyList<IBitmapCodecInfo> SupportedCodecs => Array.Empty<IBitmapCodecInfo>();

            public int IdentifyPrefixLength => 0;

            public bool TryCopyPixels(ILockedFramebuffer source, BitmapPalette? sourcePalette, PixelRect sourceRect,
                IntPtr destination, long destinationSize, int destinationStride, PixelFormat format,
                AlphaFormat alphaFormat, PixelConversionOptions? options)
            {
                Calls++;
                LastRect = sourceRect;

                if (!_accept)
                    return false;

                for (var i = 0; i < destinationSize; i++)
                    Marshal.WriteByte(destination, i, 0xC0);

                return true;
            }

            public bool TryIdentify(Stream stream, out BitmapImageInfo info) => throw new NotSupportedException();

            public bool TryIdentify(ReadOnlySpan<byte> data, out BitmapImageInfo info) => throw new NotSupportedException();

            public IBitmapDecoder CreateDecoder(Stream stream, bool ownsStream, BitmapDecodeOptions? options = null)
                => throw new NotSupportedException();

            public IBitmapEncoderImpl CreateEncoder(Guid containerFormat) => throw new NotSupportedException();
        }
    }
}
