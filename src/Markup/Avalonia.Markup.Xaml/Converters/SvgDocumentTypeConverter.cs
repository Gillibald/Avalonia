using System;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Media.Svg;

namespace Avalonia.Markup.Xaml.Converters
{
    /// <summary>
    /// Converts XAML string/URI values into <see cref="SvgDocument"/> instances
    /// for <c>SvgControl.Source</c>: markup (anything starting with <c>&lt;</c>)
    /// parses directly, everything else loads as a URI — <c>avares://</c>
    /// resources via the asset loader or local files.
    /// </summary>
    /// <remarks>
    /// This lives in Avalonia.Markup.Xaml rather than Avalonia.Svg because it
    /// needs the XAML <see cref="IUriContext"/> to resolve relative sources. It
    /// reads the base URI here and passes it to Avalonia.Svg as a plain value, so
    /// that assembly takes no dependency on the markup assembly.
    /// </remarks>
    public class SvgDocumentTypeConverter : TypeConverter
    {
        /// <inheritdoc />
        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
            => sourceType == typeof(string) || sourceType == typeof(Uri);

        /// <inheritdoc />
        public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        {
            var baseUri = context?.GetContextBaseUri();
            return value is Uri uri
                ? SvgDocument.FromXamlUri(uri, baseUri)
                : SvgDocument.FromXamlSource((string)value, baseUri);
        }
    }
}
