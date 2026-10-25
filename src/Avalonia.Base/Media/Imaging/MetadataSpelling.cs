using System;
using System.Collections.Generic;

namespace Avalonia.Media.Imaging;

/// <summary>
/// Spells canonical metadata paths the way WIC enumerates them for a container: IFD blocks
/// by their pointer tags under the container's segments, PNG chunks and fields in their own
/// case, repeated PNG text chunks numbered and iTXt entries split into their fields.
/// </summary>
internal static class MetadataSpelling
{
    // Most specific first; the first matching prefix is rewritten.
    private static readonly (string Canonical, string Container)[] s_jpeg =
    {
        ("/ifd/exif/interop", "/app1/{ushort=0}/{ushort=34665}/{ushort=40965}"),
        ("/ifd/exif", "/app1/{ushort=0}/{ushort=34665}"),
        ("/ifd/gps", "/app1/{ushort=0}/{ushort=34853}"),
        ("/ifd", "/app1/{ushort=0}"),
        ("/app1/thumb", "/app1/{ushort=1}"),
        ("/iptc", "/app13/{ushort=0}/{ulonglong=61857348781060}/iptc"),
    };

    private static readonly (string Canonical, string Container)[] s_tiff =
    {
        ("/ifd/exif/interop", "/ifd/{ushort=34665}/{ushort=40965}"),
        ("/ifd/exif", "/ifd/{ushort=34665}"),
        ("/ifd/gps", "/ifd/{ushort=34853}"),
        ("/iptc", "/ifd/{ushort=33723}"),
        ("/xmp", "/ifd/{ushort=700}"),
    };

    private static readonly Dictionary<string, string> s_pngChunks = new(StringComparer.Ordinal)
    {
        ["text"] = "tEXt", ["ztxt"] = "zTXt", ["itxt"] = "iTXt", ["gama"] = "gAMA", ["chrm"] = "cHRM",
        ["srgb"] = "sRGB", ["phys"] = "pHYs", ["time"] = "tIME", ["bkgd"] = "bKGD", ["hist"] = "hIST",
        ["sbit"] = "sBIT", ["iccp"] = "iCCP",
    };

    // The field names of WIC's metadata readers; canonical paths hold them in lower case.
    private static readonly Dictionary<string, string> s_fields = Create(
        "TableEntry", "TextEntry", "ImageGamma", "WhitePointX", "WhitePointY", "RedX", "RedY", "GreenX",
        "GreenY", "BlueX", "BlueY", "BackgroundColor", "Frequencies", "ProfileName", "ProfileData",
        "RenderingIntent", "Year", "Month", "Day", "Hour", "Minute", "Second", "PixelsPerUnitX",
        "PixelsPerUnitY", "UnitSpecifier");

    // WIC also lists CompressionFlag, which no metadata store keeps, so it is not listed here.
    private static readonly string[] s_textFields = { "Keyword", "LanguageTag", "TranslatedKeyword", "TextEntry" };

    /// <summary>
    /// Spells every stored path for the container. PNG text chunks are numbered in the order
    /// the store lists them, which is the order the iTXt field view counts them in.
    /// </summary>
    public static List<string> ToContainer(IEnumerable<string> canonicalPaths, Guid containerFormat)
    {
        var spelled = new List<string>();

        if (containerFormat != BitmapContainerFormats.Png)
        {
            foreach (var path in canonicalPaths)
            {
                // Outside PNG the profile belongs to the frame; WIC lists no metadata for it.
                if (path != "/icc")
                    spelled.Add(ToContainer(path, containerFormat));
            }

            return spelled;
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var path in canonicalPaths)
        {
            var family = TextFamily(path);

            if (family is null)
            {
                spelled.Add(ToContainer(path, containerFormat));
                continue;
            }

            counts.TryGetValue(family, out var index);
            counts[family] = index + 1;

            var block = index == 0 ? "/" + family : "/[" + index + "]" + family;

            if (family == "iTXt")
            {
                foreach (var field in s_textFields)
                    spelled.Add(block + "/" + field);
            }
            else
            {
                spelled.Add(block + path.Substring(path.IndexOf('/', 1)));
            }
        }

        return spelled;
    }

    /// <summary>
    /// Spells one canonical path or block for the container. A PNG text entry keeps its
    /// keyword selector, which resolves without the chunk number.
    /// </summary>
    public static string ToContainer(string canonicalPath, Guid containerFormat)
    {
        if (containerFormat == BitmapContainerFormats.Png)
        {
            if (canonicalPath == "/icc")
                return "/iCCP/ProfileData";

            canonicalPath = SpellPngChunk(canonicalPath);
        }
        else
        {
            var blocks = containerFormat == BitmapContainerFormats.Jpeg ? s_jpeg
                : containerFormat == BitmapContainerFormats.Tiff ? s_tiff
                : Array.Empty<(string, string)>();

            foreach (var (canonical, container) in blocks)
            {
                if (canonicalPath == canonical || MetadataQuery.IsBelow(canonicalPath, canonical))
                {
                    canonicalPath = container + canonicalPath.Substring(canonical.Length);
                    break;
                }
            }
        }

        return SpellFields(canonicalPath);
    }

    private static string? TextFamily(string canonicalPath)
    {
        foreach (var (canonical, family) in new[] { ("/text/", "tEXt"), ("/ztxt/", "zTXt"), ("/itxt/", "iTXt") })
        {
            if (canonicalPath.StartsWith(canonical, StringComparison.Ordinal))
                return family;
        }

        return null;
    }

    private static string SpellPngChunk(string path)
    {
        var end = path.IndexOf('/', 1);
        var first = end < 0 ? path.Substring(1) : path.Substring(1, end - 1);
        var index = string.Empty;

        if (first.StartsWith("[", StringComparison.Ordinal) && first.IndexOf(']') is var close and > 0)
        {
            index = first.Substring(0, close + 1);
            first = first.Substring(close + 1);
        }

        return s_pngChunks.TryGetValue(first, out var chunk)
            ? "/" + index + chunk + (end < 0 ? string.Empty : path.Substring(end))
            : path;
    }

    // Selectors keep their spelling; plain segments that name a reader field take its case.
    private static string SpellFields(string path)
    {
        var segments = path.Split('/');

        for (var i = 1; i < segments.Length; i++)
        {
            if (segments[i].Length > 0 && segments[i][0] is not ('{' or '[') && s_fields.TryGetValue(segments[i], out var field))
                segments[i] = field;
        }

        return string.Join("/", segments);
    }

    private static Dictionary<string, string> Create(params string[] names)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in names)
            map[name.ToLowerInvariant()] = name;

        return map;
    }
}
