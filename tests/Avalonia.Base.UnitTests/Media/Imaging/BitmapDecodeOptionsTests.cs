using System.Threading;
using Avalonia.Media.Imaging;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Imaging
{
    public class BitmapDecodeOptionsTests
    {
        [Fact]
        public void WithCancellation_Returns_Caller_Options_When_No_Token_Is_Injected()
        {
            var options = new BitmapDecodeOptions();

            Assert.Same(options, BitmapDecoder.WithCancellation(options, CancellationToken.None));
            Assert.Null(BitmapDecoder.WithCancellation(null, CancellationToken.None));
        }

        [Fact]
        public void WithCancellation_Creates_Options_When_None_Were_Supplied()
        {
            using var cts = new CancellationTokenSource();

            var result = BitmapDecoder.WithCancellation(null, cts.Token);

            Assert.NotNull(result);
            Assert.Equal(cts.Token, result!.CancellationToken);
        }

        [Fact]
        public void WithCancellation_Copies_Instead_Of_Mutating_The_Caller_Options()
        {
            using var cts = new CancellationTokenSource();

            var options = new BitmapDecodeOptions
            {
                TargetSize = new PixelSize(10, 10),
                MaxPixels = 1234,
            };

            var result = BitmapDecoder.WithCancellation(options, cts.Token);

            Assert.NotSame(options, result);
            Assert.Equal(CancellationToken.None, options.CancellationToken);
            Assert.Equal(cts.Token, result!.CancellationToken);
            Assert.Equal(options.TargetSize, result.TargetSize);
            Assert.Equal(options.MaxPixels, result.MaxPixels);
        }

        [Fact]
        public void WithCancellation_Preserves_The_Runtime_Options_Type()
        {
            using var cts = new CancellationTokenSource();

            var options = new JpegDecodeOptions { ScaleDenominator = 4 };

            var jpeg = Assert.IsType<JpegDecodeOptions>(BitmapDecoder.WithCancellation(options, cts.Token));

            Assert.Equal(4, jpeg.ScaleDenominator);
            Assert.Equal(cts.Token, jpeg.CancellationToken);
        }

        [Fact]
        public void WithCancellation_Keeps_Options_That_Already_Carry_A_Token()
        {
            using var existing = new CancellationTokenSource();
            using var injected = new CancellationTokenSource();

            var options = new BitmapDecodeOptions { CancellationToken = existing.Token };

            var result = BitmapDecoder.WithCancellation(options, injected.Token);

            Assert.Same(options, result);
            Assert.Equal(existing.Token, result!.CancellationToken);
        }
    }
}
