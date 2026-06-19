using System;
using Avalonia.Rendering.Composition;

namespace Avalonia.Media;

/// <summary>
/// An <see cref="IImage"/> that can render as a composition visual subtree which
/// animates on the render thread, instead of as flat draw ops. A host that can
/// parent a visual (such as the <c>Image</c> control) creates an instance,
/// attaches its <see cref="ICompositionImageInstance.Visual"/>, keeps the stretch
/// transform up to date and pumps the clock while needed; any other surface falls
/// back to <see cref="IImage.Draw"/>.
/// </summary>
internal interface ICompositionImage : IImage
{
    /// <summary>
    /// Creates a server-side animating visual subtree for one host on
    /// <paramref name="compositor"/>, or null when the image has no content that
    /// benefits from a composition visual (the host should fall back to
    /// <see cref="IImage.Draw"/>). A composition image is a factory: each host
    /// gets its own instance, since a <see cref="CompositionVisual"/> has a single
    /// parent.
    /// </summary>
    ICompositionImageInstance? CreateInstance(Compositor compositor);

    /// <summary>
    /// Raised on the UI thread when the image's content has changed (for example a
    /// new source document was assigned) so any live host must refresh: drop the
    /// <see cref="ICompositionImageInstance"/> built from the previous content,
    /// recreate it from the updated image, and repaint. The image's
    /// <see cref="IImage.Size"/> may also have changed, so hosts re-measure.
    /// </summary>
    event EventHandler? Invalidated;
}

/// <summary>
/// One host's live instance of an <see cref="ICompositionImage"/>. The host owns
/// it: attach <see cref="Visual"/> as a child visual, keep
/// <see cref="SetStretchTransform"/> current, call <see cref="OnClock"/> while
/// <see cref="NeedsClock"/> is true, and dispose it when detached or when the
/// source changes.
/// </summary>
internal interface ICompositionImageInstance : IDisposable
{
    /// <summary>The visual subtree to parent under the host.</summary>
    CompositionVisual Visual { get; }

    /// <summary>Maps the host's content rect onto the image (stretch and offset).</summary>
    void SetStretchTransform(Matrix transform);

    /// <summary>
    /// True while the instance still needs per-frame UI-thread ticks (the
    /// residual animations that are not server-side). False once everything runs
    /// as render-thread composition animations.
    /// </summary>
    bool NeedsClock { get; }

    /// <summary>Advances the residual animations to <paramref name="elapsed"/> document time.</summary>
    void OnClock(TimeSpan elapsed);
}
