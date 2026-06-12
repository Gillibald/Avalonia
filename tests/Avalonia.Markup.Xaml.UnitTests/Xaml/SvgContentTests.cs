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
        public void Cdata_Content_Compiles_Into_A_Document()
        {
            var control = (Avalonia.Svg.Svg)AvaloniaRuntimeXamlLoader.Load(Wrap("""
                <![CDATA[
                  <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                    <!-- pasted from a design tool -->
                    <path id="hill" d="M12 2 2 7" fill="#3b82f6"/>
                  </svg>
                ]]>
                """));

            var document = Assert.IsType<Avalonia.Svg.SvgDocument>(control.Source);
            Assert.Equal("svg", document.Root.Name);
            Assert.Equal("0 0 24 24", document.Root.GetAttribute("viewBox"));
            Assert.Equal("#3b82f6", document.GetElementById("hill")!.GetAttribute("fill"));
        }

        [Fact]
        public void Missing_Svg_Namespace_Is_Injected()
        {
            var control = (Avalonia.Svg.Svg)AvaloniaRuntimeXamlLoader.Load(Wrap(
                "<![CDATA[ <svg width='10' height='10'><rect id='r' width='10' height='10'/></svg> ]]>"));

            Assert.NotNull(control.Source);
            Assert.Equal("rect", control.Source!.GetElementById("r")!.Name);
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
                    <use id="u" xlink:href="#r"/>
                  </svg>
                ]]>
                """));

            var document = control.Source!;
            Assert.Null(document.GetElementById("nv"));
            Assert.Null(document.GetElementById("r")!.GetAttribute("inkscape:label"));
            Assert.Equal("#r", document.GetElementById("u")!.GetAttribute("xlink:href"));
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
        public void Attribute_Syntax_Compiles_Markup_Too()
        {
            var control = (Avalonia.Svg.Svg)AvaloniaRuntimeXamlLoader.Load(
                """
                <Svg xmlns="clr-namespace:Avalonia.Svg;assembly=Avalonia.Svg"
                     Source="&lt;svg xmlns='http://www.w3.org/2000/svg'&gt;&lt;circle id='c' r='4'/&gt;&lt;/svg&gt;"/>
                """);

            Assert.Equal("circle", control.Source!.GetElementById("c")!.Name);
        }
    }
}
