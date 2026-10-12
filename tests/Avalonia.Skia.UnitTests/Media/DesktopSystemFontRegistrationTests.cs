using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.LinuxFramebuffer;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Which system font collection the desktop builder chains register on Windows and Linux, observed
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

        [LinuxFact("Requires Linux")]
        public void UsePlatformDetect_On_Linux_Should_Serve_System_Fonts_From_Mapped_Font_Files()
        {
            var builder = AppBuilder.Configure<Application>().UsePlatformDetect();

            Assert.True(ResolvesFromMappedFontFile(builder, "sans-serif"));
        }

        [LinuxFact("Requires Linux")]
        public void Manual_X11_Skia_Chain_Should_Keep_The_Skia_System_Font_Provider()
        {
            var builder = AppBuilder.Configure<Application>().UseX11().UseSkia().UseHarfBuzz();

            Assert.False(ResolvesFromMappedFontFile(builder, "sans-serif"));
        }

        [LinuxFact("Requires Linux")]
        public void Linux_Framebuffer_Should_Serve_System_Fonts_From_Mapped_Font_Files()
        {
            var builder = AppBuilder.Configure<Application>();

            // Configures the framebuffer platform on the builder; the output backend is only kept.
            LinuxFramebufferPlatform.Initialize(builder, null!, null);

            Assert.True(ResolvesFromMappedFontFile(builder, "sans-serif"));
        }

        [Fact]
        public void WithFontconfigFonts_Should_Register_The_Fontconfig_Provider_On_Every_System()
        {
            // An explicit opt-in registers fontconfig even where libfontconfig is missing: only
            // the platform defaults fall back to the collection registered before.
            var builder = AppBuilder.Configure<Application>().UseSkia().WithFontconfigFonts();

            Assert.IsType<FontconfigFontProvider>(GetRegisteredProvider(builder));
        }

        private static ISystemFontProvider GetRegisteredProvider(AppBuilder builder)
        {
            using var scope = AvaloniaLocator.EnterScope();

            var fontManager = new FontManager();

            AvaloniaLocator.CurrentMutable.Bind<FontManager>().ToConstant(fontManager);

            try
            {
                builder.AfterSetupCallback(builder);

                var collection = Assert.IsType<SystemFontCollection>(fontManager.SystemFonts);

                return (ISystemFontProvider)typeof(SystemFontCollection)
                    .GetField("_provider", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(collection)!;
            }
            finally
            {
                ((IDisposable)fontManager).Dispose();
            }
        }

        private static bool ResolvesFromMappedFontFile(AppBuilder builder, string familyName = "Segoe UI")
        {
            using var scope = AvaloniaLocator.EnterScope();

            var fontManager = new FontManager();

            AvaloniaLocator.CurrentMutable.Bind<FontManager>().ToConstant(fontManager);

            try
            {
                // Runs the chain's font registrations (ConfigureFonts callbacks) without
                // initializing the windowing or render platform.
                builder.AfterSetupCallback(builder);

                Assert.True(fontManager.SystemFonts.TryGetGlyphTypeface(familyName, FontStyle.Normal,
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
