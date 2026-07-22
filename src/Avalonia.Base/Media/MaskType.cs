namespace Avalonia.Media;

/// <summary>
/// Selects how an opacity-mask brush's output is interpreted when composited
/// against the masked content.
/// </summary>
public enum MaskType
{
    /// <summary>
    /// The alpha channel of the mask brush determines the final coverage.
    /// Opaque mask pixels pass the content through; transparent mask pixels
    /// erase it. This is the default for <see cref="DrawingContext.PushOpacityMask(IBrush, Rect)"/>.
    /// </summary>
    Alpha,

    /// <summary>
    /// The perceived luminance of the mask brush's RGB channels is used as
    /// alpha, multiplied by the mask's own alpha. This matches SVG's default
    /// <c>mask-type="luminance"</c> semantics.
    /// </summary>
    Luminance
}
