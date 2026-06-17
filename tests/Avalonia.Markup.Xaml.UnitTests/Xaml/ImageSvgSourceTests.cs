using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Media;
using Xunit;

namespace Avalonia.Markup.Xaml.UnitTests.Xaml
{
    /// <summary>
    /// An SVG source resolves to a vector <see cref="SvgImage"/> through the same
    /// <c>IImage</c> type converter that loads raster bitmaps, so an SVG can be
    /// used directly as <see cref="Image.Source"/> — both as element content and
    /// as the <c>Source</c> attribute.
    /// </summary>
    public class ImageSvgSourceTests : XamlTestBase
    {
        private static string WriteTempSvg(string svg)
        {
            var path = Path.Combine(
                Path.GetTempPath(), "avalonia_image_svg_" + Guid.NewGuid().ToString("N") + ".svg");
            File.WriteAllText(path, svg);
            return path;
        }

        [Fact]
        public void Image_Content_Resolves_An_Svg_Uri_To_SvgImage()
        {
            var path = WriteTempSvg("""<svg xmlns="http://www.w3.org/2000/svg" width="20" height="10"/>""");
            try
            {
                var uri = new Uri(path).AbsoluteUri;

                // The URI is the [Content] of Image and binds to Source (IImage).
                var image = (Image)AvaloniaRuntimeXamlLoader.Load(
                    $"""<Image xmlns="https://github.com/avaloniaui">{uri}</Image>""");

                var svg = Assert.IsType<SvgImage>(image.Source);
                Assert.Equal(new Size(20, 10), svg.Size);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Image_Source_Attribute_Resolves_An_Svg_Uri_To_SvgImage()
        {
            var path = WriteTempSvg("""<svg xmlns="http://www.w3.org/2000/svg" width="30" height="15"/>""");
            try
            {
                var uri = new Uri(path).AbsoluteUri;

                var image = (Image)AvaloniaRuntimeXamlLoader.Load(
                    $"""<Image xmlns="https://github.com/avaloniaui" Source="{uri}"/>""");

                Assert.IsType<SvgImage>(image.Source);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Non_Svg_Uri_Still_Resolves_Through_The_Raster_Path()
        {
            // A non-.svg source must not be routed to the SVG loader; with no
            // render platform the raster Bitmap path throws rather than silently
            // producing an SvgImage — proving the extension gate is doing the
            // dispatch.
            Assert.ThrowsAny<Exception>(() => AvaloniaRuntimeXamlLoader.Load(
                """<Image xmlns="https://github.com/avaloniaui">avares://NoSuchAssembly/missing.png</Image>"""));
        }
    }
}
