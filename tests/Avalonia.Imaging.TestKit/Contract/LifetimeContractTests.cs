using Avalonia.Imaging.TestKit.Fixtures;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Lifetime rules: locked views stay valid until disposed, frames release their
    /// memory once every view closed, and dispose order is forgiving.
    /// </summary>
    public abstract class LifetimeContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        /// <summary>Why these tests are skipped for now.</summary>
        protected const string SkipReason =
            "Needs the render-install seam exercised against a real backend allocator wiring " +
            "(FakeRenderInstall plus the backend's frame memory ownership). Backend test " +
            "projects activate these together with their frame lifetime wiring.";

        [Fact(Skip = SkipReason)]
        public void LockedView_RemainsValidUntilDisposed()
        {
        }

        [Fact(Skip = SkipReason)]
        public void FrameDispose_ReleasesMemoryOnceViewsClose()
        {
        }

        [Fact(Skip = SkipReason)]
        public void DecoderDispose_BeforeFrameDisposeIsSafe()
        {
        }
    }
}
