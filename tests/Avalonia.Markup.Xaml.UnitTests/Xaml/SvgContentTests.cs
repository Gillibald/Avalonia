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

        [Fact]
        public void Inline_Element_Content_Compiles_Without_Cdata()
        {
            // The verbatim form of Cdata_Content_Compiles_Into_A_Document: the
            // <svg> subtree is pasted directly, with no CDATA wrapper.
            var control = (Avalonia.Svg.Svg)AvaloniaRuntimeXamlLoader.Load(Wrap("""
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                  <!-- pasted from a design tool -->
                  <path id="hill" d="M12 2 2 7" fill="#3b82f6"/>
                </svg>
                """));

            var document = Assert.IsType<Avalonia.Svg.SvgDocument>(control.Source);
            Assert.Equal("svg", document.Root.Name);
            Assert.Equal("0 0 24 24", document.Root.GetAttribute("viewBox"));
            Assert.Equal("#3b82f6", document.GetElementById("hill")!.GetAttribute("fill"));
        }

        [Fact]
        public void Inline_Element_With_Defs_And_Gradient_Compiles()
        {
            var control = (Avalonia.Svg.Svg)AvaloniaRuntimeXamlLoader.Load(Wrap("""
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">
                  <defs>
                    <linearGradient id="sky" x1="0" y1="0" x2="0" y2="1">
                      <stop offset="0" stop-color="#7dd3fc"/>
                      <stop offset="1" stop-color="#0ea5e9"/>
                    </linearGradient>
                  </defs>
                  <rect width="64" height="64" rx="12" fill="url(#sky)"/>
                  <text x="32" y="61" font-size="5" text-anchor="middle" fill="#0c4a6e">inline svg</text>
                </svg>
                """));

            var document = control.Source!;
            Assert.Equal("svg", document.Root.Name);
            Assert.Equal("linearGradient", document.GetElementById("sky")!.Name);
        }

        [Fact]
        public void Inline_Element_Allows_Nested_Xmlns_And_Preserves_Xlink()
        {
            // A nested xmlns declaration on the inner <svg> is exactly what XamlX's
            // parser rejects on non-root elements — the reason CDATA was required.
            // Capturing the subtree verbatim sidesteps that and keeps xlink intact.
            var control = (Avalonia.Svg.Svg)AvaloniaRuntimeXamlLoader.Load(Wrap("""
                <svg xmlns="http://www.w3.org/2000/svg"
                     xmlns:xlink="http://www.w3.org/1999/xlink">
                  <rect id="r" width="5" height="5"/>
                  <use id="u" xlink:href="#r"/>
                </svg>
                """));

            Assert.Equal("#r", control.Source!.GetElementById("u")!.GetAttribute("xlink:href"));
        }

        [Fact]
        public void Inline_Element_Matches_The_Cdata_Form()
        {
            const string markup = """
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                  <path id="hill" d="M12 2 2 7" fill="#3b82f6"/>
                </svg>
                """;

            var inline = (Avalonia.Svg.Svg)AvaloniaRuntimeXamlLoader.Load(Wrap(markup));
            var cdata = (Avalonia.Svg.Svg)AvaloniaRuntimeXamlLoader.Load(Wrap($"<![CDATA[{markup}]]>"));

            Assert.Equal(cdata.Source!.Root.GetAttribute("viewBox"), inline.Source!.Root.GetAttribute("viewBox"));
            Assert.Equal(
                cdata.Source!.GetElementById("hill")!.GetAttribute("fill"),
                inline.Source!.GetElementById("hill")!.GetAttribute("fill"));
        }
    }
}
