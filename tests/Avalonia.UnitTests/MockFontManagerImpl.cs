using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;

namespace Avalonia.UnitTests
{
    public class MockFontManagerImpl : IFontManagerImpl
    {
        public string GetDefaultFontFamilyName() => "Default";

        public IEnumerable<string> GetInstalledFontFamilyNames(bool checkForUpdates = false) =>
            new[] { "Default" };

        public FontKey MatchCharacter(int codepoint, FontWeight fontWeight = default, FontStyle fontStyle = default,
            FontFamily fontFamily = null, CultureInfo culture = null)
        {
            return new FontKey(new FontFamily("Default"), FontWeight.Normal, FontStyle.Normal);
        }
    }
}
