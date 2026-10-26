using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Avalonia.Media.Imaging;

/// <summary>
/// Parses WIC metadata query paths and rewrites them to the canonical, container-neutral
/// paths a metadata store holds.
/// </summary>
internal static class MetadataQuery
{
    private static readonly (string From, string To)[] s_exifAliases =
    {
        ("/ifd/{ushort=34665}", "/ifd/exif"),
        ("/ifd/{ushort=34853}", "/ifd/gps"),
        ("/ifd/exif/{ushort=40965}", "/ifd/exif/interop"),
    };

    // Photoshop resources are selected by {ulonglong=signature << 16 | id}; WIC also names the
    // three it parses. The IPTC record of resource 0x0404 comes after "/irb/{ulonglong=...}".
    private const string ResolutionInfoResource = "/irb/{ulonglong=61857348781037}";
    private const string IptcResource = "/irb/{ulonglong=61857348781060}";
    private const string IptcDigestResource = "/irb/{ulonglong=61857348781093}";

    private static readonly (string From, string To)[] s_resourceNames =
    {
        ("/irb/8bimresinfo", ResolutionInfoResource),
        ("/irb/8bimiptc", IptcResource),
        ("/irb/8bimiptcdigest", IptcDigestResource),
    };

    // A JPEG's only IPTC record is the one in its APP13 resource block.
    private static readonly (string From, string To)[] s_jpegAliases =
    {
        ("/app1/ifd", "/ifd"),
        ("/app1/{ushort=0}", "/ifd"),
        // The thumbnail IFD.
        ("/app1/{ushort=1}", "/app1/thumb"),
        ("/app1/xmp", "/xmp"),
        // The photoshop resource block, by name or as APP13 item 0.
        ("/app13/irb", "/irb"),
        ("/app13/{ushort=0}", "/irb"),
        ("/irb/8bimiptc/iptc", "/iptc"),
        (IptcResource + "/iptc", "/iptc"),
    };

    // A TIFF holds IPTC in tag 33723 and, apart from it, in the photoshop resource block of
    // tag 34377.
    private static readonly (string From, string To)[] s_tiffAliases =
    {
        ("/ifd/xmp", "/xmp"),
        ("/ifd/{ushort=700}", "/xmp"),
        ("/ifd/iptc", "/iptc"),
        ("/ifd/{ushort=33723}", "/iptc"),
        ("/ifd/irb", "/irb"),
        ("/ifd/{ushort=34377}", "/irb"),
    };

    private static readonly (string From, string To)[] s_pngAliases =
    {
        ("/iccp/profiledata", "/icc"),
        ("/png/text", "/text"),
        ("/png/ztxt", "/ztxt"),
        ("/png/itxt", "/itxt"),
        ("/[0]text", "/text"),
        ("/[0]ztxt", "/ztxt"),
    };

    // WIC spells these fields in mixed case; queries match them in any case.
    private static readonly string[] s_logicalScreenFields =
    {
        "Signature", "Width", "Height", "GlobalColorTableFlag", "ColorResolution", "SortFlag",
        "GlobalColorTableSize", "BackgroundColorIndex", "PixelAspectRatio",
    };

    private static readonly string[] s_imageDescriptorFields =
    {
        "Left", "Top", "Width", "Height", "LocalColorTableFlag", "InterlaceFlag", "SortFlag", "LocalColorTableSize",
    };

    private static readonly string[] s_graphicControlFields =
    {
        "Disposal", "UserInputFlag", "TransparencyFlag", "Delay", "TransparentColorIndex",
    };

    private static readonly string[] s_applicationFields = { "Application", "Data" };

    private static readonly string[] s_commentFields = { "TextEntry" };

    private static readonly string[] s_textFields = { "Keyword", "TextEntry", "LanguageTag", "TranslatedKeyword" };

    /// <summary>
    /// Returns the canonical form of a query for a container.
    /// </summary>
    /// <exception cref="ArgumentException">The query is not a valid metadata path.</exception>
    public static string Canonicalize(string query, Guid containerFormat)
    {
        var path = Normalize(query);

        if (containerFormat == BitmapContainerFormats.Jpeg)
            path = Rewrite(Rewrite(path, s_jpegAliases), s_resourceNames);
        else if (containerFormat == BitmapContainerFormats.Tiff)
            path = Rewrite(Rewrite(path, s_tiffAliases), s_resourceNames);
        else if (containerFormat == BitmapContainerFormats.Png)
            path = Rewrite(path, s_pngAliases);

        if (containerFormat == BitmapContainerFormats.Jpeg || containerFormat == BitmapContainerFormats.Tiff)
        {
            path = Rewrite(path, s_exifAliases);
            path = CanonicalizeIptc(path, query);
        }
        else if (containerFormat == BitmapContainerFormats.Gif)
        {
            path = CanonicalizeGif(path, query);
        }
        else if (containerFormat == BitmapContainerFormats.Png)
        {
            path = StripTextChunkIndex(path);
            path = CanonicalizeTextField(path, query);
        }

        return path;
    }

