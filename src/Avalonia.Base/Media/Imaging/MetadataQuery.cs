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

    private static readonly (string From, string To)[] s_jpegAliases =
    {
        ("/app1/ifd", "/ifd"),
        ("/app1/{ushort=0}", "/ifd"),
        ("/app1/xmp", "/xmp"),
        ("/app13/irb/8bimiptc/iptc", "/iptc"),
    };

    private static readonly (string From, string To)[] s_tiffAliases =
    {
        ("/ifd/xmp", "/xmp"),
        ("/ifd/{ushort=700}", "/xmp"),
        ("/ifd/iptc", "/iptc"),
        ("/ifd/{ushort=33723}", "/iptc"),
    };

    private static readonly (string From, string To)[] s_pngAliases =
    {
        ("/iccp/profiledata", "/icc"),
    };

    /// <summary>
    /// Returns the canonical form of a query for a container.
    /// </summary>
    /// <exception cref="ArgumentException">The query is not a valid metadata path.</exception>
    public static string Canonicalize(string query, Guid containerFormat)
    {
        var path = Normalize(query);

        if (containerFormat == BitmapContainerFormats.Jpeg)
            path = Rewrite(path, s_jpegAliases);
        else if (containerFormat == BitmapContainerFormats.Tiff)
            path = Rewrite(path, s_tiffAliases);
        else if (containerFormat == BitmapContainerFormats.Png)
            path = Rewrite(path, s_pngAliases);

        if (containerFormat == BitmapContainerFormats.Jpeg || containerFormat == BitmapContainerFormats.Tiff)
            path = Rewrite(path, s_exifAliases);

        return path;
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

        while (position <= query.Length)
        {
            var end = FindSegmentEnd(query, position);
            var segment = query.Substring(position, end - position);

            if (segment.Length == 0)
                throw new ArgumentException($"The metadata query '{query}' has an empty segment.", nameof(query));

            builder.Append('/').Append(NormalizeSegment(segment, query));
            position = end + 1;
        }

        return builder.ToString();
    }

    private static string NormalizeSegment(string segment, string query)
    {
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
                    if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                        throw new ArgumentException($"The metadata query '{query}' has an invalid {type} selector.", nameof(query));

                    return index + "{" + type + "=" + number.ToString(CultureInfo.InvariantCulture) + "}";
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
