using System.Threading.Tasks;
using Avalonia.Imaging.TestKit.Fixtures;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Concurrency guarantees: decoder creation and independent full decodes are safe
    /// in parallel and leave the shared pool balanced.
    /// </summary>
    public abstract class ThreadingContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        private const int WorkerCount = 8;

        [Fact]
        public void ParallelIndependentDecodes_NoPoolCorruption()
        {
            var fixture = RequireAnyDecodableReferenceFixture();

            // Materialize the backend before the workers race for it.
            _ = Backend;

            var tasks = new Task[WorkerCount];

            for (var i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    using var decoder = CreateDecoder(fixture);
                    using var frame = ReadFirstFrame(decoder);

                    AssertFrameMatchesReference(fixture, frame);
                });
            }

            Task.WaitAll(tasks);
            Allocator.AssertBalanced();
        }

        [Fact]
        public void BackendConcurrentCreate_Safe()
        {
            var fixture = RequireAnyDecodableReferenceFixture();
            var backend = Backend;
            var decoders = new IBitmapDecoder[WorkerCount];
            var tasks = new Task[WorkerCount];

            for (var i = 0; i < tasks.Length; i++)
            {
                var slot = i;

                tasks[i] = Task.Run(() =>
                    decoders[slot] = backend.CreateDecoder(fixture.OpenRead(), ownsStream: true));
            }

            Task.WaitAll(tasks);

            foreach (var decoder in decoders)
            {
                using (decoder)
                using (var frame = ReadFirstFrame(decoder))
                {
                    AssertFrameMatchesReference(fixture, frame);
                }
            }

            Allocator.AssertBalanced();
        }

        [Fact(Skip = "Needs a fixture whose decode is slow enough to cancel mid-flight; " +
                     "no backend declares cooperative cancellation yet.")]
        public void Cancellation_StopsCooperativeDecode()
        {
        }
    }
}
