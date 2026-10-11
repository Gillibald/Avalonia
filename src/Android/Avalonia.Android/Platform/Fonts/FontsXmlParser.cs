using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace Avalonia.Android.Platform.Fonts
{
    /// <summary>
    /// Reads Android font configuration files: <c>fonts.xml</c> (format versions 21 to 23),
    /// <c>font_fallback.xml</c>, vendor additions in the same format, and product customizations
    /// (<c>fonts-modification</c> root). Families and aliases are read wherever they appear below
    /// the root, unknown elements and attributes are skipped, and document type declarations are
    /// never processed.
    /// </summary>
    /// <remarks>
    /// The format is not a public contract (the files carry a warning that it can change with any
    /// release), so a file that is not well-formed yields an empty configuration rather than a
    /// partial one, and the provider falls back to the platform font APIs.
    /// </remarks>
    internal static class FontsXmlParser
    {
        private static readonly XmlReaderSettings s_settings = new()
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        /// <summary>
        /// Parses a configuration whose font file names resolve against the specified directory.
        /// </summary>
        public static FontsXmlConfig Parse(TextReader reader, string fontDirectory)
        {
            try
            {
                using var xml = XmlReader.Create(reader, s_settings);

                return Read(xml, fontDirectory);
            }
            catch (XmlException)
            {
                return FontsXmlConfig.Empty;
            }
        }

        /// <summary>
        /// Parses the configuration file at the specified path.
        /// </summary>
        /// <returns><see langword="true"/> if the file could be read and declares at least one
        /// family or alias; otherwise, <see langword="false"/>.</returns>
        public static bool TryParseFile(string path, string fontDirectory, [NotNullWhen(true)] out FontsXmlConfig? config)
        {
            config = null;

            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

                var parsed = Parse(reader, fontDirectory);

                if (parsed.Families.Count == 0 && parsed.Aliases.Count == 0)
                {
                    return false;
                }

                config = parsed;

                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                // font_fallback.xml exists on API 35 but is not readable from an app's domain.
                return false;
            }
        }

        private static FontsXmlConfig Read(XmlReader xml, string fontDirectory)
        {
            var families = new List<FontsXmlFamily>();
            var aliases = new List<FontsXmlAlias>();

            while (xml.Read())
            {
                if (xml.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                switch (xml.LocalName)
                {
                    case "family":
                        families.Add(ReadFamily(xml, fontDirectory));
                        break;
                    case "alias":
                        if (ReadAlias(xml) is { } alias)
                        {
                            aliases.Add(alias);
                        }

                        break;
                }
            }

            return new FontsXmlConfig(families, aliases);
        }

        private static FontsXmlFamily ReadFamily(XmlReader xml, string fontDirectory)
        {
            var name = NullIfEmpty(xml.GetAttribute("name"));
            var languages = SplitLanguages(xml.GetAttribute("lang"));
            var variant = xml.GetAttribute("variant") switch
            {
                "compact" => FontsXmlVariant.Compact,
                "elegant" => FontsXmlVariant.Elegant,
                _ => FontsXmlVariant.Default,
            };
            var ignore = string.Equals(xml.GetAttribute("ignore"), "true", StringComparison.OrdinalIgnoreCase);
            var fonts = new List<FontsXmlFont>();

            if (!xml.IsEmptyElement)
            {
                var depth = xml.Depth;

                while (xml.Read() && !(xml.NodeType == XmlNodeType.EndElement && xml.Depth == depth))
                {
                    if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "font" &&
                        ReadFont(xml, fontDirectory) is { } font)
                    {
                        fonts.Add(font);
                    }
                }
            }

            return new FontsXmlFamily(name, languages, variant, ignore, fonts);
        }

        private static FontsXmlFont? ReadFont(XmlReader xml, string fontDirectory)
        {
            var weight = ParseInt(xml.GetAttribute("weight"), 400);
            var isItalic = string.Equals(xml.GetAttribute("style"), "italic", StringComparison.Ordinal);
            var index = ParseInt(xml.GetAttribute("index"), 0);
            var postScriptName = NullIfEmpty(xml.GetAttribute("postScriptName"));
            var fallbackFor = NullIfEmpty(xml.GetAttribute("fallbackFor"));
            var axes = new List<FontsXmlAxis>();
            var text = new StringBuilder();

            if (!xml.IsEmptyElement)
            {
                var depth = xml.Depth;

                while (xml.Read() && !(xml.NodeType == XmlNodeType.EndElement && xml.Depth == depth))
                {
                    switch (xml.NodeType)
                    {
                        // The file name is the element's text; vendors put it on its own line between
                        // indentation and the axis children.
                        case XmlNodeType.Text:
                        case XmlNodeType.CDATA:
                        case XmlNodeType.Whitespace:
                        case XmlNodeType.SignificantWhitespace:
                            if (xml.Depth == depth + 1)
                            {
                                text.Append(xml.Value);
                            }

                            break;
                        case XmlNodeType.Element when xml.LocalName == "axis":
                            var tag = xml.GetAttribute("tag");

                            if (tag is { Length: 4 } &&
                                float.TryParse(xml.GetAttribute("stylevalue"), NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out var value))
                            {
                                axes.Add(new FontsXmlAxis(tag, value));
                            }

                            break;
                    }
                }
            }

            var fileName = text.ToString().Trim();

            if (fileName.Length == 0)
            {
                return null;
            }

            return new FontsXmlFont(fileName, Path.Combine(fontDirectory, fileName), index, weight, isItalic,
                postScriptName, fallbackFor, axes);
        }

        private static FontsXmlAlias? ReadAlias(XmlReader xml)
        {
            var name = NullIfEmpty(xml.GetAttribute("name"));
            var to = NullIfEmpty(xml.GetAttribute("to"));

            if (name is null || to is null)
            {
                return null;
            }

            var weightAttribute = xml.GetAttribute("weight");
            int? weight = int.TryParse(weightAttribute, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;

            return new FontsXmlAlias(name, to, weight);
        }

        private static IReadOnlyList<string> SplitLanguages(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }

            return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        private static int ParseInt(string? value, int defaultValue) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : defaultValue;

        private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
