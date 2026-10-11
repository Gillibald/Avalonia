using System;
using System.IO;

namespace Avalonia.Android.UnitTests
{
    internal static class Fixtures
    {
        public static string GetPath(string fileName) =>
            Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

        public static string GetFontPath(string fileName) =>
            Path.Combine(AppContext.BaseDirectory, "Fonts", fileName);
    }
}
