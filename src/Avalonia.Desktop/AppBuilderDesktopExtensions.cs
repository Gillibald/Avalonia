using System;
using System.Runtime.InteropServices;
using Avalonia.Logging;

namespace Avalonia
{
    public static class AppBuilderDesktopExtensions
    {
        public static AppBuilder UsePlatformDetect(this AppBuilder builder)
        {
            // Always load HarfBuzz on desktop platforms
            LoadHarfBuzz(builder);

            // We don't have the ability to load every assembly right now, so we are
            // stuck with manual configuration here
            // Helpers are extracted to separate methods to take the advantage of the fact
            // that CLR doesn't try to load dependencies before referencing method is jitted
            // Additionally, by having a hard reference to each assembly,
            // we verify that the assemblies are in the final .deps.json file
            //  so .NET Core knows where to load the assemblies from.
            if (OperatingSystem.IsWindows())
            {
                LoadWin32(builder);
                LoadSkia(builder);
                // Registered after Skia so it replaces Skia's system font collection: DirectWrite
                // descriptors carry file paths, which the font system memory-maps instead of
                // copying each face's font file.
                LoadDirectWriteFonts(builder);
            }
            else if (OperatingSystem.IsMacOS())
            {
                LoadAvaloniaNative(builder);
                LoadSkia(builder);
            }
            else if (OperatingSystem.IsLinux())
            {
                LoadX11(builder);
                LoadSkia(builder);
                // Registered after Skia so it replaces Skia's system font collection: fontconfig
                // descriptors carry file paths, which the font system memory-maps itself.
                LoadFontconfigFonts(builder);
            }
            else
            {
                Logger.TryGet(LogEventLevel.Warning, LogArea.Platform)?.Log(builder,
                    "Avalonia.Desktop package was referenced on non-desktop platform or it isn't supported");
            }

            return builder;
        }

        static void LoadAvaloniaNative(AppBuilder builder)
             => builder.UseAvaloniaNative();
        static void LoadWin32(AppBuilder builder)
             => builder.UseWin32();

        static void LoadX11(AppBuilder builder)
             => builder.UseX11();

        static void LoadSkia(AppBuilder builder)
             => builder.UseSkia();

        static void LoadDirectWriteFonts(AppBuilder builder)
             => builder.WithDirectWriteFonts();

        static void LoadFontconfigFonts(AppBuilder builder)
        {
            // Without libfontconfig (minimal containers, Skia's dependency-free native build) the
            // fontconfig provider finds no fonts, while Skia's registration still finds them.
            if (NativeLibrary.TryLoad("libfontconfig.so.1", typeof(AppBuilderDesktopExtensions).Assembly, null, out _))
            {
                builder.WithFontconfigFonts();
            }
        }

        static void LoadHarfBuzz(AppBuilder builder)
             => builder.UseHarfBuzz();
    }
}
