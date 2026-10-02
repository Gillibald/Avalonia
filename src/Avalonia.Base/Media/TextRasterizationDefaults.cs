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
        /// Initialized from the running platform; test assemblies whose expectations were
        /// recorded against one stack pin it so they render the same on every host.
        /// </summary>
        public static TextRasterizationMode PlatformDefault { get; set; } =
            ForPlatform(CurrentPlatform(), RuntimeInformation.ProcessArchitecture);

        /// <summary>
        /// Returns <paramref name="explicitMode"/> when the application chose one, otherwise the
        /// default for <paramref name="platform"/> and <paramref name="architecture"/>.
        /// </summary>
        public static TextRasterizationMode Resolve(TextRasterizationMode? explicitMode,
            TextRasterizationPlatform platform, Architecture architecture)
            => explicitMode ?? ForPlatform(platform, architecture);

        /// <summary>
        /// Returns the default mode for <paramref name="platform"/> and <paramref name="architecture"/>.
        /// </summary>
        public static TextRasterizationMode ForPlatform(TextRasterizationPlatform platform,
            Architecture architecture)
            => TextRasterizationMode.Managed;

        private static TextRasterizationPlatform CurrentPlatform()
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
