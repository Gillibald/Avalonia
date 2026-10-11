using Avalonia.Media;
using Xunit;

namespace Avalonia.Skia.RenderTests
{
    /// <summary>
    /// The render test host provides system fonts, so text without an explicit font family has a
    /// default family to render with.
    /// </summary>
    public class SystemFontsTests : TestBase
    {
        public SystemFontsTests()
            : base(@"SystemFonts")
        {
        }

        [Fact]
        public void Host_Should_Provide_A_Default_Font_Family()
        {
            var fontManager = FontManager.Current;

            Assert.NotEmpty(fontManager.SystemFonts);
            Assert.False(string.IsNullOrEmpty(fontManager.DefaultFontFamily.Name));
            Assert.True(fontManager.TryGetGlyphTypeface(new Typeface(FontFamily.Default), out _));
        }
    }
}
