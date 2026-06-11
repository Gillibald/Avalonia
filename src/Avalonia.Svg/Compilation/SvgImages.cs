using System;
using System.IO;
using System.IO.Compression;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Parsing;

namespace Avalonia.Svg.Compilation;

/// <summary>
/// Compiles <c>&lt;image&gt;</c> elements: raster content decodes to a
/// <see cref="Bitmap"/> and draws through <c>DrawImage</c>; SVG content parses
/// to a nested document compiled inline. Content comes from <c>data:</c> URIs
/// or files resolved against <see cref="SvgDocument.BaseUri"/>, decoded once
/// and cached on the document.
/// </summary>
internal static class SvgImages
{
    private const int MaxNestedDepth = 4;

    [ThreadStatic]
    private static int t_nestedDepth;

    public static void Compile(
        SvgElement element, DrawingContext context, SvgCompileContext compileContext, in SvgStyle style)
    {
        var href = element.Href;
        if (href is not { Length: > 0 })
            return;

        var content = compileContext.Document.GetImageContent(href, LoadContent);
        double intrinsicWidth;
        double intrinsicHeight;
        switch (content)
        {
            case Bitmap bitmap:
                intrinsicWidth = bitmap.PixelSize.Width;
                intrinsicHeight = bitmap.PixelSize.Height;
                break;
            case SvgDocument nested:
                var intrinsic = nested.GetIntrinsicSize();
                intrinsicWidth = intrinsic.Width;
                intrinsicHeight = intrinsic.Height;
                break;
            default:
                return;
        }

        if (intrinsicWidth <= 0 || intrinsicHeight <= 0)
            return;

        // Missing width/height are 'auto', per the SVG 2 sizing rules: both
        // auto takes the intrinsic size; one auto preserves the intrinsic
        // aspect ratio against the other.
        var x = GetLength(element, "x", SvgLengthAxis.Horizontal, style);
        var y = GetLength(element, "y", SvgLengthAxis.Vertical, style);
        var explicitWidth = GetOptionalLength(element, "width", SvgLengthAxis.Horizontal, style);
        var explicitHeight = GetOptionalLength(element, "height", SvgLengthAxis.Vertical, style);

        var width = explicitWidth
                    ?? (explicitHeight is { } h ? h * (intrinsicWidth / intrinsicHeight) : intrinsicWidth);
        var height = explicitHeight
                     ?? (explicitWidth is { } w ? w * (intrinsicHeight / intrinsicWidth) : intrinsicHeight);
        if (width <= 0 || height <= 0)
            return;

        var preserveAspectRatio = SvgPreserveAspectRatio.Default;
        if (element.GetAttribute("preserveAspectRatio") is { } par)
            SvgPreserveAspectRatio.TryParse(par.AsSpan(), out preserveAspectRatio);

        var contentMatrix = preserveAspectRatio.ComputeTransform(
            new SvgViewBox(0, 0, intrinsicWidth, intrinsicHeight), new Size(width, height));

        // The image viewport clips its content (slice and stretched cases).
        using (context.PushClip(new Rect(x, y, width, height)))
        using (context.PushTransform(Matrix.CreateTranslation(x, y)))
        {
            // image-rendering speed hints opt out of smooth interpolation.
            DrawingContext.PushedState? renderOptions = null;
            if (element.GetStyleOrAttribute("image-rendering") is "optimizeSpeed" or "pixelated" or "crisp-edges")
            {
                renderOptions = context.PushRenderOptions(new RenderOptions
                {
                    BitmapInterpolationMode = BitmapInterpolationMode.None,
                });
            }

            using (renderOptions)
            {
                if (content is Bitmap image)
                {
                    // preserveAspectRatio maps axis-aligned: the destination is
                    // the transformed intrinsic rect.
                    var destination = new Rect(
                        contentMatrix.M31,
                        contentMatrix.M32,
                        intrinsicWidth * contentMatrix.M11,
                        intrinsicHeight * contentMatrix.M22);
                    context.DrawImage(image, new Rect(image.Size), destination);
                }
                else if (content is SvgDocument nestedDocument && t_nestedDepth < MaxNestedDepth)
                {
                    t_nestedDepth++;
                    try
                    {
                        using (context.PushTransform(contentMatrix))
                            SvgCompiler.CompileDocument(nestedDocument, context,
                                new Size(intrinsicWidth, intrinsicHeight));
                    }
                    finally
                    {
                        t_nestedDepth--;
                    }
                }
            }
        }

        compileContext.HitTree?.AddShape(
            element,
            new SvgHitShape
            {
                Kind = SvgHitShape.ShapeKind.Rectangle,
                Bounds = new Rect(x, y, width, height),
                HasFill = true,
            },
            style.PointerEvents,
            style.Visible);
    }

    /// <summary>
    /// Loads image content from a <c>data:</c> URI or a file next to the
    /// document. Returns a <see cref="Bitmap"/>, a nested
    /// <see cref="SvgDocument"/>, or null when unresolvable (remote
    /// references are never fetched).
    /// </summary>
    private static object? LoadContent(SvgDocument document, string href)
    {
        try
        {
            if (href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return DecodeDataUri(href);

            if (href.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (document.BaseUri is not { } baseUri)
                return null;

            var resolved = new Uri(baseUri, href);
            if (!resolved.IsFile || !File.Exists(resolved.LocalPath))
                return null;

            return Decode(File.ReadAllBytes(resolved.LocalPath), resolved);
        }
        catch (Exception ex) when (ex is IOException or FormatException or ArgumentException or UriFormatException)
        {
            return null;
        }
    }

    private static object? DecodeDataUri(string href)
    {
        var comma = href.IndexOf(',');
        if (comma < 0)
            return null;

        var header = href.Substring(5, comma - 5);
        var data = href.Substring(comma + 1);

        byte[] bytes;
        if (header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                bytes = Convert.FromBase64String(data.Trim());
            }
            catch (FormatException)
            {
                return null;
            }
        }
        else
        {
            bytes = System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(data));
        }

        return Decode(bytes, source: null);
    }

    private static object? Decode(byte[] bytes, Uri? source)
    {
        if (bytes.Length < 4)
            return null;

        // gzip (svgz) unwraps first.
        if (bytes[0] == 0x1f && bytes[1] == 0x8b)
        {
            using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var unpacked = new MemoryStream();
            gzip.CopyTo(unpacked);
            bytes = unpacked.ToArray();
        }

        // SVG content sniffs as markup; anything else goes to the bitmap decoder.
        if (LooksLikeSvg(bytes))
        {
            try
            {
                return SvgDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes), source);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        try
        {
            return new Bitmap(new MemoryStream(bytes));
        }
        catch (Exception)
        {
            // The platform decoder rejects unknown formats with varying
            // exception types; an undecodable image renders nothing.
            return null;
        }
    }

    private static bool LooksLikeSvg(byte[] bytes)
    {
        // Skip BOM and leading whitespace; SVG starts with '<'.
        var start = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            start = 3;
        while (start < bytes.Length && bytes[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            start++;
        return start < bytes.Length && bytes[start] == (byte)'<';
    }

    private static double GetLength(
        SvgElement element, string name, SvgLengthAxis axis, in SvgStyle style)
        => GetOptionalLength(element, name, axis, style) ?? 0;

    private static double? GetOptionalLength(
        SvgElement element, string name, SvgLengthAxis axis, in SvgStyle style)
    {
        var value = element.GetAnimatedOrAttribute(name);
        if (value != null && value != "auto" && SvgLength.TryParse(value.AsSpan(), out var length))
            return style.ResolveLength(length, axis);
        return null;
    }
}
