using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Platform;

namespace Avalonia.Android.Platform.Fonts
{
    /// <summary>
    /// System font provider for Android over the device's font configuration
    /// (<c>/system/etc/fonts.xml</c> and vendor and product additions) and, on API 29 and later,
    /// the NDK font API. Descriptors are file paths with collection indices, so the font system
    /// maps each font file once and shares it between all faces over it.
    /// </summary>
    /// <remarks>
    /// Samsung's user-selectable system font (FlipFont) is not reflected: it replaces the Java
    /// typeface defaults and appears neither in the configuration nor in the NDK font API.
    /// </remarks>
    internal sealed class AndroidFontProvider : ISystemFontProvider
    {
        private static readonly AndroidFontConfiguration s_systemConfiguration = new(
            new[]
            {
                // font_fallback.xml is the source of truth from API 35, when an app can read it;
                // fonts.xml stays for compatibility.
                new AndroidFontConfigurationFile("/system/etc/font_fallback.xml", "/system/fonts"),
                new AndroidFontConfigurationFile("/system/etc/fonts.xml", "/system/fonts"),
            },
            new[]
            {
                new AndroidFontConfigurationFile("/system/etc/fonts_additional.xml", "/system/fonts"),
                new AndroidFontConfigurationFile("/vendor/etc/fonts.xml", "/vendor/fonts"),
                new AndroidFontConfigurationFile("/product/etc/fonts_customization.xml", "/product/fonts"),
            });

        private readonly object _lock = new();
        private readonly AndroidFontConfiguration _configuration;
        private readonly Func<AndroidSystemFontSource?> _systemFontsFactory;
        private AndroidSystemFontSource? _systemFonts;
        private volatile AndroidFontCatalog? _catalog;
        private bool _disposed;

        public AndroidFontProvider()
            : this(s_systemConfiguration, CreateSystemFontSource)
        {
        }

        /// <summary>
        /// Creates a provider over the specified configuration files and platform source. Nothing
        /// is read until the first query.
        /// </summary>
        internal AndroidFontProvider(AndroidFontConfiguration configuration, Func<AndroidSystemFontSource?> systemFonts)
        {
            _configuration = configuration;
            _systemFontsFactory = systemFonts;
        }

        public bool TryGetDefaultFontFace([NotNullWhen(true)] out SystemFontFace? face)
        {
            face = null;

            return TryGetCatalog(out var catalog) && catalog.TryGetDefaultFace(out face);
        }

        public IReadOnlyList<string> GetFontFamilyNames()
        {
            return TryGetCatalog(out var catalog) ? catalog.GetFamilyNames() : Array.Empty<string>();
        }

        public bool TryMatchFamily(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
            [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            return TryGetCatalog(out var catalog) && catalog.TryMatchFamily(familyName, style, weight, stretch, out match);
        }

        public bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch,
            string? familyName, CultureInfo? culture, [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            return TryGetCatalog(out var catalog) &&
                   catalog.TryMatchCharacter(codepoint, style, weight, stretch, familyName, culture, out match);
        }

        public bool TryGetFamilyFaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<SystemFontFace>? faces)
        {
            faces = null;

            return TryGetCatalog(out var catalog) && catalog.TryGetFamilyFaces(familyName, out faces);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _catalog = null;
                _systemFonts?.Dispose();
                _systemFonts = null;
            }
        }

        /// <summary>
        /// The NDK font API exists from API 29; earlier releases rely on the configuration alone.
        /// </summary>
        private static AndroidSystemFontSource? CreateSystemFontSource() =>
            OperatingSystem.IsAndroidVersionAtLeast(29) ? AndroidNdkFontSource.Create() : null;

        private bool TryGetCatalog([NotNullWhen(true)] out AndroidFontCatalog? catalog)
        {
            catalog = _catalog;

            if (catalog != null)
            {
                return true;
            }

            lock (_lock)
            {
                if (_disposed)
                {
                    return false;
                }

                if (_catalog == null)
                {
                    _systemFonts = _systemFontsFactory();
                    _catalog = AndroidFontCatalog.Create(_configuration, _systemFonts);

                    if (_catalog.IsPlatformOnly)
                    {
                        Logger.TryGet(LogEventLevel.Information, LogArea.Fonts)?.Log(this,
                            "No readable font configuration file; system fonts come from the platform font API only.");
                    }
                }

                catalog = _catalog;

                return true;
            }
        }
    }
}