    // A tEXt or zTXt index is the chunk's number among the chunks of its type, as in WIC:
    // /[1]text/{str=Author} is chunk 1 if it holds Author. Chunk 0 is spelled without an
    // index. An iTXt keyword selects the entry on its own, so its index adds nothing; the
    // indexed iTXt field view keeps its index.
    private static string StripTextChunkIndex(string path)
    {
        if (!path.StartsWith("/[", StringComparison.Ordinal))
            return path;

        var close = path.IndexOf(']');

        if (close < 0)
            return path;

        var rest = path.Substring(close + 1);

        if (rest.StartsWith("itxt/{str=", StringComparison.Ordinal))
            return "/" + rest;

        if (path.StartsWith("/[0]", StringComparison.Ordinal) &&
            (rest.StartsWith("text/{str=", StringComparison.Ordinal) || rest.StartsWith("ztxt/{str=", StringComparison.Ordinal)))
        {
            return "/" + rest;
        }

        return path;
    }

    /// <summary>
    /// Recognizes a canonical tEXt or zTXt entry, <c>/[n]text/{str=K}</c>, and returns the
    /// chunk it addresses, <c>/[n]text</c> (<c>/text</c> for chunk 0).
    /// </summary>
    public static bool TryGetTextChunk(string path, out string chunk)
    {
        chunk = string.Empty;

        var segments = Split(path);

        if (segments.Count != 2 || !TrySplitIndex(segments[0], out var name, out _) || name is not ("text" or "ztxt") ||
            !TryGetSelector(segments[1], "str", out _))
        {
            return false;
        }

        chunk = "/" + segments[0];
        return true;
    }

    /// <summary>
    /// Recognizes a canonical iTXt field query, <c>/[n]itxt/Keyword</c> and its siblings,
    /// which reads a part of the n-th iTXt entry.
    /// </summary>
    public static bool TryParseTextField(string path, out int index, out string field)
    {
        index = 0;
        field = string.Empty;

        var segments = Split(path);

        if (segments.Count != 2 || !TrySplitIndex(segments[0], out var name, out index) || name != "itxt" ||
            Array.IndexOf(s_textFields, segments[1]) < 0)
        {
            return false;
        }

        field = segments[1];
        return true;
    }

    /// <summary>
    /// Returns whether a canonical path is a GIF flag, which WIC reports as a boolean.
    /// </summary>
    public static bool IsBooleanField(string path)
    {
        var segments = Split(path);

        return segments.Count == 2 && segments[0] is "logscrdesc" or "imgdesc" or "grctlext" &&
            segments[1].EndsWith("Flag", StringComparison.Ordinal);
    }

