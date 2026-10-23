using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Imaging.TestKit.Fixtures;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Codecs that declare <see cref="BitmapCodecCapabilities.PreservesStoredFormat"/> report the
    /// stored layout at identify time and decode to it when no target format is requested.
    /// </summary>
    public abstract class StoredFormatContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        /// <summary>The skip reason when the backend keeps no PNG layout as stored.</summary>
        protected const string NotDeclared =
            "The backend's PNG codec does not declare PreservesStoredFormat.";

        // Gray 0x1234, so a narrowed sample cannot pass for the stored one.
        private static readonly byte[] s_gray16 = PngFixtureWriter.WriteRaw(2, 1, 0, 16, 4, (row, _) =>
        {
            row[1] = 0x12; row[2] = 0x34; row[3] = 0xAB; row[4] = 0xCD;
        });

        private static readonly byte[] s_gray8 = PngFixtureWriter.WriteRaw(2, 1, 0, 8, 2, (row, _) =>
        {
            row[1] = 0x40; row[2] = 0xC0;
        });

        private static readonly byte[] s_rgb48 = PngFixtureWriter.WriteRaw(1, 1, 2, 16, 6, (row, _) =>
        {
            for (var i = 1; i <= 6; i++)
                row[i] = (byte)(i * 17);
        });

        private static readonly byte[] s_rgba64 = PngFixtureWriter.WriteRaw(1, 1, 6, 16, 8, (row, _) =>
        {
            for (var i = 1; i <= 8; i++)
                row[i] = (byte)(i * 13);
        });

        private static readonly byte[] s_indexed4 = PngFixtureWriter.WriteRaw(3, 1, 3, 4, 2, (row, _) =>
        {
            row[1] = 0x12; row[2] = 0x30;
        }, palette: new byte[] { 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255 });

        /// <summary>The stored-layout fixtures with every pixel format that holds them without loss.</summary>
        public static IEnumerable<object[]> Fixtures()
        {
            yield return new object[] { "Gray16", s_gray16, new[] { nameof(PixelFormats.Gray16) } };
            yield return new object[] { "Gray8", s_gray8, new[] { nameof(PixelFormats.Gray8) } };
            yield return new object[] { "Rgb48", s_rgb48, new[] { nameof(PixelFormats.Rgb48) } };
            yield return new object[] { "Rgba64", s_rgba64, new[] { nameof(PixelFormats.Rgba64), nameof(PixelFormats.Bgra64) } };
            yield return new object[] { "Indexed4", s_indexed4, new[] { nameof(PixelFormats.Indexed4) } };
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void Identify_ReportsALosslessStoredFormat(string name, byte[] png, string[] lossless)
        {
            RequirePreservingPngCodec();

            Assert.True(Backend.TryIdentify(png, out var info), $"TryIdentify must recognize the {name} PNG.");
            Assert.True(info.PixelFormat is { } format && lossless.Contains(format.ToString()),
                $"{name}: identify reported {info.PixelFormat?.ToString() ?? "no format"}, expected one of {string.Join(", ", lossless)}.");
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void Decode_WithoutTargetFormat_KeepsTheStoredFormat(string name, byte[] png, string[] lossless)
        {
            _ = lossless;

            RequirePreservingPngCodec();

            Assert.True(Backend.TryIdentify(png, out var info));

            using var decoder = Backend.CreateDecoder(new MemoryStream(png), ownsStream: true);
            using var frame = decoder.ReadNextFrame()!;

            Assert.Equal(info.PixelFormat, frame.PixelFormat);

            if (frame.PixelFormat.IsIndexed)
                Assert.True(frame is IIndexedFrame, $"{name}: an indexed frame must expose its palette.");
        }

        [Fact]
        public void Decode_KeepsEverySampleBit()
        {
            RequirePreservingPngCodec();

            using var decoder = Backend.CreateDecoder(new MemoryStream(s_gray16), ownsStream: true);
            using var frame = decoder.ReadNextFrame()!;
            using var view = frame.Lock();

            Assert.Equal(PixelFormats.Gray16, view.Format);
            Assert.Equal((short)0x1234, Marshal.ReadInt16(view.Address));
            Assert.Equal(unchecked((short)0xABCD), Marshal.ReadInt16(view.Address, 2));
        }

        private void RequirePreservingPngCodec()
        {
            var png = Backend.SupportedCodecs.FirstOrDefault(codec => codec.FormatName == FixtureImages.PngFormatName);

            Assert.SkipWhen(png is null || (png.Capabilities & BitmapCodecCapabilities.PreservesStoredFormat) == 0,
                NotDeclared);
        }
    }
}
