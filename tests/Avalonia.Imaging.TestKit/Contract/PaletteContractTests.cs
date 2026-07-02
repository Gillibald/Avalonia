using Avalonia.Imaging.TestKit.Fixtures;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Indexed pixel access and palette exposure through
    /// <see cref="Avalonia.Platform.IIndexedFrame"/>.
    /// </summary>
    public abstract class PaletteContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        /// <summary>Why these tests are skipped for now.</summary>
        protected const string SkipReason =
            "Needs indexed fixtures (paletted PNG, GIF or 8-bit BMP); the generated fixtures " +
            "are direct-color. Backend test projects supply them together with their palette " +
            "codec support.";

        [Fact(Skip = SkipReason)]
        public void IndexedFrames_ExposePalette()
        {
        }

        [Fact(Skip = SkipReason)]
        public void LockIndexed_ReturnsRawIndices()
        {
        }

        [Fact(Skip = SkipReason)]
        public void PaletteColors_MatchReference()
        {
        }
    }
}