    // IPTC datasets are named the way WIC names them, whatever the query's case or whether it
    // gives the dataset by number. A TIFF's resource IPTC names them the same way.
    private static string CanonicalizeIptc(string path, string query)
    {
        var block = path.StartsWith(IptcResource + "/iptc/", StringComparison.Ordinal) ? IptcResource : string.Empty;
        var segments = Split(path.Substring(block.Length));

        if (segments.Count != 2 || segments[0] != "iptc")
            return path;

        var segment = segments[1];

        if (TryGetSelector(segment, "ushort", out var number))
        {
            if (!ushort.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                throw new ArgumentException($"The metadata query '{query}' has an IPTC dataset number above 65535.", nameof(query));

            return block + IptcDatasets.GetPath(id);
        }

        var name = TryGetSelector(segment, "str", out var text) ? text : segment;

        if (!IptcDatasets.TryGetId(name, out var named))
            throw new ArgumentException($"The metadata query '{query}' names no IPTC dataset.", nameof(query));

        return block + IptcDatasets.GetPath(named);
    }

    private static string CanonicalizeGif(string path, string query)
    {
        var segments = Split(path);

        if (segments.Count != 2 || !TrySplitIndex(segments[0], out var block, out var index))
            return path;

        var hasIndex = segments[0][0] == '[';
        var fields = block switch
        {
            "logscrdesc" when !hasIndex => s_logicalScreenFields,
            "imgdesc" when !hasIndex => s_imageDescriptorFields,
            "grctlext" when !hasIndex => s_graphicControlFields,
            "commentext" => s_commentFields,
            "appext" => s_applicationFields,
            _ => null,
        };

        if (fields is null)
            return path;

        if (block == "grctlext" && TryGetSelector(segments[1], "ushort", out _))
            return path;

        var field = FindField(fields, TryGetSelector(segments[1], "str", out var text) ? text : segments[1]) ??
            throw new ArgumentException($"The metadata query '{query}' names no {block} field.", nameof(query));
        var prefix = fields == s_commentFields || fields == s_applicationFields
            ? "/[" + index.ToString(CultureInfo.InvariantCulture) + "]" + block
            : "/" + block;

        return prefix + "/" + field;
    }

    // "/iTXt/Keyword" reads the first entry, as "/[0]iTXt/Keyword" does.
    private static string CanonicalizeTextField(string path, string query)
    {
        var segments = Split(path);

        if (segments.Count != 2 || !TrySplitIndex(segments[0], out var block, out var index) || block != "itxt" ||
            segments[1].StartsWith("{", StringComparison.Ordinal))
        {
            return path;
        }

        var field = FindField(s_textFields, segments[1]) ??
            throw new ArgumentException($"The metadata query '{query}' names no iTXt field.", nameof(query));

        return "/[" + index.ToString(CultureInfo.InvariantCulture) + "]itxt/" + field;
    }

    private static string? FindField(string[] fields, string name)
    {
        foreach (var field in fields)
        {
            if (string.Equals(field, name, StringComparison.OrdinalIgnoreCase))
                return field;
        }

        return null;
    }

    private static bool TryGetSelector(string segment, string type, out string value)
    {
        var prefix = "{" + type + "=";

        if (segment.StartsWith(prefix, StringComparison.Ordinal) && segment.EndsWith("}", StringComparison.Ordinal))
        {
            value = segment.Substring(prefix.Length, segment.Length - prefix.Length - 1);
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TrySplitIndex(string segment, out string name, out int index)
    {
        index = 0;
        name = segment;

        if (segment.Length == 0 || segment[0] != '[')
            return true;

        var close = segment.IndexOf(']');

        if (close < 0 || !int.TryParse(segment.AsSpan(1, close - 1), NumberStyles.None, CultureInfo.InvariantCulture, out index))
            return false;

        name = segment.Substring(close + 1);
        return true;
    }

    private static List<string> Split(string path)
    {
        var segments = new List<string>();
        var position = 1;

        while (position < path.Length)
        {
            var end = FindSegmentEnd(path, position);

            segments.Add(path.Substring(position, end - position));
            position = end + 1;
        }

        return segments;
    }

    /// <summary>
    /// Returns whether <paramref name="path"/> lies below <paramref name="block"/>.
    /// </summary>
    public static bool IsBelow(string path, string block)
        => block == "/" ? path.Length > 1 : path.Length > block.Length && path[block.Length] == '/' &&
            path.StartsWith(block, StringComparison.Ordinal);

    /// <summary>
    /// Returns the first segment of <paramref name="path"/> below <paramref name="block"/>.
    /// </summary>
    public static string ChildSegment(string path, string block)
    {
        var start = block == "/" ? 0 : block.Length;
        var end = FindSegmentEnd(path, start + 1);

        return path.Substring(start, end - start);
    }

    /// <summary>
    /// Joins a block path and a relative query.
    /// </summary>
    public static string Combine(string block, string query)
    {
        if (string.IsNullOrEmpty(query) || query == "/")
            return block;

        return block == "/" ? query : block + (query[0] == '/' ? query : "/" + query);
    }

    // Lower-cases names, normalizes item selectors and validates the syntax.
    private static string Normalize(string query)
    {
        if (string.IsNullOrEmpty(query) || query[0] != '/')
            throw new ArgumentException($"The metadata query '{query}' must start with '/'.", nameof(query));

        if (query == "/")
            return "/";

        var builder = new StringBuilder(query.Length);
        var position = 1;
        var parent = string.Empty;

        while (position <= query.Length)
        {
            var end = FindSegmentEnd(query, position);
            var segment = query.Substring(position, end - position);

            if (segment.Length == 0)
                throw new ArgumentException($"The metadata query '{query}' has an empty segment.", nameof(query));

            var normalized = NormalizeSegment(segment, query, parent);

            builder.Append('/').Append(normalized);
            parent = TrySplitIndex(normalized, out var name, out _) ? name : normalized;
            position = end + 1;
        }

        return builder.ToString();
    }

    // Below a PNG text chunk a bare segment is the keyword, taken literally ("/tEXt/Title" is
    // the keyword "Title"); below iTXt the field names are the exception.
    private static string NormalizeSegment(string segment, string query, string parent)
    {
        if (segment[0] is not ('[' or '{') && parent is "text" or "ztxt" or "itxt" &&
            !(parent == "itxt" && FindField(s_textFields, segment) is not null))
        {
            if (segment.IndexOfAny(new[] { '{', '}', '[', ']' }) >= 0)
                throw new ArgumentException($"The metadata query '{query}' has a malformed segment '{segment}'.", nameof(query));

            return "{str=" + segment + "}";
        }

        var index = string.Empty;

        if (segment[0] == '[')
        {
            var close = segment.IndexOf(']');

            if (close < 0 || !int.TryParse(segment.AsSpan(1, close - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                throw new ArgumentException($"The metadata query '{query}' has an invalid index.", nameof(query));

            index = "[" + number.ToString(CultureInfo.InvariantCulture) + "]";
            segment = segment.Substring(close + 1);
        }

        if (segment.Length >= 2 && segment[0] == '{' && segment[segment.Length - 1] == '}')
        {
            if (segment.Length == 2)
                return index + "{}";

            var equals = segment.IndexOf('=');

            if (equals < 0)
                throw new ArgumentException($"The metadata query '{query}' has an item selector without a value.", nameof(query));

            var type = segment.Substring(1, equals - 1).ToLowerInvariant();
            var value = segment.Substring(equals + 1, segment.Length - equals - 2);

            switch (type)
            {
                case "str":
                case "wstr":
                    return index + "{str=" + value + "}";
                case "ushort":
                case "uint":
                case "ulong":
                case "char":
                case "uchar":
                case "short":
                case "int":
                case "long":
                case "ulonglong":
                case "longlong":
                    if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                        throw new ArgumentException($"The metadata query '{query}' has an invalid {type} selector.", nameof(query));

                    return index + "{" + type + "=" + number.ToString(CultureInfo.InvariantCulture) + "}";
                case "guid":
                    if (!Guid.TryParse(value, out var guid))
                        throw new ArgumentException($"The metadata query '{query}' has an invalid guid selector.", nameof(query));

                    return index + "{guid=" + guid.ToString("B") + "}";
                default:
                    throw new ArgumentException($"The metadata query '{query}' has an unknown selector type '{type}'.", nameof(query));
            }
        }

        if (segment.IndexOfAny(new[] { '{', '}', '[', ']' }) >= 0)
            throw new ArgumentException($"The metadata query '{query}' has a malformed segment '{segment}'.", nameof(query));

        return index + segment.ToLowerInvariant();
    }

    // Item selectors may contain '/' inside braces, so a segment ends at the next '/' outside them.
    private static int FindSegmentEnd(string path, int start)
    {
        var depth = 0;

        for (var i = start; i < path.Length; i++)
        {
            switch (path[i])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    break;
                case '/' when depth == 0:
                    return i;
            }
        }

        return path.Length;
    }

    private static string Rewrite(string path, (string From, string To)[] aliases)
    {
        // Rewrites can expose a further alias (/app1/ifd/{ushort=34665} becomes /ifd/{ushort=34665}),
        // so the table is applied until nothing changes.
        for (var changed = true; changed;)
        {
            changed = false;

            foreach (var (from, to) in aliases)
            {
                if (path == from || IsBelow(path, from))
                {
                    path = to + path.Substring(from.Length);
                    changed = true;
                }
            }
        }

        return path;
    }

    /// <summary>
    /// Returns the container name used by <see cref="BitmapMetadata.Format"/>.
    /// </summary>
    public static string GetFormatName(Guid containerFormat)
    {
        foreach (var (format, name) in s_names)
        {
            if (format == containerFormat)
                return name;
        }

        return containerFormat.ToString("D", CultureInfo.InvariantCulture);
    }

    private static readonly List<(Guid, string)> s_names = new()
    {
        (BitmapContainerFormats.Png, "PNG"),
        (BitmapContainerFormats.Jpeg, "JPEG"),
        (BitmapContainerFormats.Tiff, "TIFF"),
        (BitmapContainerFormats.Gif, "GIF"),
        (BitmapContainerFormats.Bmp, "BMP"),
        (BitmapContainerFormats.Webp, "WebP"),
        (BitmapContainerFormats.Ico, "ICO"),
    };
}
