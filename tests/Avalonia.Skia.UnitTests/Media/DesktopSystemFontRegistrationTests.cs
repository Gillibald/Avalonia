using System;
using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Which system font collection the desktop builder chains register on Windows, observed
    /// through the font data of a resolved typeface: path-based providers serve memory-mapped
    /// font files, the Skia provider a copy of each face's file.
    /// </summary>
    public class DesktopSystemFontRegistrationTests
    {
        [Win32Fact("Requires Windows")]
        public void UsePlatformDetect_Should_Serve_System_Fonts_From_Mapped_Font_Files()
        {
            var builder = AppBuilder.Configure<Application>().UsePlatformDetect();

            Assert.True(ResolvesFromMappedFontFile(builder));
        }

        [Win32Fact("Requires Windows")]
        public void Manual_Skia_Chain_Should_Keep_The_Skia_System_Font_Provider()
        {
            var builder = AppBuilder.Configure<Application>().UseWin32().UseSkia().UseHarfBuzz();

            Assert.False(ResolvesFromMappedFontFile(builder));
        }

        private static bool ResolvesFromMappedFontFile(AppBuilder builder)
        {
            using var scope = AvaloniaLocator.EnterScope();

            var fontManager = new FontManager();

            AvaloniaLocator.CurrentMutable.Bind<FontManager>().ToConstant(fontManager);

            try
            {
                // Runs the chain's font registrations (ConfigureFonts callbacks) without
                // initializing the windowing or render platform.
                builder.AfterSetupCallback(builder);

                Assert.True(fontManager.SystemFonts.TryGetGlyphTypeface("Segoe UI", FontStyle.Normal,
                    FontWeight.Normal, FontStretch.Normal, out var glyphTypeface));

                var face = Assert.IsType<SfntFace>(glyphTypeface.FontMemory);

                Assert.True(face.TryGetFontFileData(out var data, out _));

                return MemoryMarshal.TryGetMemoryManager<byte, FontFileMemory>(data, out _);
            }
            finally
            {
                ((IDisposable)fontManager).Dispose();
            }
        }
    }
}
