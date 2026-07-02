using Avalonia.Imaging.TestKit.Fixtures;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Pool discipline: decodes stay within the manifest's per-format copy budget and
    /// pooled retention honors the configured ceiling.
    /// </summary>
    public abstract class PoolContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        /// <summary>Why these tests are skipped for now.</summary>
        protected const string SkipReason =
            "Needs the backend's full decode pipeline renting through the fixture allocator " +
            "(install destination plus staging), so the per-format copy budgets become " +
            "observable. Backend test projects activate these together with their allocator wiring.";

        [Fact(Skip = SkipReason)]
        public void Decode_StaysWithinDeclaredCopyBudget()
        {
        }

        [Fact(Skip = SkipReason)]
        public void InstallDestination_IsTheOnlyFrameSizedRent()
        {
        }

        [Fact(Skip = SkipReason)]
        public void PoolRetention_HonorsDecodePoolMaxBytes()
        {
        }
    }
}
