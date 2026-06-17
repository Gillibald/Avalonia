using System;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Avalonia.Markup.Xaml.Converters
{
    public class BitmapTypeConverter : TypeConverter
    {
        /// <inheritdoc />
        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        {
            return sourceType == typeof(string);
        }

        /// <inheritdoc />
        public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        {
            var s = (string)value;
            var uri = s.StartsWith("/")
                ? new Uri(s, UriKind.Relative)
                : new Uri(s, UriKind.RelativeOrAbsolute);

            // Vector sources resolve to an SvgImage; everything else is a raster Bitmap.
            // (Both implement IImage, so Image.Source accepts either.)
            if (IsSvgSource(uri))
                return SvgImage.Load(uri, context?.GetContextBaseUri());

            if(uri.IsAbsoluteUri && uri.IsFile)
                return new Bitmap(uri.LocalPath);

            var assets = AvaloniaLocator.Current.GetRequiredService<IAssetLoader>();
            return new Bitmap(assets.Open(uri, context?.GetContextBaseUri()));
        }

        private static bool IsSvgSource(Uri uri)
        {
            var path = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString;
            return path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
        }
    }
}
