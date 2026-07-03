using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Imaging
{
    public class EncoderTransformTests
    {
        private static PixelBuffer CreateBuffer(int width, int height)
        {
            // Bgra8888, one distinct blue value per pixel.
            var pixels = new byte[width * height * 4];

            for (var i = 0; i < width * height; i++)
            {
                pixels[i * 4] = (byte)i;
                pixels[i * 4 + 3] = 255;
            }

            return PixelBuffer.TakeOwnership(pixels, new PixelSize(width, height), width * 4,
                PixelFormat.Bgra8888, AlphaFormat.Opaque, new Vector(96, 96));
        }

        private static byte ValueAt(PixelBuffer buffer, int x, int y) =>
            buffer.Pixels.Span[y * buffer.Stride + x * 4];

        [Fact]
        public void Identity_Returns_The_Same_Instance()
        {
            var buffer = CreateBuffer(3, 2);

            Assert.Same(buffer, EncoderTransform.Apply(buffer, default));
        }

        [Fact]
        public void Cancelling_Combination_Returns_The_Same_Instance()
        {
            var buffer = CreateBuffer(3, 2);
            var transform = new BitmapTransform(BitmapRotation.Rotate180, true, true);

            Assert.Same(buffer, EncoderTransform.Apply(buffer, transform));
        }

        [Fact]
        public void Rotate90_Moves_Pixels_And_Swaps_Dimensions()
        {
            // Source 3x2:  0 1 2     rotated 90 clockwise (2x3):  3 0
            //              3 4 5                                  4 1
            //                                                     5 2
            var buffer = CreateBuffer(3, 2);
            var rotated = EncoderTransform.Apply(buffer, new BitmapTransform(BitmapRotation.Rotate90, false, false));

            Assert.Equal(new PixelSize(2, 3), rotated.Size);
            Assert.Equal(3, ValueAt(rotated, 0, 0));
            Assert.Equal(0, ValueAt(rotated, 1, 0));
            Assert.Equal(4, ValueAt(rotated, 0, 1));
            Assert.Equal(1, ValueAt(rotated, 1, 1));
            Assert.Equal(5, ValueAt(rotated, 0, 2));
            Assert.Equal(2, ValueAt(rotated, 1, 2));
        }

        [Fact]
        public void FlipHorizontal_Mirrors_Rows()
        {
            var buffer = CreateBuffer(3, 2);
            var flipped = EncoderTransform.Apply(buffer, new BitmapTransform(BitmapRotation.None, true, false));

            Assert.Equal(new PixelSize(3, 2), flipped.Size);
            Assert.Equal(2, ValueAt(flipped, 0, 0));
            Assert.Equal(1, ValueAt(flipped, 1, 0));
            Assert.Equal(0, ValueAt(flipped, 2, 0));
            Assert.Equal(5, ValueAt(flipped, 0, 1));
        }

        [Fact]
        public void Rotation_Applies_Before_Flips()
        {
            // Rotate 90 then flip horizontally equals a transpose: out(x, y) = in(y, x).
            var buffer = CreateBuffer(3, 2);
            var transformed = EncoderTransform.Apply(buffer, new BitmapTransform(BitmapRotation.Rotate90, true, false));

            Assert.Equal(new PixelSize(2, 3), transformed.Size);
            Assert.Equal(0, ValueAt(transformed, 0, 0));
            Assert.Equal(3, ValueAt(transformed, 1, 0));
            Assert.Equal(1, ValueAt(transformed, 0, 1));
            Assert.Equal(4, ValueAt(transformed, 1, 1));
            Assert.Equal(2, ValueAt(transformed, 0, 2));
            Assert.Equal(5, ValueAt(transformed, 1, 2));
        }

        [Theory]
        [InlineData(BitmapRotation.Rotate90, false, false, 6)]   // Rotate90
        [InlineData(BitmapRotation.Rotate180, false, false, 3)]  // Rotate180
        [InlineData(BitmapRotation.Rotate270, false, false, 8)]  // Rotate270
        [InlineData(BitmapRotation.None, true, false, 2)]        // FlipHorizontal
        [InlineData(BitmapRotation.None, false, true, 4)]        // FlipVertical
        [InlineData(BitmapRotation.None, true, true, 3)]         // both flips = Rotate180
        [InlineData(BitmapRotation.Rotate90, true, false, 5)]    // Transpose
        [InlineData(BitmapRotation.Rotate90, false, true, 7)]    // Transverse
        [InlineData(BitmapRotation.Rotate180, true, true, 1)]    // cancels out = Normal
        public void FromTransform_Reduces_To_The_Expected_Orientation(
            BitmapRotation rotation, bool flipHorizontal, bool flipVertical, int expectedExifValue)
        {
            var transform = new BitmapTransform(rotation, flipHorizontal, flipVertical);

            Assert.Equal((PixelOrientation)expectedExifValue, PixelOrientations.FromTransform(transform));
        }
    }
}
