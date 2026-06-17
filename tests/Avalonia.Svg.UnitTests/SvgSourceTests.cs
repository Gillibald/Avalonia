using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Svg;
using Xunit;

namespace Avalonia.Svg.UnitTests;

public class SvgSourceTests
{
    private const string RectMarkup =
        """<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100"><rect id="r" x="10" y="10" width="20" height="20" fill="red"/></svg>""";

    private static SvgControl CreateMeasured(SvgControl control)
    {
        control.Measure(new Size(100, 100));
        control.Arrange(new Rect(0, 0, 100, 100));
        return control;
    }

    [Fact]
    public void Source_Document_Renders_And_Hit_Tests()
    {
        using var document = SvgDocument.Parse(RectMarkup);
        var control = CreateMeasured(new SvgControl
        {
            Source = document,
            Width = 100,
            Height = 100,
        });

        Assert.Contains(control.HitTestElements(new Point(15, 15)), e => e.Id == "r");
        Assert.Empty(control.HitTestElements(new Point(50, 50)));
    }

    [Fact]
    public void Xaml_Created_Documents_Are_Disposed_On_Replacement()
    {
        var inline = SvgDocument.FromXamlContent(RectMarkup);
        Assert.True(inline.HostOwned);

        var control = CreateMeasured(new SvgControl { Source = inline, Width = 100, Height = 100 });
        Assert.NotEmpty(control.HitTestElements(new Point(15, 15)));

        control.Source = null;

        Assert.True(inline.IsDisposed);
    }

    [Fact]
    public void Caller_Owned_Documents_Are_Not_Disposed()
    {
        using var document = SvgDocument.Parse(RectMarkup);
        var control = CreateMeasured(new SvgControl { Source = document, Width = 100, Height = 100 });

        control.Source = null;

        Assert.False(document.IsDisposed);
    }

    [Fact]
    public void Changing_Source_Reloads()
    {
        var control = CreateMeasured(new SvgControl
        {
            Source = SvgDocument.FromXamlContent(RectMarkup),
            Width = 100,
            Height = 100,
        });
        Assert.NotEmpty(control.HitTestElements(new Point(15, 15)));

        control.Source = SvgDocument.FromXamlContent(
            """<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100"><rect x="60" y="60" width="20" height="20"/></svg>""");
        CreateMeasured(control);

        Assert.Empty(control.HitTestElements(new Point(15, 15)));
        Assert.NotEmpty(control.HitTestElements(new Point(65, 65)));
    }
}

// The XAML SvgDocumentTypeConverter (in Avalonia.Markup.Xaml) only reads the
// base URI from the markup context and delegates here, so the conversion logic
// is covered by exercising SvgDocument.FromXamlSource directly with an explicit
// base URI.
public class SvgDocumentSourceTests
{
    private const string RectMarkup =
        """<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><rect id="r" width="10" height="10"/></svg>""";

    [Fact]
    public void Markup_Strings_Parse_As_Host_Owned_Documents()
    {
        var document = SvgDocument.FromXamlSource($"  {RectMarkup}", baseUri: null);

        Assert.True(document.HostOwned);
        Assert.Equal("rect", document.GetElementById("r")!.Name);
    }

    [Fact]
    public void File_Uri_Strings_Load_As_Host_Owned_Documents()
    {
        var path = Path.Combine(Path.GetTempPath(), $"svg-source-{Guid.NewGuid():N}.svg");
        File.WriteAllText(path, RectMarkup);
        try
        {
            var uri = new Uri(path, UriKind.Absolute);

            var document = SvgDocument.FromXamlSource(uri.AbsoluteUri, baseUri: null);

            Assert.True(document.HostOwned);
            Assert.NotNull(document.GetElementById("r"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Relative_Uri_Without_Base_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SvgDocument.FromXamlSource("/Assets/icon.svg", baseUri: null));
    }

    [Fact]
    public void Relative_Uri_Resolves_Against_The_Base_Uri()
    {
        var path = Path.Combine(Path.GetTempPath(), $"svg-base-{Guid.NewGuid():N}.svg");
        File.WriteAllText(path, RectMarkup);
        try
        {
            // The base URI is the file itself; the relative source (its file
            // name) resolves back to the same file.
            var baseUri = new Uri(path, UriKind.Absolute);

            var document = SvgDocument.FromXamlSource(Path.GetFileName(path), baseUri);

            Assert.True(document.HostOwned);
            Assert.NotNull(document.GetElementById("r"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
