using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace Avalonia.Android.Platform.Fonts
{
    internal static class FontsXmlParser
    {
        public static FontsXmlConfig Parse(TextReader reader, string fontDirectory) => FontsXmlConfig.Empty;

        public static bool TryParseFile(string path, string fontDirectory, [NotNullWhen(true)] out FontsXmlConfig? config)
        {
            config = null;

            return false;
        }
    }
}
