using System;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Markup.Xaml;

namespace Avalonia.Svg;

/// <summary>
/// Converts XAML string values into <see cref="SvgDocument"/> instances:
/// markup (anything starting with <c>&lt;</c>) parses directly, everything
/// else is treated as a URI and loaded — <c>avares://</c> resources via the
/// asset loader (relative URIs resolve against the XAML base URI) or local
/// files. Documents created here are owned by the consuming control.
/// </summary>
public class SvgDocumentTypeConverter : TypeConverter
{
    /// <inheritdoc />
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        => sourceType == typeof(string) || sourceType == typeof(Uri);

    /// <inheritdoc />
    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is Uri uri)
            return FromUri(uri, context);

        var text = (string)value;
        if (IsMarkup(text))
            return SvgDocument.ParseHostOwned(text, GetBaseUri(context));

        uri = text.StartsWith("/", StringComparison.Ordinal)
            ? new Uri(text, UriKind.Relative)
            : new Uri(text, UriKind.RelativeOrAbsolute);
        return FromUri(uri, context);
    }

    private static bool IsMarkup(string text)
    {
        foreach (var c in text)
        {
            if (c == '<')
                return true;
            if (!char.IsWhiteSpace(c))
                return false;
        }

        return false;
    }

    private static SvgDocument FromUri(Uri uri, ITypeDescriptorContext? context)
    {
        if (!uri.IsAbsoluteUri)
        {
            var baseUri = GetBaseUri(context)
                ?? throw new InvalidOperationException(
                    $"Cannot resolve the relative SVG source '{uri}' without a base URI.");
            uri = new Uri(baseUri, uri);
        }

        return SvgDocument.LoadHostOwned(uri);
    }

    private static Uri? GetBaseUri(ITypeDescriptorContext? context)
        => (context?.GetService(typeof(IUriContext)) as IUriContext)?.BaseUri;
}
