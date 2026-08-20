using Avalonia.Media;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts
{
    /// <summary>
    /// Reusable contract suite for <see cref="ISystemFontProvider"/> implementations. Platform
    /// bindings instantiate it behind an <see cref="IsSupported"/> gate (the binding test projects
    /// link this file); the fake provider instantiation keeps the suite itself covered.
    /// </summary>
    public abstract class SystemFontProviderContractTests
    {
        protected abstract ISystemFontProvider CreateProvider();

        /// <summary>A family name the provider is expected to know.</summary>
        protected abstract string KnownFamilyName { get; }

        /// <summary>A codepoint the provider is expected to place.</summary>
        protected virtual int KnownCodepoint => 'A';

        /// <summary>Whether the provider's platform library is available in this environment.</summary>
        protected virtual bool IsSupported => true;

        [Fact]
        public void Should_Enumerate_At_Least_One_Family()
        {
            Assert.SkipUnless(IsSupported, "The provider is not supported on this platform.");

            using var provider = CreateProvider();

            Assert.NotEmpty(provider.GetFontFamilyNames());
        }

        [Fact]
        public void Should_Match_Known_Family()
        {
            Assert.SkipUnless(IsSupported, "The provider is not supported on this platform.");

            using var provider = CreateProvider();

            Assert.True(provider.TryMatchFamily(KnownFamilyName, FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out var match));

            Assert.False(string.IsNullOrEmpty(match.FamilyName));
            Assert.True(match.TryOpenFontMemory(out var fontMemory));

            fontMemory.Dispose();
        }

        [Fact]
        public void Should_Match_Known_Codepoint()
        {
            Assert.SkipUnless(IsSupported, "The provider is not supported on this platform.");

            using var provider = CreateProvider();

            Assert.True(provider.TryMatchCharacter(KnownCodepoint, FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, null, null, out var match));

            Assert.False(string.IsNullOrEmpty(match.FamilyName));
        }

        [Fact]
        public void Known_Family_Should_Have_Faces()
        {
            Assert.SkipUnless(IsSupported, "The provider is not supported on this platform.");

            using var provider = CreateProvider();

            Assert.True(provider.TryGetFamilyFaces(KnownFamilyName, out var faces));
            Assert.NotEmpty(faces);
        }

        [Fact]
        public void Default_Face_Should_Be_Consistent()
        {
            Assert.SkipUnless(IsSupported, "The provider is not supported on this platform.");

            using var provider = CreateProvider();

            if (provider.TryGetDefaultFontFace(out var face))
            {
                Assert.False(string.IsNullOrEmpty(face.FamilyName));
                Assert.True(face.TryOpenFontMemory(out var fontMemory));

                fontMemory.Dispose();
            }
        }
    }
}
