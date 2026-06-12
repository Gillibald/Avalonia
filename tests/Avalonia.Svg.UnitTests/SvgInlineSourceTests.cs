using System.Linq;
using Xunit;

namespace Avalonia.Svg.UnitTests;

public class SvgInlineSourceTests
{
    private const string RectMarkup =
        """<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100"><rect id="inline" x="10" y="10" width="20" height="20" fill="red"/></svg>""";

    private static Svg CreateMeasured(Svg control)
    {
        control.Measure(new Size(100, 100));
        control.Arrange(new Rect(0, 0, 100, 100));
        return control;
    }

    [Fact]
    public void Inline_Source_Parses_And_Hit_Tests()
    {
        var control = CreateMeasured(new Svg
        {
            InlineSource = RectMarkup,
            Width = 100,
            Height = 100,
        });

        var chain = control.HitTestElements(new Point(15, 15));

        Assert.Contains(chain, e => e.Id == "inline");
        Assert.Empty(control.HitTestElements(new Point(50, 50)));
    }

    [Fact]
    public void Document_Takes_Precedence_Over_Inline_Source()
    {
        using var document = SvgDocument.Parse(
            """<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100"><rect id="doc" x="60" y="60" width="20" height="20"/></svg>""");

        var control = CreateMeasured(new Svg
        {
            Document = document,
            InlineSource = RectMarkup,
            Width = 100,
            Height = 100,
        });

        Assert.Contains(control.HitTestElements(new Point(65, 65)), e => e.Id == "doc");
        Assert.Empty(control.HitTestElements(new Point(15, 15)));
    }

    [Fact]
    public void Changing_Inline_Source_Reloads_The_Document()
    {
        var control = CreateMeasured(new Svg
        {
            InlineSource = RectMarkup,
            Width = 100,
            Height = 100,
        });
        Assert.NotEmpty(control.HitTestElements(new Point(15, 15)));

        control.InlineSource =
            """<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100"><rect x="60" y="60" width="20" height="20"/></svg>""";
        CreateMeasured(control);

        Assert.Empty(control.HitTestElements(new Point(15, 15)));
        Assert.NotEmpty(control.HitTestElements(new Point(65, 65)));
    }

    [Fact]
    public void Invalid_Inline_Source_Fails_Gracefully()
    {
        var control = CreateMeasured(new Svg
        {
            InlineSource = "<svg not xml",
            Width = 100,
            Height = 100,
        });

        Assert.Empty(control.HitTestElements(new Point(15, 15)));
    }
}
