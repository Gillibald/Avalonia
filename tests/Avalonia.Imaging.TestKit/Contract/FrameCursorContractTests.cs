using Avalonia.Imaging.TestKit.Fixtures;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Frame cursor semantics of multi-frame containers: forward-only enumeration,
    /// exhaustion, and random access through <see cref="Avalonia.Platform.ISeekableFrameDecoder"/>.
    /// </summary>
    public abstract class FrameCursorContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        /// <summary>Why these tests are skipped for now.</summary>
        protected const string SkipReason =
            "Needs a multi-frame fixture; the generated PNG/BMP fixtures are single-frame. " +
            "Backend test projects supply one together with their multi-frame codec support.";

        [Fact(Skip = SkipReason)]
        public void ForwardOnly_FrameCountUnknownUntilExhausted()
        {
        }

        [Fact(Skip = SkipReason)]
        public void Exhausted_ReadNextFrameStaysNull()
        {
        }

        [Fact(Skip = SkipReason)]
        public void Seekable_GetFrameMatchesCursorOrder()
        {
        }
    }
}
