using Avalonia.Imaging.TestKit.Fixtures;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Concurrency guarantees: concurrent decoder creation is safe and cooperative
    /// cancellation is observed where declared.
    /// </summary>
    public abstract class ThreadingContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        /// <summary>Why these tests are skipped for now.</summary>
        protected const string SkipReason =
            "Needs fixtures whose decode is slow enough to race and to cancel mid-flight. " +
            "Backend test projects supply them together with their concurrency support.";

        [Fact(Skip = SkipReason)]
        public void ConcurrentDecoderCreation_IsSafe()
        {
        }

        [Fact(Skip = SkipReason)]
        public void Cancellation_StopsCooperativeDecode()
        {
        }
    }
}
