using System;

namespace Avalonia.Media;

/// <summary>
/// Tells the compositor how the content behind a backdrop effect is expected to
/// change, so it can decide whether retaining the backdrop between frames pays
/// off. A null value lets the compositor decide from what it observes changing
/// beneath the backdrop.
/// </summary>
/// <remarks>
/// The hint never changes what is drawn, only what is kept between frames and
/// how much of the frame is repainted when something beneath the backdrop
/// changes.
/// </remarks>
public abstract class BackdropEffectCache
{
    private protected BackdropEffectCache()
    {
    }

    /// <summary>
    /// Parses a cache mode name, enabling the <c>BackdropEffectCache="Retained"</c>
    /// attribute syntax. <c>"Default"</c> maps to null (the compositor decides).
    /// </summary>
    public static BackdropEffectCache? Parse(string s) => s switch
    {
        "Default" => null,
        "Retained" => new RetainedBackdropEffectCache(),
        "Volatile" => new VolatileBackdropEffectCache(),
        _ => throw new ArgumentException("Unknown BackdropEffectCache: " + s)
    };

    internal abstract BackdropCacheHint Hint { get; }
}

/// <summary>
/// Keeps the backdrop in a texture between frames, for content behind it that
/// stays mostly still, such as the background of a modal dialog. The compositor
/// still picks what the texture holds.
/// </summary>
public sealed class RetainedBackdropEffectCache : BackdropEffectCache
{
    internal override BackdropCacheHint Hint => BackdropCacheHint.Retained;
}

/// <summary>
/// Keeps nothing between frames and samples the content behind the backdrop
/// every time it is drawn, for content that changes all the time, such as an
/// overlay on video playback. Costs no texture memory.
/// </summary>
public sealed class VolatileBackdropEffectCache : BackdropEffectCache
{
    internal override BackdropCacheHint Hint => BackdropCacheHint.Volatile;
}
