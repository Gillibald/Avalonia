using System;
using System.IO;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization.TrueType
{
    /// <summary>
    /// Loads font files straight from the render-test assets. The embedded Inter resource
    /// is an uninstructed build, so tests that need real TrueType programs (Noto Mono's
    /// ttfautohint set, the subset Inter) read the files directly.
    /// </summary>
    internal static class TestFontFiles
    {
        public static byte[] Load(string fileName)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && directory.Name != "tests")
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);

            return File.ReadAllBytes(Path.Combine(directory!.FullName, "Avalonia.RenderTests", "Assets", fileName));
        }
    }
}
