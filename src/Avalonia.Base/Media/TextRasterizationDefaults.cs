using System;
using System.Runtime.InteropServices;

namespace Avalonia.Media
{
    /// <summary>
    /// Operating system families the <see cref="TextRasterizationMode"/> default distinguishes.
    /// </summary>
    internal enum TextRasterizationPlatform
    {
        Other,
        Windows,
        Linux,
        MacOS,
        IOS,
        Android,
        Browser,
    }

    /// <summary>
    /// What text an application renders when it does not choose: the
    /// <see cref="TextRasterizationMode"/> when <see cref="GlyphRasterizationOptions.TextRasterizationMode"/>
    /// is unset, and whether unspecified text renders subpixel. The application's
    /// <see cref="GlyphRasterizationContext"/> takes an instance bound in its locator, else the running
    /// platform's defaults as if it renders on the GPU (<see cref="ForProcess"/>). A platform whose
    /// default depends on the render path (Android) binds one once it has chosen the path, before
    /// the first glyph run is created; test applications whose expectations were recorded against
    /// one stack bind one so they render the same on every host.
    /// </summary>
    internal sealed class TextRasterizationDefaults
    {
        public TextRasterizationDefaults(TextRasterizationMode platformDefault, bool unspecifiedRendersSubpixel)
        {
            PlatformDefault = platformDefault;
            UnspecifiedRendersSubpixel = unspecifiedRendersSubpixel;
        }

        /// <summary>The running platform's defaults, for an application that renders on the GPU.</summary>
        public static TextRasterizationDefaults ForProcess() => new(
            ForPlatform(CurrentPlatform(), RuntimeInformation.ProcessArchitecture),
            RendersSubpixelByDefault(CurrentPlatform()));

        /// <summary>
        /// The defaults of the current application: its context's, or the running platform's when it
        /// has no <see cref="GlyphRasterizationContext"/>.
        /// </summary>
        public static TextRasterizationDefaults OfCurrentApplication()
            => GlyphRasterizationContext.Current?.RasterizationDefaults ?? ForProcess();

        /// <summary>
        /// The mode used when the application leaves <see cref="GlyphRasterizationOptions.TextRasterizationMode"/>
        /// unset (or registers no options).
        /// </summary>
        public TextRasterizationMode PlatformDefault { get; }

        /// <summary>
        /// Returns <paramref name="explicitMode"/> when the application chose one, otherwise the
        /// default for <paramref name="platform"/>, <paramref name="architecture"/> and the render path.
        /// </summary>
        public static TextRasterizationMode Resolve(TextRasterizationMode? explicitMode,
            TextRasterizationPlatform platform, Architecture architecture, bool rendersInSoftware = false)
            => explicitMode ?? ForPlatform(platform, architecture, rendersInSoftware);

        /// <summary>
        /// Managed rasterization is the default only where it was measured against the backend
        /// and its vector paths tuned for the hardware; everywhere else the backend's text stack
        /// stays the default. <paramref name="rendersInSoftware"/> tells whether the platform
        /// composes frames on the CPU instead of the GPU; only Android's default depends on it.
        /// </summary>
        public static TextRasterizationMode ForPlatform(TextRasterizationPlatform platform,
            Architecture architecture, bool rendersInSoftware = false)
            => (platform, architecture) switch
            {
                (TextRasterizationPlatform.Windows, Architecture.X64) => TextRasterizationMode.Managed,
                // Browser apps always run WebAssembly. Measured under AOT, which Avalonia browser
                // apps publish with; interpreter builds are slower than the backend but keep it.
                (TextRasterizationPlatform.Browser, _) => TextRasterizationMode.Managed,
                (TextRasterizationPlatform.MacOS, Architecture.Arm64) => TextRasterizationMode.Managed,
                // Measured on ARM64 phones under Mono AOT: faster than the backend when EGL or
                // Vulkan composes the frame, but slower on CPU surfaces, where the backend's
                // software text path blits faster than the managed one.
                (TextRasterizationPlatform.Android, Architecture.Arm64) => rendersInSoftware
                    ? TextRasterizationMode.Backend
                    : TextRasterizationMode.Managed,
                _ => TextRasterizationMode.Backend,
            };

        /// <summary>
        /// Whether text whose rendering mode is unspecified renders subpixel (LCD) where the surface
        /// allows it, rather than grayscale.
        /// </summary>
        public bool UnspecifiedRendersSubpixel { get; }

        /// <summary>
        /// Whether unspecified text renders subpixel on <paramref name="platform"/> where the
        /// surface allows it. Apple platforms draw text grayscale (macOS since 10.14), and the
        /// backend's CoreText scaler draws no subpixel text there either.
        /// </summary>
        public static bool RendersSubpixelByDefault(TextRasterizationPlatform platform)
            => platform is not (TextRasterizationPlatform.MacOS or TextRasterizationPlatform.IOS);

        /// <summary>The operating system family the process runs on.</summary>
        internal static TextRasterizationPlatform CurrentPlatform()
        {
            if (OperatingSystem.IsBrowser())
                return TextRasterizationPlatform.Browser;
            if (OperatingSystem.IsWindows())
                return TextRasterizationPlatform.Windows;
            if (OperatingSystem.IsAndroid())
                return TextRasterizationPlatform.Android;
            if (OperatingSystem.IsIOS() || OperatingSystem.IsTvOS())
                return TextRasterizationPlatform.IOS;
            if (OperatingSystem.IsMacOS())
                return TextRasterizationPlatform.MacOS;
            if (OperatingSystem.IsLinux())
                return TextRasterizationPlatform.Linux;
            return TextRasterizationPlatform.Other;
        }
    }
}
