using System;
using Xunit;

namespace Avalonia.Markup.Xaml.UnitTests.Xaml
{
    public class SvgContentTests : XamlTestBase
    {
        private static string Wrap(string content) => $"""
            <Svg xmlns="clr-namespace:Avalonia.Svg;assembly=Avalonia.Svg">{content}</Svg>
            """;

        [Fact]
        public void Cdata_Content_Is_Validated_And_Minified()
        {
            var control = (Avalonia.Svg.Svg)AvaloniaRuntimeXamlLoader.Load(Wrap("""
                <![CDATA[
                  <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                    <!-- pasted from a design tool -->
                    <path d="M12 2 2 7" fill="#3b82f6"/>
                  </svg>
                ]]>
                """));

            Assert.Equal(
                """<svg viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg"><path d="M12 2 2 7" fill="#3b82f6" /></svg>""",
                control.InlineSource);
        }

        [Fact]
        public void Missing_Svg_Namespace_Is_Injected()
        {
            var control = (Avalonia.Svg.Svg)AvaloniaRuntimeXamlLoader.Load(Wrap(
                "<![CDATA[ <svg width='10' height='10'><rect width='10' height='10'/></svg> ]]>"));

            Assert.Equal(
                """<svg width="10" height="10" xmlns="http://www.w3.org/2000/svg"><rect width="10" height="10" /></svg>""",
                control.InlineSource);
        }

        [Fact]
        public void Editor_Cruft_Is_Stripped_And_Xlink_Survives()
        {
            var control = (Avalonia.Svg.Svg)AvaloniaRuntimeXamlLoader.Load(Wrap("""
                <![CDATA[
                  <svg xmlns="http://www.w3.org/2000/svg"
                       xmlns:xlink="http://www.w3.org/1999/xlink"
                       xmlns:inkscape="http://www.inkscape.org/namespaces/inkscape">
                    <sodipodi:namedview xmlns:sodipodi="http://sodipodi.sourceforge.net/DTD/sodipodi-0.0.dtd" id="nv"/>
                    <rect id="r" inkscape:label="Layer" width="5" height="5"/>
                    <use xlink:href="#r"/>
                  </svg>
                ]]>
                """));

            Assert.Equal(
                """<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink"><rect id="r" width="5" height="5" /><use xlink:href="#r" /></svg>""",
                control.InlineSource);
        }

        [Fact]
        public void Text_Content_Whitespace_Is_Preserved()
        {
            var control = (Avalonia.Svg.Svg)AvaloniaRuntimeXamlLoader.Load(Wrap("""
                <![CDATA[
                  <svg xmlns="http://www.w3.org/2000/svg">
                    <text x="1" y="1"> spaced  out </text>
                  </svg>
                ]]>
                """));

            Assert.Equal(
                """<svg xmlns="http://www.w3.org/2000/svg"><text x="1" y="1"> spaced  out </text></svg>""",
                control.InlineSource);
        }

        [Fact]
        public void Malformed_Markup_Fails_The_Compilation()
        {
            var exception = Assert.ThrowsAny<Exception>(() =>
                AvaloniaRuntimeXamlLoader.Load(Wrap("<![CDATA[ <svg><rect </svg> ]]>")));

            Assert.Contains("Invalid inline SVG markup", exception.Message);
        }

        [Fact]
        public void Non_Svg_Root_Fails_The_Compilation()
        {
            var exception = Assert.ThrowsAny<Exception>(() =>
                AvaloniaRuntimeXamlLoader.Load(Wrap("<![CDATA[ <div>nope</div> ]]>")));

            Assert.Contains("must have an <svg> root element", exception.Message);
        }

        [Fact]
        public void Attribute_Syntax_Is_Processed_Too()
        {
            var control = (Avalonia.Svg.Svg)AvaloniaRuntimeXamlLoader.Load(
                """
                <Svg xmlns="clr-namespace:Avalonia.Svg;assembly=Avalonia.Svg"
                     InlineSource="&lt;svg xmlns='http://www.w3.org/2000/svg'&gt;&lt;circle r='4'/&gt;&lt;/svg&gt;"/>
                """);

            Assert.Equal(
                """<svg xmlns="http://www.w3.org/2000/svg"><circle r="4" /></svg>""",
                control.InlineSource);
        }
    }
}
