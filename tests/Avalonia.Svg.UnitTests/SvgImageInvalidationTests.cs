using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Svg;
using Avalonia.Rendering.Composition;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Svg.UnitTests;

/// <summary>
/// Reassigning <see cref="SvgImage.Document"/> recompiles the image and signals
/// hosts through <see cref="ICompositionImage.Invalidated"/>; a hosting control
/// then rebuilds the composition instance it had built from the previous document.
/// </summary>
public class SvgImageInvalidationTests
{
    private const string Svg10 =
        """<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><rect width="10" height="10"/></svg>""";
    private const string Svg20 =
        """<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20"><rect width="20" height="20"/></svg>""";

    [Fact]
    public void Reassigning_Document_Recompiles_And_Raises_Invalidated()
    {
        using var first = SvgDocument.Parse(Svg10);
        using var second = SvgDocument.Parse(Svg20);

        var image = new SvgImage(first);
        var firstRecording = image.Recording;
        Assert.Equal(new Size(10, 10), image.Size);

        var raised = 0;
        ((ICompositionImage)image).Invalidated += (_, _) => raised++;

        image.Document = second;

        Assert.Equal(1, raised);
        Assert.Same(second, image.Document);
        Assert.Equal(new Size(20, 20), image.Size);
        Assert.NotSame(firstRecording, image.Recording);
        Assert.True(firstRecording.IsDisposed); // the superseded recording is released
    }

    [Fact]
    public void Reassigning_The_Same_Document_Is_A_Noop()
    {
        using var document = SvgDocument.Parse(Svg10);
        var image = new SvgImage(document);

        var raised = 0;
        ((ICompositionImage)image).Invalidated += (_, _) => raised++;

        image.Document = document;

        Assert.Equal(0, raised);
        Assert.False(image.Recording.IsDisposed);
    }

    [Fact]
    public void Setting_Document_To_Null_Clears_And_Raises_Invalidated()
    {
        using var document = SvgDocument.Parse(Svg10);
        var image = new SvgImage(document);
        var recording = image.Recording;

        var raised = 0;
        ((ICompositionImage)image).Invalidated += (_, _) => raised++;

        image.Document = null;

        Assert.Equal(1, raised);
        Assert.Null(image.Document);
        Assert.Equal(default, image.Size);
        Assert.True(recording.IsDisposed);
    }

    [Fact]
    public void A_Document_Assigned_To_The_Property_Stays_Caller_Owned_When_Replaced()
    {
        using var first = SvgDocument.Parse(Svg10);
        using var second = SvgDocument.Parse(Svg20);

        // The object-initializer assignment goes through the Document setter.
        var image = new SvgImage { Document = first };
        image.Document = second;

        Assert.False(first.IsDisposed); // property documents are not owned by the image
    }

    private const string AnimatedFirst =
        """
        <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
          <g>
            <animateTransform attributeName="transform" type="rotate"
                              from="0 50 50" to="360 50 50" dur="4s" repeatCount="indefinite"/>
            <circle cx="80" cy="50" r="10" fill="#f59e0b"/>
          </g>
        </svg>
        """;

    private const string AnimatedSecond =
        """
        <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
          <g>
            <animateTransform attributeName="transform" type="rotate"
                              from="360 50 50" to="0 50 50" dur="2s" repeatCount="indefinite"/>
            <rect x="40" y="40" width="20" height="20" fill="#22d3ee"/>
          </g>
        </svg>
        """;

    [Fact]
    public void Host_Rebuilds_Its_Instance_When_A_Shared_SvgImage_Swaps_Document()
    {
        using var services = new CompositorTestServices(new Size(100, 100));
        using var first = SvgDocument.Parse(AnimatedFirst);
        using var second = SvgDocument.Parse(AnimatedSecond);

        var svgImage = new SvgImage(first);
        var image = new Image { Source = svgImage };

        services.TopLevel.Content = image;
        services.RunJobs();

        // The animated document is hosted as a child composition visual.
        var firstVisual = ElementComposition.GetElementChildVisual(image);
        Assert.NotNull(firstVisual);

        svgImage.Document = second;
        services.RunJobs();

        // The host dropped the instance built from the first document and rebuilt
        // it from the second, so a fresh child visual is attached.
        var secondVisual = ElementComposition.GetElementChildVisual(image);
        Assert.NotNull(secondVisual);
        Assert.NotSame(firstVisual, secondVisual);
    }
}
