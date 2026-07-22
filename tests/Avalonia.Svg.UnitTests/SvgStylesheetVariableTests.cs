using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Svg;
using Avalonia.Media.Svg.Compilation;
using Xunit;

namespace Avalonia.Svg.UnitTests;

/// <summary>
/// CSS custom properties (<c>--*</c>) resolved through <c>var()</c>, the
/// <c>:root</c> selector, and whole-block skipping of unsupported at-rules such
/// as <c>@media</c>. Observed through the resolved fill brush, which is where a
/// value that fails to resolve falls back to the inherited default.
/// </summary>
public class SvgStylesheetVariableTests
{
    private static Color ResolveFill(string svg, string id = "r")
    {
        using var document = SvgDocument.Parse(svg);
        SvgStylesheets.Apply(document);

        var style = SvgStyle.CreateDefault(new Size(100, 100));
        style.Apply(document.GetElementById(id)!);

        return Assert.IsType<ImmutableSolidColorBrush>(style.ResolveFillBrush()).Color;
    }

    [Fact]
    public void Root_Custom_Property_Resolves_Through_Var()
    {
        var color = ResolveFill(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <style>:root { --brand: #12ab34; } .box { fill: var(--brand); }</style>
              <rect id="r" class="box" width="10" height="10"/>
            </svg>
            """);

        Assert.Equal(Color.FromRgb(0x12, 0xab, 0x34), color);
    }

    [Fact]
    public void Custom_Property_Inherits_To_Descendants()
    {
        // Declared on the group via a style attribute, read on the child through
        // a presentation attribute.
        var color = ResolveFill(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <g style="--c: #0000ff">
                <rect id="r" fill="var(--c)" width="10" height="10"/>
              </g>
            </svg>
            """);

        Assert.Equal(Colors.Blue, color);
    }

    [Fact]
    public void Nearer_Custom_Property_Shadows_The_Root()
    {
        var color = ResolveFill(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <style>:root { --c: #ff0000; }</style>
              <g style="--c: #008000">
                <rect id="r" fill="var(--c)" width="10" height="10"/>
              </g>
            </svg>
            """);

        Assert.Equal(Color.FromRgb(0, 0x80, 0), color);
    }

    [Fact]
    public void Var_Fallback_Applies_When_Undefined()
    {
        var color = ResolveFill(
            """<svg xmlns="http://www.w3.org/2000/svg"><rect id="r" fill="var(--missing, #ff0000)" width="10" height="10"/></svg>""");

        Assert.Equal(Colors.Red, color);
    }

    [Fact]
    public void Var_Fallback_Captures_Nested_Parens()
    {
        var color = ResolveFill(
            """<svg xmlns="http://www.w3.org/2000/svg"><rect id="r" fill="var(--missing, rgb(0,128,0))" width="10" height="10"/></svg>""");

        Assert.Equal(Color.FromRgb(0, 128, 0), color);
    }

    [Fact]
    public void Undefined_Var_Without_Fallback_Keeps_The_Inherited_Value()
    {
        // The declaration is invalid at computed-value time, so the fill keeps its
        // inherited default (black) rather than falling through to something else.
        var color = ResolveFill(
            """<svg xmlns="http://www.w3.org/2000/svg"><rect id="r" fill="var(--missing)" width="10" height="10"/></svg>""");

        Assert.Equal(Colors.Black, color);
    }

    [Fact]
    public void Custom_Property_Can_Reference_Another_Custom_Property()
    {
        var color = ResolveFill(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <style>:root { --a: var(--b); --b: #008000; } .box { fill: var(--a); }</style>
              <rect id="r" class="box" width="10" height="10"/>
            </svg>
            """);

        Assert.Equal(Color.FromRgb(0, 0x80, 0), color);
    }

    [Fact]
    public void Media_At_Rule_Is_Skipped_Without_Breaking_Following_Rules()
    {
        // The @media block's nested braces must not corrupt the parse: the rule
        // after it still applies.
        var color = ResolveFill(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <style>
                @media (prefers-color-scheme: dark) { .box { fill: #000000; } }
                .box { fill: #ff0000; }
              </style>
              <rect id="r" class="box" width="10" height="10"/>
            </svg>
            """);

        Assert.Equal(Colors.Red, color);
    }
}
