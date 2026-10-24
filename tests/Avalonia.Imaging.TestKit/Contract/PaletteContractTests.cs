using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Imaging.TestKit.Fixtures;
using Avalonia.Media;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Indexed pixel access and palette exposure through <see cref="IIndexedFrame"/>, for
    /// backends whose manifest declares PNG palettes.
    /// </summary>
    public abstract class PaletteContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        /// <summary>The skip reason when the manifest declares no PNG palettes.</summary>
        protected const string NotDeclared = "The manifest does not declare PNG palettes.";

        // A 3x1 4-bit palette PNG with the indices 1, 2, 3 over black, red, green and blue.
        private static readonly byte[] s_indexed4 = PngFixtureWriter.WriteRaw(3, 1, 3, 4, 2, (row, _) =>
        {
            row[1] = 0x12;
            row[2] = 0x30;
        }, palette: new byte[] { 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255 });

        [Fact]
        public void IndexedFrames_ExposePalette()
        {
            using var frame = DecodeIndexed();

            var indexed = Assert.IsAssignableFrom<IIndexedFrame>(frame);

            Assert.True(frame.PixelFormat.IsIndexed, $"An indexed PNG decoded to {frame.PixelFormat}.");
            Assert.True(indexed.Palette.Colors.Count >= 4, "The palette must hold the file's four entries.");
        }

        [Fact]
        public void LockIndexed_ReturnsRawIndices()
        {
            using var frame = DecodeIndexed();
            using var view = Assert.IsAssignableFrom<IIndexedFrame>(frame).LockIndexed();

            Assert.Equal(PixelFormats.Indexed4, view.Format);
            Assert.Equal(0x12, Marshal.ReadByte(view.Address));
            Assert.Equal(0x30, Marshal.ReadByte(view.Address, 1) & 0xF0);
        }

        [Fact]
        public void PaletteColors_MatchReference()
        {
            using var frame = DecodeIndexed();

            var colors = Assert.IsAssignableFrom<IIndexedFrame>(frame).Palette.Colors;

            Assert.Equal(Color.FromRgb(0, 0, 0), colors[0]);
            Assert.Equal(Color.FromRgb(255, 0, 0), colors[1]);
            Assert.Equal(Color.FromRgb(0, 255, 0), colors[2]);
            Assert.Equal(Color.FromRgb(0, 0, 255), colors[3]);
        }

        private IBitmapFrameSource DecodeIndexed()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true, Palette: true }, NotDeclared);

            var decoder = Backend.CreateDecoder(new MemoryStream(s_indexed4), ownsStream: true);

            try
            {
                return decoder.ReadNextFrame()!;
            }
            finally
            {
                decoder.Dispose();
            }
        }
    }
}
