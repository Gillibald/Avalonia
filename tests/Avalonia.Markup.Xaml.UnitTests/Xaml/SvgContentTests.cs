using System;
using System.Collections.Generic;
using Xunit;

namespace Avalonia.Markup.Xaml.UnitTests.Xaml
{
    public class SvgContentTests : XamlTestBase
    {
        private static string Wrap(string content) => $"""
            <SvgControl xmlns="clr-namespace:Avalonia.Controls;assembly=Avalonia.Svg">{content}</SvgControl>
            """;

        private static Avalonia.Controls.SvgControl LoadCapturingDiagnostics(
            string content, out List<RuntimeXamlDiagnostic> diagnostics)
        {
            var captured = new List<RuntimeXamlDiagnostic>();
            var control = (Avalonia.Controls.SvgControl)AvaloniaRuntimeXamlLoader.Load(
                new RuntimeXamlLoaderDocument(Wrap(content)),
                new RuntimeXamlLoaderConfiguration
                {
                    DiagnosticHandler = diagnostic =>
                    {
                        captured.Add(diagnostic);
                        return diagnostic.Severity;
                    }
                });
            diagnostics = captured;
            return control;
        }

        [Fact]
        public void Cdata_Content_Compiles_Into_A_Document()
        {
            var control = (Avalonia.Controls.SvgControl)AvaloniaRuntimeXamlLoader.Load(Wrap("""
                <![CDATA[
                  <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                    <!-- pasted from a design tool -->
                    <path id="hill" d="M12 2 2 7" fill="#3b82f6"/>
                  </svg>
                ]]>
                """));

            var document = Assert.IsType<Avalonia.Media.Svg.SvgDocument>(control.Source);
            Assert.Equal("svg", document.Root.Name);
            Assert.Equal("0 0 24 24", document.Root.GetAttribute("viewBox"));
            Assert.Equal("#3b82f6", document.GetElementById("hill")!.GetAttribute("fill"));
        }

        [Fact]
        public void Missing_Svg_Namespace_Is_Injected()
        {
            var control = (Avalonia.Controls.SvgControl)AvaloniaRuntimeXamlLoader.Load(Wrap(
                "<![CDATA[ <svg width='10' height='10'><rect id='r' width='10' height='10'/></svg> ]]>"));

            Assert.NotNull(control.Source);
            Assert.Equal("rect", control.Source!.GetElementById("r")!.Name);
        }

