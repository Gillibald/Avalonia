using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Imaging
{
    public class FusedPixelPipelineTests
    {
        public static TheoryData<PixelFormat, AlphaFormat, PixelFormat, AlphaFormat> ConversionPairs => new()
        {
            // The first pair hits the same-format row copy fast path; Transcode is the
            // slow path it must match byte for byte.
            { PixelFormats.Bgra8888, AlphaFormat.Unpremul, PixelFormats.Bgra8888, AlphaFormat.Unpremul },
            { PixelFormats.Bgra8888, AlphaFormat.Unpremul, PixelFormats.Rgba8888, AlphaFormat.Premul },
            { PixelFormats.Rgba8888, AlphaFormat.Unpremul, PixelFormats.Bgra8888, AlphaFormat.Unpremul },
            { PixelFormats.Bgra8888, AlphaFormat.Opaque, PixelFormats.Rgb565, AlphaFormat.Opaque },
            { PixelFormats.Rgba64, AlphaFormat.Unpremul, PixelFormats.Rgba8888, AlphaFormat.Unpremul },
            { PixelFormats.Gray8, AlphaFormat.Opaque, PixelFormats.Bgra8888, AlphaFormat.Premul }
        };

        [Theory]
        [MemberData(nameof(ConversionPairs))]
        public void Identity_Plan_Matches_Transcode(
            PixelFormat sourceFormat, AlphaFormat sourceAlpha, PixelFormat targetFormat, AlphaFormat targetAlpha)
        {
            var size = new PixelSize(5, 4);

            using var sourceMemory = new BitmapMemory(sourceFormat, sourceAlpha, size);
            using var transcoded = new BitmapMemory(targetFormat, targetAlpha, size);
            using var piped = new BitmapMemory(targetFormat, targetAlpha, size);

            FillDeterministic(sourceMemory);

            PixelFormatTranscoder.Transcode(
                sourceMemory.Address,
                size,
                sourceMemory.RowBytes,
                sourceMemory.Format,
                sourceAlpha,
                transcoded.Address,
                transcoded.RowBytes,
                transcoded.Format,
                targetAlpha);

            using var framebuffer = Lock(sourceMemory);

            var plan = new FusedPlanExecution(
                new PixelRect(0, 0, size.Width, size.Height),
                size,
                piped.Format,
                targetAlpha,
                BitmapInterpolationMode.HighQuality);

            FusedPixelPipeline.Run(framebuffer, plan, piped.Address, piped.RowBytes,
                cancellation: TestContext.Current.CancellationToken);

            var usableRowBytes = size.Width * ((piped.Format.BitsPerPixel + 7) / 8);

            for (var y = 0; y < size.Height; y++)
            {
                Assert.Equal(
                    GetBytes(transcoded.Address + y * transcoded.RowBytes, usableRowBytes),
                    GetBytes(piped.Address + y * piped.RowBytes, usableRowBytes));
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Crop_Copies_The_Sub_Rect(bool sameFormat)
        {
            // The same-format case takes the row copy fast path; the converting case
            // decodes the sub-rect through the canonical format.
            using var sourceMemory = new BitmapMemory(PixelFormat.Bgra8888, AlphaFormat.Unpremul, new PixelSize(4, 4));
            using var destMemory = new BitmapMemory(
                sameFormat ? PixelFormat.Bgra8888 : PixelFormat.Rgba8888, AlphaFormat.Unpremul, new PixelSize(2, 2));

            for (var y = 0; y < 4; y++)
            {
                SetRow(sourceMemory, y,
                    Pixel(y, 0), Pixel(y, 1), Pixel(y, 2), Pixel(y, 3));
            }

            using var framebuffer = Lock(sourceMemory);

            var plan = new FusedPlanExecution(
                new PixelRect(1, 1, 2, 2),
                new PixelSize(2, 2),
                destMemory.Format,
                destMemory.AlphaFormat,
                BitmapInterpolationMode.HighQuality);

            FusedPixelPipeline.Run(framebuffer, plan, destMemory.Address, destMemory.RowBytes,
                cancellation: TestContext.Current.CancellationToken);

            Assert.Equal(new[] { Pixel(1, 1), Pixel(1, 2) }, GetRow(destMemory, 0));
            Assert.Equal(new[] { Pixel(2, 1), Pixel(2, 2) }, GetRow(destMemory, 1));

            static Rgba8888Pixel Pixel(int y, int x) => new((byte)(10 * x), (byte)(10 * y), 200, 255);
        }

        [Fact]
        public void Nearest_Upscale_Duplicates_Pixels()
        {
            using var sourceMemory = new BitmapMemory(PixelFormat.Bgra8888, AlphaFormat.Premul, new PixelSize(2, 2));
            using var destMemory = new BitmapMemory(PixelFormat.Bgra8888, AlphaFormat.Premul, new PixelSize(4, 4));

            var topLeft = new Rgba8888Pixel(10, 20, 30, 255);
            var topRight = new Rgba8888Pixel(40, 50, 60, 255);
            var bottomLeft = new Rgba8888Pixel(70, 80, 90, 255);
            var bottomRight = new Rgba8888Pixel(100, 110, 120, 255);

            SetRow(sourceMemory, 0, topLeft, topRight);
            SetRow(sourceMemory, 1, bottomLeft, bottomRight);

            using var framebuffer = Lock(sourceMemory);

            var plan = new FusedPlanExecution(
                new PixelRect(0, 0, 2, 2),
                new PixelSize(4, 4),
                destMemory.Format,
                destMemory.AlphaFormat,
                BitmapInterpolationMode.None);

            FusedPixelPipeline.Run(framebuffer, plan, destMemory.Address, destMemory.RowBytes,
                cancellation: TestContext.Current.CancellationToken);

            // Center-aligned nearest sampling duplicates each source pixel into a 2x2 block.
            Assert.Equal(new[] { topLeft, topLeft, topRight, topRight }, GetRow(destMemory, 0));
            Assert.Equal(new[] { topLeft, topLeft, topRight, topRight }, GetRow(destMemory, 1));
            Assert.Equal(new[] { bottomLeft, bottomLeft, bottomRight, bottomRight }, GetRow(destMemory, 2));
            Assert.Equal(new[] { bottomLeft, bottomLeft, bottomRight, bottomRight }, GetRow(destMemory, 3));
        }

        [Fact]
        public void Bilinear_Upscale_Interpolates()
        {
            using var sourceMemory = new BitmapMemory(PixelFormat.Bgra8888, AlphaFormat.Premul, new PixelSize(2, 2));
            using var destMemory = new BitmapMemory(PixelFormat.Bgra8888, AlphaFormat.Premul, new PixelSize(4, 4));

            var values = new byte[,] { { 0, 80 }, { 160, 240 } };

            SetRow(sourceMemory, 0, Gray(values[0, 0]), Gray(values[0, 1]));
            SetRow(sourceMemory, 1, Gray(values[1, 0]), Gray(values[1, 1]));

            using var framebuffer = Lock(sourceMemory);

            var plan = new FusedPlanExecution(
                new PixelRect(0, 0, 2, 2),
                new PixelSize(4, 4),
                destMemory.Format,
                destMemory.AlphaFormat,
                BitmapInterpolationMode.HighQuality);

            FusedPixelPipeline.Run(framebuffer, plan, destMemory.Address, destMemory.RowBytes,
                cancellation: TestContext.Current.CancellationToken);

            for (var y = 0; y < 4; y++)
            {
                var row = GetRow(destMemory, y);

                for (var x = 0; x < 4; x++)
                {
                    var expected = SampleBilinear(values, x, y);

                    Assert.True(Math.Abs(row[x].R - expected) <= 1,
                        $"({x},{y}): expected {expected} +- 1 but got {row[x].R}");
                    Assert.Equal(255, row[x].A);
                }
            }

            static Rgba8888Pixel Gray(byte value) => new(value, value, value, 255);

            // Center-aligned mapping: position = (index + 0.5) * source / target - 0.5,
            // clamped to the source range.
            static double SampleBilinear(byte[,] values, int x, int y)
            {
                var px = Math.Clamp((x + 0.5) * 2 / 4 - 0.5, 0, 1);
                var py = Math.Clamp((y + 0.5) * 2 / 4 - 0.5, 0, 1);
                var x0 = (int)px;
                var y0 = (int)py;
                var x1 = Math.Min(x0 + 1, 1);
                var y1 = Math.Min(y0 + 1, 1);
                var fx = px - x0;
                var fy = py - y0;

                var top = values[y0, x0] + (values[y0, x1] - values[y0, x0]) * fx;
                var bottom = values[y1, x0] + (values[y1, x1] - values[y1, x0]) * fx;

                return top + (bottom - top) * fy;
            }
        }

        [Fact]
        public void Box_Downscale_Averages_Quadrants()
        {
            using var sourceMemory = new BitmapMemory(PixelFormat.Bgra8888, AlphaFormat.Premul, new PixelSize(4, 4));
            using var destMemory = new BitmapMemory(PixelFormat.Bgra8888, AlphaFormat.Premul, new PixelSize(2, 2));

            var quadrants = new[]
            {
                new Rgba8888Pixel(10, 20, 30, 255),
                new Rgba8888Pixel(50, 60, 70, 255),
                new Rgba8888Pixel(90, 100, 110, 255),
                new Rgba8888Pixel(130, 140, 150, 255)
            };

            for (var y = 0; y < 4; y++)
            {
                var left = quadrants[y < 2 ? 0 : 2];
                var right = quadrants[y < 2 ? 1 : 3];

                SetRow(sourceMemory, y, left, left, right, right);
            }

            using var framebuffer = Lock(sourceMemory);

            var plan = new FusedPlanExecution(
                new PixelRect(0, 0, 4, 4),
                new PixelSize(2, 2),
                destMemory.Format,
                destMemory.AlphaFormat,
                BitmapInterpolationMode.HighQuality);

            FusedPixelPipeline.Run(framebuffer, plan, destMemory.Address, destMemory.RowBytes,
                cancellation: TestContext.Current.CancellationToken);

            // Each destination pixel covers exactly one flat quadrant, so the box average
            // reproduces the quadrant color exactly.
            Assert.Equal(new[] { quadrants[0], quadrants[1] }, GetRow(destMemory, 0));
            Assert.Equal(new[] { quadrants[2], quadrants[3] }, GetRow(destMemory, 1));
        }

        [Fact]
        public void Box_Downscale_Weights_Partial_Coverage()
        {
            using var sourceMemory = new BitmapMemory(PixelFormat.Bgra8888, AlphaFormat.Premul, new PixelSize(3, 3));
            using var destMemory = new BitmapMemory(PixelFormat.Bgra8888, AlphaFormat.Premul, new PixelSize(2, 2));

            // Gray levels 10..90:
            //   10 20 30
            //   40 50 60
            //   70 80 90
            for (var y = 0; y < 3; y++)
            {
                SetRow(sourceMemory, y, Gray(10 + 30 * y), Gray(20 + 30 * y), Gray(30 + 30 * y));
            }

            using var framebuffer = Lock(sourceMemory);

            var plan = new FusedPlanExecution(
                new PixelRect(0, 0, 3, 3),
                new PixelSize(2, 2),
                destMemory.Format,
                destMemory.AlphaFormat,
                BitmapInterpolationMode.HighQuality);

            FusedPixelPipeline.Run(framebuffer, plan, destMemory.Address, destMemory.RowBytes,
                cancellation: TestContext.Current.CancellationToken);

            // Scale factor 1.5: destination cell (0,0) covers columns {0 at 1, 1 at 0.5}
            // and rows {0 at 1, 1 at 0.5}, total weight 2.25:
            //   (0,0): (1*(10 + 20*0.5) + 0.5*(40 + 50*0.5)) / 2.25 = 52.5  / 2.25 = 23.33 -> 23
            //   (1,0): (1*(20*0.5 + 30) + 0.5*(50*0.5 + 60)) / 2.25 = 82.5  / 2.25 = 36.67 -> 37
            //   (0,1): (0.5*(40 + 50*0.5) + 1*(70 + 80*0.5)) / 2.25 = 142.5 / 2.25 = 63.33 -> 63
            //   (1,1): (0.5*(50*0.5 + 60) + 1*(80*0.5 + 90)) / 2.25 = 172.5 / 2.25 = 76.67 -> 77
            Assert.Equal(new[] { Gray(23), Gray(37) }, GetRow(destMemory, 0));
            Assert.Equal(new[] { Gray(63), Gray(77) }, GetRow(destMemory, 1));

            static Rgba8888Pixel Gray(int value) => new((byte)value, (byte)value, (byte)value, 255);
        }

        [Fact]
        public void Rgb565_Packing_Is_Exact()
        {
            using var sourceMemory = new BitmapMemory(PixelFormat.Bgra8888, AlphaFormat.Premul, new PixelSize(4, 1));
            using var destMemory = new BitmapMemory(PixelFormat.Rgb565, AlphaFormat.Opaque, new PixelSize(4, 1));

            SetRow(sourceMemory, 0,
                new Rgba8888Pixel(255, 0, 0, 255),
                new Rgba8888Pixel(0, 255, 0, 255),
                new Rgba8888Pixel(0, 0, 255, 255),
                new Rgba8888Pixel(128, 128, 128, 255));

            using var framebuffer = Lock(sourceMemory);

            var plan = new FusedPlanExecution(
                new PixelRect(0, 0, 4, 1),
                new PixelSize(4, 1),
                destMemory.Format,
                destMemory.AlphaFormat,
                BitmapInterpolationMode.HighQuality);

            FusedPixelPipeline.Run(framebuffer, plan, destMemory.Address, destMemory.RowBytes,
                cancellation: TestContext.Current.CancellationToken);

            Assert.Equal(0xF800, ReadUInt16(destMemory.Address, 0));
            Assert.Equal(0x07E0, ReadUInt16(destMemory.Address, 1));
            Assert.Equal(0x001F, ReadUInt16(destMemory.Address, 2));
            Assert.Equal(0x8410, ReadUInt16(destMemory.Address, 3));

            static int ReadUInt16(IntPtr address, int index) =>
                (ushort)Marshal.ReadInt16(address, index * sizeof(ushort));
        }

        [Fact]
        public void Box_Downscale_Does_Not_Bleed_Transparent_Color()
        {
            using var sourceMemory = new BitmapMemory(PixelFormat.Bgra8888, AlphaFormat.Unpremul, new PixelSize(4, 4));
            using var destMemory = new BitmapMemory(PixelFormat.Bgra8888, AlphaFormat.Premul, new PixelSize(2, 2));

            // Unassociated alpha: the transparent right half carries a saturated red that
            // must not survive the average because filtering happens premultiplied.
            var opaqueGreen = new Rgba8888Pixel(0, 255, 0, 255);
            var transparentRed = new Rgba8888Pixel(255, 0, 0, 0);

            for (var y = 0; y < 4; y++)
            {
                SetRow(sourceMemory, y, opaqueGreen, opaqueGreen, transparentRed, transparentRed);
            }

            using var framebuffer = Lock(sourceMemory);

            var plan = new FusedPlanExecution(
                new PixelRect(0, 0, 4, 4),
                new PixelSize(2, 2),
                destMemory.Format,
                destMemory.AlphaFormat,
                BitmapInterpolationMode.HighQuality);

            FusedPixelPipeline.Run(framebuffer, plan, destMemory.Address, destMemory.RowBytes,
                cancellation: TestContext.Current.CancellationToken);

            for (var y = 0; y < 2; y++)
            {
                var row = GetRow(destMemory, y);

                Assert.Equal(opaqueGreen, row[0]);
                Assert.Equal(new Rgba8888Pixel(0, 0, 0, 0), row[1]);
            }
        }

        [Fact]
        public void Downscale_Uses_Bounded_Staging()
        {
            var sourceSize = new PixelSize(64, 64);

            using var sourceMemory = new BitmapMemory(PixelFormat.Bgra8888, AlphaFormat.Premul, sourceSize);
            using var destMemory = new BitmapMemory(PixelFormat.Bgra8888, AlphaFormat.Premul, new PixelSize(8, 8));

            FillDeterministic(sourceMemory);

            using var framebuffer = Lock(sourceMemory);

            var plan = new FusedPlanExecution(
                new PixelRect(0, 0, 64, 64),
                new PixelSize(8, 8),
                destMemory.Format,
                destMemory.AlphaFormat,
                BitmapInterpolationMode.HighQuality);

            var allocator = new CountingAllocator();

            FusedPixelPipeline.Run(framebuffer, plan, destMemory.Address, destMemory.RowBytes, allocator,
                TestContext.Current.CancellationToken);

            var sourceRowBytes = sourceSize.Width * 4;

            Assert.NotEmpty(allocator.Rentals);
            Assert.All(allocator.Rentals, byteCount => Assert.True(byteCount <= 4 * sourceRowBytes,
                $"Rental of {byteCount} bytes exceeds a few source rows ({4 * sourceRowBytes} bytes)."));
            Assert.Equal(0, allocator.Outstanding);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        public void Orientation_Produces_The_Expected_Layout(int orientationValue)
        {
            using var sourceMemory = CreateOrientationSource(out var pixels);

            var expected = GetExpectedGrid(orientationValue, pixels);
            var destSize = new PixelSize(expected[0].Length, expected.Length);

            using var destMemory = new BitmapMemory(PixelFormat.Bgra8888, AlphaFormat.Premul, destSize);
            using var framebuffer = Lock(sourceMemory);

            var plan = new FusedPlanExecution(
                new PixelRect(0, 0, destSize.Width, destSize.Height),
                destSize,
                destMemory.Format,
                destMemory.AlphaFormat,
                BitmapInterpolationMode.HighQuality,
                (PixelOrientation)orientationValue);

            FusedPixelPipeline.Run(framebuffer, plan, destMemory.Address, destMemory.RowBytes,
                cancellation: TestContext.Current.CancellationToken);

            for (var y = 0; y < destSize.Height; y++)
            {
                Assert.Equal(expected[y], GetRow(destMemory, y));
            }
        }

        [Fact]
        public void Orientation_Composes_With_Crop()
        {
            using var sourceMemory = CreateOrientationSource(out var p);
            using var destMemory = new BitmapMemory(PixelFormat.Bgra8888, AlphaFormat.Premul, new PixelSize(2, 2));
            using var framebuffer = Lock(sourceMemory);

            // Rotate90 orients the source to D A / E B / F C; the oriented-space region
            // selects the bottom two oriented rows.
            var plan = new FusedPlanExecution(
                new PixelRect(0, 1, 2, 2),
                new PixelSize(2, 2),
                destMemory.Format,
                destMemory.AlphaFormat,
                BitmapInterpolationMode.HighQuality,
                PixelOrientation.Rotate90);

            FusedPixelPipeline.Run(framebuffer, plan, destMemory.Address, destMemory.RowBytes,
                cancellation: TestContext.Current.CancellationToken);

            Assert.Equal(new[] { p[4], p[1] }, GetRow(destMemory, 0));
            Assert.Equal(new[] { p[5], p[2] }, GetRow(destMemory, 1));
        }

        [Fact]
        public void Orientation_Composes_With_Scale()
        {
            using var sourceMemory = CreateOrientationSource(out var p);
            using var destMemory = new BitmapMemory(PixelFormat.Bgra8888, AlphaFormat.Premul, new PixelSize(4, 6));
            using var framebuffer = Lock(sourceMemory);

            // Rotate90 orients the source to 2x3 (D A / E B / F C); the nearest 2x
            // upscale then duplicates every oriented pixel into a 2x2 block.
            var plan = new FusedPlanExecution(
                new PixelRect(0, 0, 2, 3),
                new PixelSize(4, 6),
                destMemory.Format,
                destMemory.AlphaFormat,
                BitmapInterpolationMode.None,
                PixelOrientation.Rotate90);

            FusedPixelPipeline.Run(framebuffer, plan, destMemory.Address, destMemory.RowBytes,
                cancellation: TestContext.Current.CancellationToken);

            var oriented = new[] { new[] { p[3], p[0] }, new[] { p[4], p[1] }, new[] { p[5], p[2] } };

            for (var y = 0; y < 6; y++)
            {
                var source = oriented[y / 2];

                Assert.Equal(new[] { source[0], source[0], source[1], source[1] }, GetRow(destMemory, y));
            }
        }

        [Fact]
        public void Rotate90_Swaps_The_Source_Bounds()
        {
            using var sourceMemory = CreateOrientationSource(out _);
            using var destMemory = new BitmapMemory(PixelFormat.Bgra8888, AlphaFormat.Premul, new PixelSize(3, 2));
            using var framebuffer = Lock(sourceMemory);

            // The oriented bounds of the rotated 3x2 source are 2x3, so the raw-bounds
            // region no longer fits.
            var rawBoundsPlan = new FusedPlanExecution(
                new PixelRect(0, 0, 3, 2),
                new PixelSize(3, 2),
                destMemory.Format,
                destMemory.AlphaFormat,
                BitmapInterpolationMode.HighQuality,
                PixelOrientation.Rotate90);

            Assert.Throws<ArgumentException>(() =>
                FusedPixelPipeline.Run(framebuffer, rawBoundsPlan, destMemory.Address, destMemory.RowBytes,
                    cancellation: TestContext.Current.CancellationToken));
        }

        [Fact]
        public void Cancellation_Before_Run_Throws_Without_Renting()
        {
            using var sourceMemory = new BitmapMemory(PixelFormat.Bgra8888, AlphaFormat.Premul, new PixelSize(4, 4));
            using var destMemory = new BitmapMemory(PixelFormat.Rgba8888, AlphaFormat.Premul, new PixelSize(4, 4));
            using var framebuffer = Lock(sourceMemory);
            using var cancellation = new CancellationTokenSource();

            var allocator = new CountingAllocator();

            var plan = new FusedPlanExecution(
                new PixelRect(0, 0, 4, 4),
                new PixelSize(4, 4),
                destMemory.Format,
                destMemory.AlphaFormat,
                BitmapInterpolationMode.HighQuality);

            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                FusedPixelPipeline.Run(framebuffer, plan, destMemory.Address, destMemory.RowBytes, allocator, cancellation.Token));

            Assert.Empty(allocator.Rentals);
        }

        [Fact]
        public void Cancellation_During_Run_Returns_All_Rentals()
        {
            using var sourceMemory = new BitmapMemory(PixelFormat.Bgra8888, AlphaFormat.Premul, new PixelSize(64, 64));
            using var destMemory = new BitmapMemory(PixelFormat.Bgra8888, AlphaFormat.Premul, new PixelSize(8, 8));

            FillDeterministic(sourceMemory);

            var destByteCount = destMemory.RowBytes * destMemory.Size.Height;

            Marshal.Copy(new byte[destByteCount], 0, destMemory.Address, destByteCount);

            using var framebuffer = Lock(sourceMemory);
            using var cancellation = new CancellationTokenSource();

            // The staging rentals happen before the first row, so cancelling from the
            // allocator makes the first between-rows check throw before any output.
            var allocator = new CountingAllocator { RentCallback = cancellation.Cancel };

            var plan = new FusedPlanExecution(
                new PixelRect(0, 0, 64, 64),
                new PixelSize(8, 8),
                destMemory.Format,
                destMemory.AlphaFormat,
                BitmapInterpolationMode.HighQuality);

            Assert.Throws<OperationCanceledException>(() =>
                FusedPixelPipeline.Run(framebuffer, plan, destMemory.Address, destMemory.RowBytes, allocator, cancellation.Token));

            Assert.NotEmpty(allocator.Rentals);
            Assert.Equal(0, allocator.Outstanding);
            Assert.All(GetBytes(destMemory.Address, destByteCount), value => Assert.Equal(0, value));
        }

        // Raw 3x2 source with six distinct pixels:
        //   A B C
        //   D E F
        private static BitmapMemory CreateOrientationSource(out Rgba8888Pixel[] pixels)
        {
            pixels = new Rgba8888Pixel[6];

            for (var i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Rgba8888Pixel((byte)(10 + 40 * i), (byte)(15 + 30 * i), (byte)(20 + 20 * i), 255);
            }

            var memory = new BitmapMemory(PixelFormat.Bgra8888, AlphaFormat.Premul, new PixelSize(3, 2));

            SetRow(memory, 0, pixels[0], pixels[1], pixels[2]);
            SetRow(memory, 1, pixels[3], pixels[4], pixels[5]);

            return memory;
        }

        // Expected oriented layouts of the raw 3x2 source A B C / D E F, where the
        // orientation names the transform applied to the raw pixels to display them
        // upright (EXIF numbering):
        //   1 Normal          A B C / D E F
        //   2 FlipHorizontal  C B A / F E D
        //   3 Rotate180       F E D / C B A
        //   4 FlipVertical    D E F / A B C
        //   5 Transpose       A D / B E / C F
        //   6 Rotate90        D A / E B / F C
        //   7 Transverse      F C / E B / D A
        //   8 Rotate270       C F / B E / A D
        private static Rgba8888Pixel[][] GetExpectedGrid(int orientation, Rgba8888Pixel[] p)
        {
            return orientation switch
            {
                1 => new[] { new[] { p[0], p[1], p[2] }, new[] { p[3], p[4], p[5] } },
                2 => new[] { new[] { p[2], p[1], p[0] }, new[] { p[5], p[4], p[3] } },
                3 => new[] { new[] { p[5], p[4], p[3] }, new[] { p[2], p[1], p[0] } },
                4 => new[] { new[] { p[3], p[4], p[5] }, new[] { p[0], p[1], p[2] } },
                5 => new[] { new[] { p[0], p[3] }, new[] { p[1], p[4] }, new[] { p[2], p[5] } },
                6 => new[] { new[] { p[3], p[0] }, new[] { p[4], p[1] }, new[] { p[5], p[2] } },
                7 => new[] { new[] { p[5], p[2] }, new[] { p[4], p[1] }, new[] { p[3], p[0] } },
                8 => new[] { new[] { p[2], p[5] }, new[] { p[1], p[4] }, new[] { p[0], p[3] } },
                _ => throw new ArgumentOutOfRangeException(nameof(orientation))
            };
        }

        private static LockedFramebuffer Lock(BitmapMemory memory) =>
            new(memory.Address, memory.Size, memory.RowBytes, new Vector(96, 96), memory.Format, memory.AlphaFormat, null);

        private static void SetRow(BitmapMemory memory, int y, params Rgba8888Pixel[] pixels) =>
            PixelFormatTranscoder.WriteRow(pixels, memory.Address + y * memory.RowBytes, memory.Format, memory.AlphaFormat, memory.AlphaFormat);

        private static Rgba8888Pixel[] GetRow(BitmapMemory memory, int y)
        {
            var row = new Rgba8888Pixel[memory.Size.Width];

            PixelFormatTranscoder.ReadRow(memory.Address + y * memory.RowBytes, memory.Format, row);

            return row;
        }

        private static void FillDeterministic(BitmapMemory memory)
        {
            var byteCount = memory.RowBytes * memory.Size.Height;
            var bytes = new byte[byteCount];

            for (var i = 0; i < byteCount; i++)
            {
                bytes[i] = (byte)(i * 31 + 7);
            }

            Marshal.Copy(bytes, 0, memory.Address, byteCount);
        }

        private static byte[] GetBytes(IntPtr address, int count)
        {
            var bytes = new byte[count];

            Marshal.Copy(address, bytes, 0, count);

            return bytes;
        }

        private sealed class CountingAllocator : IBitmapMemoryAllocator
        {
            public List<long> Rentals { get; } = new();

            public int Outstanding { get; private set; }

            public Action? RentCallback { get; init; }

            public IBitmapMemory Rent(long byteCount)
            {
                Rentals.Add(byteCount);
                Outstanding++;

                RentCallback?.Invoke();

                return new CountedMemory(this, byteCount);
            }

            private sealed class CountedMemory : IBitmapMemory
            {
                private readonly CountingAllocator _owner;
                private IntPtr _address;

                public CountedMemory(CountingAllocator owner, long byteCount)
                {
                    _owner = owner;
                    _address = Marshal.AllocHGlobal((IntPtr)byteCount);
                    ByteCount = byteCount;
                }

                public IntPtr Address => _address;

                public long ByteCount { get; }

                public void Dispose()
                {
                    if (_address != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(_address);
                        _address = IntPtr.Zero;
                        _owner.Outstanding--;
                    }
                }
            }
        }
    }
}
