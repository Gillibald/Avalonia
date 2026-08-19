using System;
using Avalonia.Base.UnitTests.Media.Fonts.Tables;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Base.UnitTests.Media
{
    /// <summary>
    /// The explicit-beats-implicit composition rule on <c>WithVariations</c>: applied to
    /// a typeface already bound to a position (an implicit platform-styled match, or any
    /// varied clone), explicit settings win per mentioned axis while unmentioned axes
    /// keep the current position — CSS <c>font-variation-settings</c> semantics.
    /// </summary>
    public class GlyphTypefaceVariationCompositionTests
    {
        private const string InterVariableAsset =
            "resm:Avalonia.Base.UnitTests.Assets.InterVariable.ttf?assembly=Avalonia.Base.UnitTests";

        private static readonly OpenTypeTag s_wghtTag = OpenTypeTag.Parse("wght");
        private static readonly OpenTypeTag s_opszTag = OpenTypeTag.Parse("opsz");

        private static GlyphTypeface LoadTypeface()
        {
            var assetLoader = new StandardAssetLoader();
            using var stream = assetLoader.Open(new Uri(InterVariableAsset));
            return new GlyphTypeface(new CustomPlatformTypeface(stream));
        }

        [Fact]
        public void Unmentioned_Axes_Keep_The_Receivers_Position()
        {
            var source = LoadTypeface();
            var bold = source.WithVariations(FontVariationSettings.Parse("wght=700"));

            // Setting only opsz on the bold clone must not lose the bold weight.
            var boldLarge = bold.WithVariations(FontVariationSettings.Parse("opsz=32"));

            Assert.True(boldLarge.VariationPosition.TryGetCoordinate(s_wghtTag, out var wght));
            Assert.True(bold.VariationPosition.TryGetCoordinate(s_wghtTag, out var boldWght));
            Assert.Equal(boldWght, wght);
            Assert.True(boldLarge.VariationPosition.TryGetCoordinate(s_opszTag, out _));
        }

        [Fact]
        public void Mentioning_An_Axis_At_Its_Default_Unsets_It()
        {
            var source = LoadTypeface();
            var bold = source.WithVariations(FontVariationSettings.Parse("wght=700"));

            // wght=400 is Inter's default — an explicit mention wins even when it lands
            // on the default, returning the axis to the design position.
            var unbolded = bold.WithVariations(FontVariationSettings.Parse("wght=400"));

            Assert.Same(source, unbolded);
        }

        [Fact]
        public void Null_Settings_Keep_The_Receiver()
        {
            var source = LoadTypeface();
            var bold = source.WithVariations(FontVariationSettings.Parse("wght=700"));

            // No explicit overrides — the current position stays in effect.
            Assert.Same(bold, bold.WithVariations(null));
            Assert.Same(bold, bold.WithVariations(FontVariationSettings.Empty));
        }

        [Fact]
        public void Named_Instance_Replaces_The_Position_Outright()
        {
            var source = LoadTypeface();
            var bold = source.WithVariations(FontVariationSettings.Parse("wght=700"));
            var lastIndex = source.NamedInstances.Count - 1; // Black (wght=900)

            // A named instance defines every axis, so composition does not apply: the
            // result matches selecting the instance from the source directly.
            var fromClone = bold.WithVariations(null, instanceIndex: lastIndex);
            var fromSource = source.WithVariations(null, instanceIndex: lastIndex);

            Assert.Same(fromSource, fromClone);
        }

        [Fact]
        public void Composition_Does_Not_Change_Source_Behavior()
        {
            // On a default-position typeface the composition path is inert — the
            // absolute semantics pinned by GlyphTypefaceWithVariationTests stand.
            var source = LoadTypeface();

            Assert.Same(source, source.WithVariations(null));

            var bold = source.WithVariations(FontVariationSettings.Parse("wght=700"));

            Assert.True(bold.VariationPosition.TryGetCoordinate(s_wghtTag, out _));
            Assert.False(bold.VariationPosition.TryGetCoordinate(s_opszTag, out _));
        }
    }
}