        [Fact]
        public void Editor_Cruft_Is_Stripped_And_Xlink_Survives()
        {
            var control = (Avalonia.Controls.SvgControl)AvaloniaRuntimeXamlLoader.Load(Wrap("""
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
            var control = (Avalonia.Controls.SvgControl)AvaloniaRuntimeXamlLoader.Load(
                """
                <SvgControl xmlns="clr-namespace:Avalonia.Controls;assembly=Avalonia.Svg"
                            Source="&lt;svg xmlns='http://www.w3.org/2000/svg'&gt;&lt;circle id='c' r='4'/&gt;&lt;/svg&gt;"/>
                """);

            Assert.Equal("circle", control.Source!.GetElementById("c")!.Name);
        }

        [Fact]
        public void Inline_Element_Content_Compiles_Without_Cdata()
        {
            // The verbatim form of Cdata_Content_Compiles_Into_A_Document: the
            // <svg> subtree is pasted directly, with no CDATA wrapper.
            var control = (Avalonia.Controls.SvgControl)AvaloniaRuntimeXamlLoader.Load(Wrap("""
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                  <!-- pasted from a design tool -->
                  <path id="hill" d="M12 2 2 7" fill="#3b82f6"/>
                </svg>
                """));

            var document = Assert.IsType<Avalonia.Media.Svg.SvgDocument>(control.Source);
            Assert.Equal("svg", document.Root.Name);
            Assert.Equal("0 0 24 24", document.Root.GetAttribute("viewBox"));
            Assert.Equal("#3b82f6", document.GetElementById("hill")!.GetAttribute("fill"));
        }

        [Fact]
        public void Inline_Element_With_Defs_And_Gradient_Compiles()
        {
            var control = (Avalonia.Controls.SvgControl)AvaloniaRuntimeXamlLoader.Load(Wrap("""
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
            var control = (Avalonia.Controls.SvgControl)AvaloniaRuntimeXamlLoader.Load(Wrap("""
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

            var inline = (Avalonia.Controls.SvgControl)AvaloniaRuntimeXamlLoader.Load(Wrap(markup));
            var cdata = (Avalonia.Controls.SvgControl)AvaloniaRuntimeXamlLoader.Load(Wrap($"<![CDATA[{markup}]]>"));

            Assert.Equal(cdata.Source!.Root.GetAttribute("viewBox"), inline.Source!.Root.GetAttribute("viewBox"));
            Assert.Equal(
                cdata.Source!.GetElementById("hill")!.GetAttribute("fill"),
                inline.Source!.GetElementById("hill")!.GetAttribute("fill"));
        }

        [Fact]
        public void Uri_Source_Loads_The_Resource_Rather_Than_Parsing_The_Uri()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "svgcontrol-uri-" + Guid.NewGuid().ToString("N") + ".svg");
            System.IO.File.WriteAllText(path,
                """<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><rect id="r" width="10" height="10"/></svg>""");
            try
            {
                var uri = new Uri(path).AbsoluteUri;

                // Source is a URI string, not markup, so it must load the resource.
                // Treating it as markup would feed the URI to SvgDocument.Parse and
                // the element would be missing (or parsing would throw).
                var control = (Avalonia.Controls.SvgControl)AvaloniaRuntimeXamlLoader.Load(
                    $"""<SvgControl xmlns="clr-namespace:Avalonia.Controls;assembly=Avalonia.Svg" Source="{uri}"/>""");

                Assert.NotNull(control.Source);
                Assert.NotNull(control.Source!.GetElementById("r"));
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }

        [Fact]
        public void SvgImage_Resource_Is_A_Shared_Image()
        {
            // A .svg URI declared as an <SvgImage> resource resolves to one
            // SvgImage that several Image controls can share — the registration
            // that lets a single document drive several Image.Source bindings.
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "svgimage-res-" + Guid.NewGuid().ToString("N") + ".svg");
            System.IO.File.WriteAllText(path,
                """<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><rect width="10" height="10"/></svg>""");
            try
            {
                var uri = new Uri(path).AbsoluteUri;
                var panel = (Avalonia.Controls.StackPanel)AvaloniaRuntimeXamlLoader.Load(
                    $$"""
                    <StackPanel xmlns="https://github.com/avaloniaui"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                      <StackPanel.Resources>
                        <SvgImage x:Key="Shared">{{uri}}</SvgImage>
                      </StackPanel.Resources>
                      <Image Source="{StaticResource Shared}"/>
                      <Image Source="{StaticResource Shared}"/>
                    </StackPanel>
                    """);

                var first = (Avalonia.Controls.Image)panel.Children[0];
                var second = (Avalonia.Controls.Image)panel.Children[1];
                var image = Assert.IsType<Avalonia.Media.SvgImage>(first.Source);
                Assert.Same(image, second.Source);
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }

        [Fact]
        public void Unresolved_Url_Reference_Reports_A_Warning()
        {
            // The markup is valid SVG and still loads; the broken paint reference
            // is surfaced as a warning the renderer otherwise paints as nothing.
            var control = LoadCapturingDiagnostics("""
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                  <rect width="24" height="24" fill="url(#missing)"/>
                </svg>
                """, out var diagnostics);

            Assert.NotNull(control.Source);
            Assert.Contains(diagnostics, d =>
                d.Id == "AVLN2209"
                && d.Severity == RuntimeXamlDiagnosticSeverity.Warning
                && d.Title.Contains("#missing"));
        }

        [Fact]
        public void Unresolved_Href_Reference_Reports_A_Warning()
        {
            LoadCapturingDiagnostics("""
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                  <use href="#nope"/>
                </svg>
                """, out var diagnostics);

            Assert.Contains(diagnostics, d => d.Id == "AVLN2209" && d.Title.Contains("#nope"));
        }

        [Fact]
        public void Forward_And_Resolved_References_Report_No_Warning()
        {
            // The <use> precedes its target, and the gradient is referenced before
            // it is declared — both are valid (ids are collected document-wide).
            LoadCapturingDiagnostics("""
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                  <rect width="24" height="24" fill="url(#sky)"/>
                  <use href="#dot"/>
                  <defs>
                    <linearGradient id="sky"><stop offset="0" stop-color="#000"/></linearGradient>
                    <circle id="dot" r="2"/>
                  </defs>
                </svg>
                """, out var diagnostics);

            Assert.DoesNotContain(diagnostics, d => d.Id == "AVLN2209");
        }

        [Fact]
        public void Paint_Fallback_Does_Not_Report_A_Warning()
        {
            // 'url(#x) red' falls back to red when #x is absent, so it is not a
            // broken reference — the conservative check must not flag it.
            LoadCapturingDiagnostics("""
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                  <rect width="24" height="24" fill="url(#maybe) red"/>
                </svg>
                """, out var diagnostics);

            Assert.DoesNotContain(diagnostics, d => d.Id == "AVLN2209");
        }

        [Fact]
        public void Invalid_Transform_Reports_A_Warning()
        {
            LoadCapturingDiagnostics("""
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                  <rect width="10" height="10" transform="rotate(oops)"/>
                </svg>
                """, out var diagnostics);

            Assert.Contains(diagnostics, d =>
                d.Id == "AVLN2210"
                && d.Severity == RuntimeXamlDiagnosticSeverity.Warning
                && d.Title.Contains("transform"));
        }

        [Fact]
        public void Valid_Transform_With_Css_Units_And_None_Reports_No_Warning()
        {
            // 'none' and the deg/px-bearing CSS form are both honored by the
            // renderer, so the check (which mirrors it) must not flag them.
            LoadCapturingDiagnostics("""
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                  <g transform="translate(10px 4px) rotate(45deg)">
                    <rect width="10" height="10" transform="none"/>
                  </g>
                </svg>
                """, out var diagnostics);

            Assert.DoesNotContain(diagnostics, d => d.Id == "AVLN2210");
        }

        [Fact]
        public void Invalid_Color_Reports_A_Warning()
        {
            LoadCapturingDiagnostics("""
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                  <rect width="10" height="10" fill="#ggg"/>
                </svg>
                """, out var diagnostics);

            Assert.Contains(diagnostics, d =>
                d.Id == "AVLN2210" && d.Title.Contains("paint") && d.Title.Contains("#ggg"));
        }

        [Fact]
        public void Valid_Paint_Forms_Report_No_Warning()
        {
            // currentColor, a url() reference, none and the CSS-wide 'inherit' are
            // all honored by the renderer and must not be flagged as invalid paint.
            LoadCapturingDiagnostics("""
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                  <defs><linearGradient id="g"><stop offset="0" stop-color="currentColor"/></linearGradient></defs>
                  <rect width="10" height="10" fill="url(#g)" stroke="inherit"/>
                  <circle r="2" fill="none"/>
                </svg>
                """, out var diagnostics);

            Assert.DoesNotContain(diagnostics, d => d.Id == "AVLN2210");
        }

        [Fact]
        public void Invalid_Length_Reports_A_Warning()
        {
            LoadCapturingDiagnostics("""
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                  <circle cx="12" cy="12" r="abc"/>
                </svg>
                """, out var diagnostics);

            Assert.Contains(diagnostics, d =>
                d.Id == "AVLN2210" && d.Title.Contains("length") && d.Title.Contains("abc"));
        }

        [Fact]
        public void Valid_Lengths_And_List_Coordinates_Report_No_Warning()
        {
            // Units, percentages and 'auto' (rx) are valid; list-valued text x/y
            // are not in the single-length set, so they are never misread.
            LoadCapturingDiagnostics("""
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                  <rect width="50%" height="10px" rx="auto"/>
                  <text x="2 6 10" y="8">hi</text>
                </svg>
                """, out var diagnostics);

            Assert.DoesNotContain(diagnostics, d => d.Id == "AVLN2210");
        }

        [Fact]
        public void Invalid_Path_Data_Reports_A_Warning()
        {
            LoadCapturingDiagnostics("""
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                  <path d="M0 0 L10 oops"/>
                </svg>
                """, out var diagnostics);

            Assert.Contains(diagnostics, d =>
                d.Id == "AVLN2210"
                && d.Severity == RuntimeXamlDiagnosticSeverity.Warning
                && d.Title.Contains("path data"));
        }

        [Fact]
        public void Valid_Path_Data_Reports_No_Warning()
        {
            LoadCapturingDiagnostics("""
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                  <path d="M8 46 L24 30 C36 42 46 32 56 42 A4 4 0 0 1 8 56 Z"/>
                </svg>
                """, out var diagnostics);

            Assert.DoesNotContain(diagnostics, d => d.Id == "AVLN2210");
        }

        [Fact]
        public void Malformed_Points_Reports_A_Warning()
        {
            // An odd coordinate count (the renderer drops the unpaired tail).
            LoadCapturingDiagnostics("""
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                  <polygon points="0,0 10,0 10"/>
                </svg>
                """, out var diagnostics);

            Assert.Contains(diagnostics, d => d.Id == "AVLN2210" && d.Title.Contains("points"));
        }

        [Fact]
        public void Valid_Points_Report_No_Warning()
        {
            LoadCapturingDiagnostics("""
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                  <polyline points="0,0 10,0 10,10 0,10"/>
                </svg>
                """, out var diagnostics);

            Assert.DoesNotContain(diagnostics, d => d.Id == "AVLN2210");
        }

        [Fact]
        public void Invalid_ViewBox_Reports_A_Warning()
        {
            // Negative width is rejected by SvgViewBox.TryParse, so the renderer
            // ignores the viewBox entirely.
            LoadCapturingDiagnostics("""
                <svg xmlns="http://www.w3.org/2000/svg">
                  <symbol id="s" viewBox="0 0 -5 10"><rect width="5" height="5"/></symbol>
                </svg>
                """, out var diagnostics);

            Assert.Contains(diagnostics, d => d.Id == "AVLN2210" && d.Title.Contains("viewBox"));
        }

        [Fact]
        public void Valid_ViewBox_Reports_No_Warning()
        {
            LoadCapturingDiagnostics("""
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                  <pattern id="p" viewBox="0 0 4 4"><rect width="2" height="2"/></pattern>
                </svg>
                """, out var diagnostics);

            Assert.DoesNotContain(diagnostics, d => d.Id == "AVLN2210");
        }
    }
}
