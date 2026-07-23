using System;

namespace Avalonia.Media;

/// <summary>
/// Handshake object between the compositor's update pass and a drawing context
/// implementation for retaining a backdrop layer's filtered result across
/// frames. The update pass owns the policy: it clears <see cref="IsValid"/>
/// when content beneath the backdrop changes and sets
/// <see cref="RefreshRequested"/> on frames whose dirty region is guaranteed to
/// contain the filter's whole input area. The implementation owns the
/// mechanism: it may capture only when a refresh was granted (any other frame
/// might hand it a partially stale surface), sets <see cref="IsValid"/> once a
/// usable image exists, and draws that image instead of re-sampling while it
/// stays valid. A backend that ignores the slot leaves <see cref="IsValid"/>
/// unset, which keeps the update pass repainting the full area under the
/// backdrop every time it is touched - the uncached behavior.
/// </summary>
/// <remarks>
/// Only ever touched from the render thread: the update pass and the render
/// pass run sequentially there, so the flags need no synchronization.
/// </remarks>
internal sealed class BackdropLayerCache : IDisposable
{
    /// <summary>
    /// True while <see cref="PlatformState"/> holds an image that matches the
    /// current content beneath the backdrop. Set by the implementation after a
    /// capture; cleared by the update pass on invalidation and by the
    /// implementation when its resources died with the platform context.
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// Set by the update pass when it has widened the dirty region to cover the
    /// filter's whole input area, which makes this frame safe to capture from.
    /// Consumed (cleared) by the implementation at the layer push whether or
    /// not the capture succeeds - a stale grant must not fire on a later frame
    /// whose region no longer covers the area.
    /// </summary>
    public bool RefreshRequested { get; set; }

    /// <summary>
    /// The implementation's retained resources (image, device rect, the
    /// platform context they were created with). Opaque to the compositor;
    /// disposed with the slot.
    /// </summary>
    public object? PlatformState { get; set; }

    public void Dispose()
    {
        (PlatformState as IDisposable)?.Dispose();
        PlatformState = null;
        IsValid = false;
        RefreshRequested = false;
    }
}
