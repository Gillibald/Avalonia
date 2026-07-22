using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Xunit;

#if AVALONIA_SKIA
namespace Avalonia.Skia.RenderTests;

/// <summary>
/// Exercises <see cref="Visual.BackdropEffect"/> and <see cref="Visual.Effect"/>
/// through the API an application actually uses: properties on controls inside a
/// visual tree, rendered by the compositor. The effect render tests drive
/// DrawingContext.PushLayer from an overridden Render, which skips the whole
/// Visual -> CompositionVisual -> server-render path this covers.
/// </summary>
public class BackdropEffectTests : TestBase
{
    private const double Size = 200;

    public BackdropEffectTests() : base(@"Controls\BackdropEffect")
    {
    }

    /// <summary>
    /// A deliberately busy backdrop: four colour quadrants plus overlapping
    /// siblings, so a filter confined to the panel is obvious against its
    /// surroundings and colour filters have saturated input to work on.
    /// </summary>
    private static Canvas BuildScene()
    {
        var canvas = new Canvas { Width = Size, Height = Size, Background = Brushes.White };

        void Add(Control child, double left, double top)
        {
            Canvas.SetLeft(child, left);
            Canvas.SetTop(child, top);
            canvas.Children.Add(child);
        }

        var half = Size / 2;
        Add(new Border { Width = half, Height = half, Background = Brushes.Crimson }, 0, 0);
        Add(new Border { Width = half, Height = half, Background = Brushes.DodgerBlue }, half, 0);
        Add(new Border { Width = half, Height = half, Background = Brushes.Gold }, 0, half);
        Add(new Border { Width = half, Height = half, Background = Brushes.MediumSeaGreen }, half, half);

        // Overlapping detail so blur and morphology have edges to act on.
        Add(new Border { Width = 70, Height = 70, Background = Brushes.White, CornerRadius = new CornerRadius(35) }, 65, 20);
        Add(new Border { Width = 120, Height = 12, Background = Brushes.Black }, 40, 150);
        Add(new Border { Width = 12, Height = 120, Background = Brushes.Black }, 150, 40);

        return canvas;
    }

    private static Border FrostedPanel(IEffect backdrop) => new()
    {
        Width = 140,
        Height = 84,
        // A backdrop only shows where the control paints, so the panel carries a
        // translucent wash; a fully transparent panel composites to nothing.
        Background = new ImmutableSolidColorBrush(Colors.White, 0.3),
        BorderBrush = Brushes.White,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        BackdropEffect = backdrop
    };

    private async Task RunBackdrop(IEffect backdrop, [System.Runtime.CompilerServices.CallerMemberName] string testName = "")
    {
        var canvas = BuildScene();
        var panel = FrostedPanel(backdrop);
        Canvas.SetLeft(panel, 30);
        Canvas.SetTop(panel, 58);
        canvas.Children.Add(panel);

        await RenderToFile(canvas, testName);
        CompareImages(testName, skipImmediate: true);
    }

    [Fact]
    public Task Backdrop_Blur_On_Control() => RunBackdrop(new ImmutableBlurEffect(8));

    [Fact]
    public Task Backdrop_Grayscale_On_Control() => RunBackdrop(new ImmutableColorMatrixEffect(new double[]
    {
        0.2126, 0.7152, 0.0722, 0, 0,
        0.2126, 0.7152, 0.0722, 0, 0,
        0.2126, 0.7152, 0.0722, 0, 0,
        0,      0,      0,      1, 0
    }));

    /// <summary>
    /// The backdrop must sample siblings painted before this control and leave
    /// the ones painted after it alone, so a later sibling drawn over the panel
    /// stays sharp.
    /// </summary>
    [Fact]
    public async Task Backdrop_Only_Filters_Earlier_Siblings()
    {
        var canvas = BuildScene();

        var panel = FrostedPanel(new ImmutableBlurEffect(8));
        Canvas.SetLeft(panel, 30);
        Canvas.SetTop(panel, 58);
        canvas.Children.Add(panel);

        // Added after the panel, so it is not part of the backdrop and must
        // render crisp on top of the frosted region.
        var onTop = new Border { Width = 46, Height = 46, Background = Brushes.Black };
        Canvas.SetLeft(onTop, 96);
        Canvas.SetTop(onTop, 78);
        canvas.Children.Add(onTop);

        await RenderToFile(canvas);
        CompareImages(skipImmediate: true);
    }

    /// <summary>
    /// A frosted panel is normally rounded, and the backdrop has to follow that
    /// shape rather than filling the corners. The compositor cannot know a
    /// control's painted shape, so the panel's own clip is what confines it.
    /// </summary>
    [Fact]
    public async Task Backdrop_Follows_Rounded_Clip()
    {
        var canvas = BuildScene();

        // A grayscale backdrop over saturated quadrants makes the filtered region's
        // outline unmistakable; a blur over flat colour would hide the corners.
        var panel = FrostedPanel(new ImmutableColorMatrixEffect(new double[]
        {
            0.2126, 0.7152, 0.0722, 0, 0,
            0.2126, 0.7152, 0.0722, 0, 0,
            0.2126, 0.7152, 0.0722, 0, 0,
            0,      0,      0,      1, 0
        }));
        panel.Clip = new RectangleGeometry(new Rect(0, 0, 140, 84), 24, 24);
        panel.CornerRadius = new CornerRadius(24);
        Canvas.SetLeft(panel, 30);
        Canvas.SetTop(panel, 58);
        canvas.Children.Add(panel);

        await RenderToFile(canvas);
        CompareImages(skipImmediate: true);
    }

    /// <summary>
    /// The pre-existing Visual.Effect API over the same tree, for contrast: it
    /// filters the control's own content, not what is behind it.
    /// </summary>
    [Fact]
    public async Task Effect_On_Control()
    {
        var canvas = BuildScene();

        var panel = new Border
        {
            Width = 140,
            Height = 84,
            Background = Brushes.MediumVioletRed,
            CornerRadius = new CornerRadius(10),
            Effect = new ImmutableBlurEffect(6)
        };
        Canvas.SetLeft(panel, 30);
        Canvas.SetTop(panel, 58);
        canvas.Children.Add(panel);

        await RenderToFile(canvas);
        CompareImages(skipImmediate: true);
    }
}
#endif
