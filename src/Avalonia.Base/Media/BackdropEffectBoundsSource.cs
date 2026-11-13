namespace Avalonia.Media;

/// <summary>
/// Selects the region a <see cref="Visual.BackdropEffect"/> fills.
/// </summary>
public enum BackdropEffectBoundsSource
{
    /// <summary>
    /// The visual's own layout bounds, refined by its clips and backdrop clip.
    /// Children painting outside the visual and effect output such as a drop
    /// shadow fall on the unfiltered background, and the region does not move
    /// when children do.
    /// </summary>
    Layout,

    /// <summary>
    /// The bounds of everything the visual and its descendants paint, refined
    /// by the visual's clips and an explicit backdrop clip. For a container
    /// without a size of its own, whose extent exists only through its
    /// children. The region follows the children, so a change anywhere in the
    /// subtree refreshes the whole backdrop.
    /// </summary>
    Subtree
}
