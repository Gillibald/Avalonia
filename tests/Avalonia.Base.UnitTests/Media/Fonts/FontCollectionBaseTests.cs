using System;
using System.Collections.Generic;
using System.Reflection;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Avalonia.UnitTests;
using Moq;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts
{
    public class FontCollectionBaseTests
    {
        private const string InterFontUri = "resm:Avalonia.Base.UnitTests.Assets.Inter-Regular.ttf?assembly=Avalonia.Base.UnitTests";

        [Fact]
        public void Should_Add_GlyphTypeface_From_Stream_Without_Platform_Services()
        {
            var collection = new TestFontCollection();

            using var stream = SfntFaceTestHelper.OpenAsset(InterFontUri);

            Assert.True(collection.TryAddGlyphTypeface(stream, out var glyphTypeface));
            Assert.Equal("Inter", glyphTypeface.FamilyName);
            Assert.IsType<SfntFace>(glyphTypeface.FontMemory);
        }

        [Fact]
        public void Synthetic_GlyphTypeface_Should_Share_Font_Memory()
        {
            var collection = new TestFontCollection();

            using var stream = SfntFaceTestHelper.OpenAsset(InterFontUri);

            Assert.True(collection.TryAddGlyphTypeface(stream, out var glyphTypeface));

            var face = Assert.IsType<SfntFace>(glyphTypeface.FontMemory);
            var references = GetReferenceCount(face);

            Assert.True(collection.TryCreateSyntheticGlyphTypeface(glyphTypeface, FontStyle.Italic, FontWeight.Bold,
                FontStretch.Normal, out var syntheticGlyphTypeface));

            Assert.Equal(FontSimulations.Bold | FontSimulations.Oblique, syntheticGlyphTypeface.FontSimulations);
            Assert.Equal(FontWeight.Bold, syntheticGlyphTypeface.Weight);
            Assert.Equal(FontStyle.Italic, syntheticGlyphTypeface.Style);

            Assert.Same(face, syntheticGlyphTypeface.FontMemory);
            Assert.Equal(references, GetReferenceCount(face));
        }

        [Fact]
        public void Requests_Needing_The_Same_Simulations_Should_Share_One_Synthetic()
        {
            var collection = new TestFontCollection();

            using var stream = SfntFaceTestHelper.OpenAsset(InterFontUri);

            Assert.True(collection.TryAddGlyphTypeface(stream, out var regular));

            Assert.True(collection.TryCreateSyntheticGlyphTypeface(regular, FontStyle.Normal, FontWeight.SemiBold,
                FontStretch.Normal, out var semiBold));
            Assert.True(collection.TryCreateSyntheticGlyphTypeface(regular, FontStyle.Normal, FontWeight.Bold,
                FontStretch.Normal, out var bold));

            Assert.Equal(FontSimulations.Bold, semiBold.FontSimulations);
            Assert.Same(semiBold, bold);
            Assert.Same(regular.WithSimulations(FontSimulations.Bold), bold);
        }

        [Fact]
        public void Synthetic_Over_A_Synthetic_Should_Keep_The_Source_Simulations()
        {
            var collection = new TestFontCollection();

            using var stream = SfntFaceTestHelper.OpenAsset(InterFontUri);

            Assert.True(collection.TryAddGlyphTypeface(stream, out var regular));

            Assert.True(collection.TryCreateSyntheticGlyphTypeface(regular, FontStyle.Normal, FontWeight.Bold,
                FontStretch.Normal, out var bold));
            Assert.True(collection.TryCreateSyntheticGlyphTypeface(bold, FontStyle.Italic, FontWeight.Bold,
                FontStretch.Normal, out var boldItalic));

            Assert.Equal(FontSimulations.Bold | FontSimulations.Oblique, boldItalic.FontSimulations);
            Assert.Same(regular.WithSimulations(FontSimulations.Bold | FontSimulations.Oblique), boldItalic);
        }

        [Fact]
        public void Disposing_The_Collection_Should_Dispose_Each_Render_Typeface_Once()
        {
            var created = new List<CountingTypeface>();
            var renderInterface = new Mock<IPlatformRenderInterface>();

            renderInterface
                .Setup(x => x.CreateTypeface(It.IsAny<GlyphTypeface>()))
                .Returns(() =>
                {
                    var typeface = new CountingTypeface();
                    created.Add(typeface);
                    return typeface;
                });

            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(
                renderInterface: renderInterface.Object)))
            {
                var collection = new TestFontCollection();

                using var stream = SfntFaceTestHelper.OpenAsset(InterFontUri);

                Assert.True(collection.TryAddGlyphTypeface(stream, out var regular));

                Assert.True(collection.TryCreateSyntheticGlyphTypeface(regular, FontStyle.Normal, FontWeight.Bold,
                    FontStretch.Normal, out var bold));
                Assert.True(collection.TryCreateSyntheticGlyphTypeface(regular, FontStyle.Italic, FontWeight.Bold,
                    FontStretch.Normal, out var boldItalic));

                _ = bold.PlatformTypeface;
                _ = boldItalic.PlatformTypeface;
                _ = regular.PlatformTypeface;

                var typeface = Assert.Single(created);

                ((IDisposable)collection).Dispose();

                Assert.Equal(1, typeface.DisposeCount);
            }
        }

        private static int GetReferenceCount(SfntFace face)
        {
            var data = typeof(SfntFace)
                .GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(face)!;

            return (int)data.GetType()
                .GetField("_refCount", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(data)!;
        }

        private sealed class CountingTypeface : IPlatformTypeface
        {
            public int DisposeCount { get; private set; }

            public void Dispose() => DisposeCount++;
        }

        private class TestFontCollection : FontCollectionBase
        {
            public override Uri Key => new Uri("fonts:TestFonts");
        }
    }
}
