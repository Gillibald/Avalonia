using Avalonia.Imaging.TestKit.Fixtures;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Decode plan semantics: target size, source region, target format and the honesty
    /// of <see cref="Avalonia.Platform.ISupportsFusedDecode"/> reporting.
    /// </summary>
    public abstract class DecodePlanContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        /// <summary>Why these tests are skipped for now.</summary>
        protected const string SkipReason =
            "Needs fixtures large enough to observe scaling and region decodes plus per-backend " +
            "fused-decode references. Backend test projects supply them together with their " +
            "decode plan support.";

        [Fact(Skip = SkipReason)]
        public void TargetSize_ProducesRequestedDescriptor()
        {
        }

        [Fact(Skip = SkipReason)]
        public void SourceRegion_DecodesRequestedPixels()
        {
        }

        [Fact(Skip = SkipReason)]
        public void TargetFormat_ProducesRequestedFormat()
        {
        }

        [Fact(Skip = SkipReason)]
        public void FusedParts_ReportedHonestly()
        {
        }
    }
}
