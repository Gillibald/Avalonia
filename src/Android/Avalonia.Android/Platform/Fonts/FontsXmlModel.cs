using System;
using System.Collections.Generic;

namespace Avalonia.Android.Platform.Fonts
{
    /// <summary>
    /// The families and aliases of one Android font configuration file (<c>fonts.xml</c>,
    /// <c>font_fallback.xml</c>, or an OEM or product addition), in file order.
    /// </summary>
    internal sealed class FontsXmlConfig
    {
        public static readonly FontsXmlConfig Empty =
            new(Array.Empty<FontsXmlFamily>(), Array.Empty<FontsXmlAlias>());

        public FontsXmlConfig(IReadOnlyList<FontsXmlFamily> families, IReadOnlyList<FontsXmlAlias> aliases)
        {
            Families = families;
            Aliases = aliases;
        }

        public IReadOnlyList<FontsXmlFamily> Families { get; }

        public IReadOnlyList<FontsXmlAlias> Aliases { get; }
    }

    internal enum FontsXmlVariant
    {
        Default,
        Compact,
        Elegant,
    }

    /// <summary>
    /// A <c>family</c> element. Families without a name form the system fallback chain, in file
    /// order; named families are addressable by name and through aliases.
    /// </summary>
    internal sealed class FontsXmlFamily
    {
        public FontsXmlFamily(string? name, IReadOnlyList<string> languages, FontsXmlVariant variant, bool ignore,
            IReadOnlyList<FontsXmlFont> fonts)
        {
            Name = name;
            Languages = languages;
            Variant = variant;
            Ignore = ignore;
            Fonts = fonts;
        }

        public string? Name { get; }

        /// <summary>Language tags from the comma-separated <c>lang</c> attribute.</summary>
        public IReadOnlyList<string> Languages { get; }

        public FontsXmlVariant Variant { get; }

        /// <summary>Whether the family is excluded from fallback (<c>ignore="true"</c>).</summary>
        public bool Ignore { get; }

        public IReadOnlyList<FontsXmlFont> Fonts { get; }
    }

    /// <summary>
    /// A <c>font</c> element: one face of a file, the style it is listed for, and the axis values it
    /// is instantiated with.
    /// </summary>
    internal sealed class FontsXmlFont
    {
        public FontsXmlFont(string fileName, string path, int index, int weight, bool isItalic,
            string? postScriptName, string? fallbackFor, IReadOnlyList<FontsXmlAxis> axes)
        {
            FileName = fileName;
            Path = path;
            Index = index;
            Weight = weight;
            IsItalic = isItalic;
            PostScriptName = postScriptName;
            FallbackFor = fallbackFor;
            Axes = axes;
        }

        /// <summary>The file name as written in the configuration, trimmed.</summary>
        public string FileName { get; }

        /// <summary>The file name resolved against the configuration's font directory.</summary>
        public string Path { get; }

        public int Index { get; }

        public int Weight { get; }

        public bool IsItalic { get; }

        public string? PostScriptName { get; }

        /// <summary>The named family this entry substitutes for inside a fallback family (<c>serif</c>).</summary>
        public string? FallbackFor { get; }

        public IReadOnlyList<FontsXmlAxis> Axes { get; }
    }

    internal readonly record struct FontsXmlAxis(string Tag, float Value);

    /// <summary>
    /// An <c>alias</c> element: another name for a family, optionally restricted to the entries
    /// nearest a weight.
    /// </summary>
    internal sealed class FontsXmlAlias
    {
        public FontsXmlAlias(string name, string to, int? weight)
        {
            Name = name;
            To = to;
            Weight = weight;
        }

        public string Name { get; }

        public string To { get; }

        public int? Weight { get; }
    }
}
