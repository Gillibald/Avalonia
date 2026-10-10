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
    /// Resolves the <see cref="TextRasterizationMode"/> an application gets when it does not
    /// choose one through <see cref="FontManagerOptions.TextRasterizationMode"/>.
    /// </summary>
    internal static class TextRasterizationDefaults
    {
        /// <summary>
        /// Gets or sets the mode used when the application leaves
        /// <see cref="FontManagerOptions.TextRasterizationMode"/> unset (or registers no options).
        /// Initialized from the running platform as if it renders on the GPU; a platform whose
        /// default depends on the render path (Android) sets it again once it has chosen the path,
        /// before the first glyph run is created. Test assemblies whose expectations were
        /// recorded against one stack pin it so they render the same on every host.
        /// </summary>
        public static TextRasterizationMode PlatformDefault { get; set; } =
            ForPlatform(CurrentPlatform(), RuntimeInformation.ProcessArchitecture);

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
        /// Gets or sets whether text whose rendering mode is unspecified renders subpixel (LCD)
        /// where the surface allows it, rather than grayscale. Initialized from the running
        /// platform; test assemblies whose expectations were recorded with one choice pin it.
        /// </summary>
        public static bool UnspecifiedRendersSubpixel { get; set; } = RendersSubpixelByDefault(CurrentPlatform());

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
