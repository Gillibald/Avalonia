using System;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization.TrueType
{
    /// <summary>
    /// GETINFO tells the programs which engine they run on, and prep may branch on the answer,
    /// so the size states of text told different engines are separate: Light grayscale and LCD
    /// text runs v40, Strong grayscale and LCD text and every bi-level build run the classic
    /// engine.
    /// </summary>
    public class TrueTypeHinterEngineTests : IDisposable
    {
        // The application every test of the class draws text in.
        private readonly IDisposable _application = UnitTestApplication.Start();

        public void Dispose() => _application.Dispose();

        private const string NotoMonoUri =
            "resm:Avalonia.Base.UnitTests.Assets.NotoMono-Regular.ttf?assembly=Avalonia.Base.UnitTests";

        [Fact]
        public void Light_And_Strong_Hint_On_Separate_Size_States()
        {
            var typeface = SyntheticFont.FromAsset(NotoMonoUri).CreateGlyphTypeface();
            var scaleQ = GlyphMaskKey.QuantizeScale(16);

            var light = typeface.GetTrueTypeHinter(scaleQ, GlyphMaskMode.Antialiased);
            var lightLcd = typeface.GetTrueTypeHinter(scaleQ, GlyphMaskMode.Subpixel);
            var strong = typeface.GetTrueTypeHinter(scaleQ, GlyphMaskMode.Antialiased, strong: true);
            var strongLcd = typeface.GetTrueTypeHinter(scaleQ, GlyphMaskMode.Subpixel, strong: true);
            var aliased = typeface.GetTrueTypeHinter(scaleQ, GlyphMaskMode.Aliased);
            var strongAliased = typeface.GetTrueTypeHinter(scaleQ, GlyphMaskMode.Aliased, strong: true);

            Assert.NotNull(light);
            Assert.NotNull(strong);
            Assert.NotSame(light, strong);
            Assert.NotSame(lightLcd, strongLcd);

            // The classic engine answers the same for grayscale and LCD targets, and bi-level
            // builds run it on both axes whatever the hinting mode.
            Assert.Same(strong, strongLcd);
            Assert.Same(aliased, strongAliased);
            Assert.NotSame(strong, aliased);
            Assert.Equal(4, typeface.TrueTypeHinterCount);
        }
    }
}
