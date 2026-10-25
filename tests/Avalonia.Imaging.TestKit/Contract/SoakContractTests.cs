using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Imaging.TestKit.Fixtures;
using Avalonia.Imaging.TestKit.Instrumentation;
using Avalonia.Media.Imaging;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Decodes, installs and releases the same image many times and checks that nothing
    /// accumulates: the pool ends balanced and the process's private memory stays flat. A
    /// backend that keeps a little native or managed memory per decode passes the lifetime
    /// tests and fails here.
    /// </summary>
    public abstract class SoakContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        private const int WarmUpIterations = 1_000;
        private const int Iterations = 10_000;

        // 9,000 measured iterations: a leak of about 0.5 KB per iteration exceeds this.
        private const long MaxGrowthBytes = 4L * 1024 * 1024;

        private const int Size = 256;

        // Opaque RGB with a gradient, so the PNG has real filtered rows to decode.
        private static readonly byte[] s_png = PngFixtureWriter.WriteRaw(Size, Size, 2, 8, Size * 3, (row, y) =>
        {
            for (var x = 0; x < Size; x++)
            {
                row[1 + x * 3] = (byte)x;
                row[2 + x * 3] = (byte)y;
                row[3 + x * 3] = (byte)(x ^ y);
            }
        });

        [Fact]
        [Trait("Category", "Soak")]
        public void RepeatedDecodeInstallAndRelease_KeepsPoolBalancedAndMemoryFlat()
        {
            if (TryGetFormat("PNG") is not { Decode: true })
            {
                Assert.Skip("The manifest does not declare PNG decoding.");
                return;
            }

            using var scope = LocatorScope.With(Backend, new FakeRenderInterface(Fixture.RenderInstall));

            for (var i = 0; i < WarmUpIterations; i++)
                RunOnce();

            var before = PrivateBytes();

            for (var i = WarmUpIterations; i < Iterations; i++)
                RunOnce();

            var after = PrivateBytes();

            Allocator.AssertBalanced();
            Assert.True(after - before <= MaxGrowthBytes,
                $"Private memory grew by {(after - before) / 1024} KB over {Iterations - WarmUpIterations} " +
                $"decode, install and release iterations (limit {MaxGrowthBytes / 1024} KB).");
        }

        private static void RunOnce()
        {
            using (var decoder = BitmapDecoder.Create(new MemoryStream(s_png, writable: false)))
            using (var frame = decoder.ReadNextFrame()!)
            using (frame.Lock())
            {
            }

            using (Bitmap.Decode(new MemoryStream(s_png, writable: false)))
            {
            }
        }

        // Collected twice so finalizers that release native memory have run.
        private static long PrivateBytes()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            using var process = Process.GetCurrentProcess();

            return process.PrivateMemorySize64;
        }
    }
}
