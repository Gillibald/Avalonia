using System;
using System.Collections.Generic;

namespace Avalonia.Media;

/// <summary>
/// What a retained backdrop keeps between frames.
/// </summary>
internal enum BackdropCachePayload
{
    /// <summary>
    /// The filtered result. A frame that redraws the glass without a change
    /// beneath it costs one image draw and no filter. Re-filtering after a
    /// change beneath reads input around the changed output, so that ring has
    /// to be repainted fresh first.
    /// </summary>
    Output,

    /// <summary>
    /// The sampled surface beneath the glass, padded by the filter's reach.
    /// Every frame that redraws the glass runs the filter under the frame's
    /// clip, but a change beneath only has to be copied in: nothing past the
    /// changed output is repainted.
    /// </summary>
    Input,

    /// <summary>
    /// Nothing; the backdrop samples the live surface every time it is drawn
    /// and any change touching it repaints its whole area. Costs no memory,
    /// which is right when the content beneath changes all over every frame.
    /// </summary>
    None
}

/// <summary>
/// Handshake object between the compositor's update pass and a drawing context
/// implementation for retaining a backdrop layer across frames. The update pass
/// owns the policy: it picks the <see cref="Payload"/>, clears
/// <see cref="IsValid"/> when content beneath the backdrop changes and sets
/// <see cref="RefreshRequested"/> on frames whose dirty region is guaranteed to
/// contain everything the refresh reads. The implementation owns the mechanism:
/// it may capture only when a refresh was granted (any other frame might hand
/// it a partially stale surface), sets <see cref="IsValid"/> once a usable
/// payload exists, and draws from that payload instead of re-sampling while it
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
    /// Consecutive frames with small changes beneath the glass after which the
    /// retained output gives way to the retained input.
    /// </summary>
    internal const int SwitchToInputAfterFrames = 3;

    /// <summary>
    /// Consecutive frames with changes beneath most of the glass after which
    /// nothing is retained any more.
    /// </summary>
    internal const int SwitchToNoneAfterFrames = 3;

    /// <summary>
    /// Frames without any change beneath the glass after which a touch from
    /// above returns to the retained output.
    /// </summary>
    internal const int ReturnToOutputAfterFrames = 30;

    private long _observedFrame = -1;
    private BackdropTouch _observedTouch;
    private int _smallBehindStreak;
    private int _wideBehindStreak;
    private long _lastBehindFrame;
    private BackdropCachePayload? _pendingPayload;

    /// <summary>
    /// What the implementation retains. Written by the update pass; a change
    /// takes effect at the start of a frame and invalidates the retained
    /// pixels, which belong to the old payload.
    /// </summary>
    public BackdropCachePayload Payload { get; set; }

    /// <summary>
    /// Pins <see cref="Payload"/> and keeps the adaptive policy from switching
    /// it. Null lets the policy choose from what has been changing beneath the
    /// glass.
    /// </summary>
    public BackdropCachePayload? FixedPayload { get; set; }

    /// <summary>
    /// True while <see cref="PlatformState"/> holds a payload that matches the
    /// current content beneath the backdrop. Set by the implementation after a
    /// capture; cleared by the update pass on invalidation and by the
    /// implementation when its resources died with the platform context.
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// Set by the update pass when it has widened the dirty region to cover
    /// everything the refresh reads, which makes this frame safe to capture
    /// from. Consumed (cleared) by the implementation at the layer push whether
    /// or not the capture succeeds - a stale grant must not fire on a later
    /// frame whose region no longer covers the area. With
    /// <see cref="IsValid"/> false the grant is a full refresh; with it true
    /// the grant is a partial refresh listed in <see cref="RefreshRects"/> or
    /// <see cref="IngestRects"/>, depending on <see cref="Payload"/>.
    /// </summary>
    public bool RefreshRequested { get; set; }

    /// <summary>
    /// The output sub-rects of a partial refresh of a retained
    /// <see cref="BackdropCachePayload.Output"/>, in the visual's local space
    /// (the space of the layer bounds). Each rect's input neighborhood (the
    /// rect inflated by the effect's reach) is covered by this frame's dirty
    /// region, so re-filtering exactly these rects reads only freshly painted
    /// content. Consumed (cleared) with the grant.
    /// </summary>
    public List<Rect> RefreshRects { get; } = new();

    /// <summary>
    /// The input sub-rects of a partial refresh of a retained
    /// <see cref="BackdropCachePayload.Input"/>, in the visual's local space.
    /// Each rect is part of this frame's dirty region and is painted before the
    /// backdrop samples, so copying exactly these rects from the surface
    /// brings the retained input up to date. Consumed (cleared) with the
    /// grant.
    /// </summary>
    public List<Rect> IngestRects { get; } = new();

    /// <summary>
    /// The implementation's retained resources (pixels, device rect, the
    /// platform context they were created with). Opaque to the compositor;
    /// disposed with the slot.
    /// </summary>
    public object? PlatformState { get; set; }

    /// <summary>
    /// The update-pass frame the current partial grant was computed in, so a
    /// revisit within the same frame rebuilds the grant instead of stacking it.
    /// </summary>
    internal long PartialGrantFrame { get; set; } = -1;

    /// <summary>
    /// Records how this frame's damage touched the glass. Called for every
    /// touch, possibly several times per frame as the region grows; the
    /// widest touch of a frame is the one the policy sees.
    /// </summary>
    internal void Observe(long frame, BackdropTouch touch)
    {
        if (_observedFrame != frame)
        {
            _observedFrame = frame;
            _observedTouch = touch;
        }
        else if (touch > _observedTouch)
        {
            _observedTouch = touch;
        }
    }

    /// <summary>
    /// Feeds the frame's widest touch into the policy. A switch is only
    /// scheduled here: this frame's grant was already computed for the current
    /// payload, so the switch waits for <see cref="ApplyPendingPayload"/> at the
    /// start of the next frame.
    /// </summary>
    internal void EndFrame(long frame)
    {
        if (_observedFrame != frame)
            return;

        switch (_observedTouch)
        {
            case BackdropTouch.SmallBehind:
                _smallBehindStreak++;
                _wideBehindStreak = 0;
                _lastBehindFrame = frame;
                break;
            case BackdropTouch.WideBehind:
                _wideBehindStreak++;
                _smallBehindStreak = 0;
                _lastBehindFrame = frame;
                break;
            default:
                _smallBehindStreak = 0;
                _wideBehindStreak = 0;
                break;
        }

        if (FixedPayload is { } fixedPayload)
        {
            _pendingPayload = fixedPayload != Payload ? fixedPayload : null;
            return;
        }

        var next = Payload;
        if (_wideBehindStreak >= SwitchToNoneAfterFrames)
            next = BackdropCachePayload.None;
        else if (_smallBehindStreak >= SwitchToInputAfterFrames)
            next = BackdropCachePayload.Input;
        else if (_observedTouch == BackdropTouch.Above
                 && frame - _lastBehindFrame >= ReturnToOutputAfterFrames)
            next = BackdropCachePayload.Output;

        _pendingPayload = next != Payload ? next : null;
    }

    /// <summary>
    /// Applies a payload switch scheduled by an earlier frame, or one forced
    /// through <see cref="FixedPayload"/>. Runs before the frame's grants are
    /// computed, so the invalidation turns the next touch into a full refresh.
    /// </summary>
    internal void ApplyPendingPayload()
    {
        var target = _pendingPayload;
        if (FixedPayload is { } fixedPayload && fixedPayload != Payload)
            target = fixedPayload;
        _pendingPayload = null;

        if (target is not { } payload || payload == Payload)
            return;

        Payload = payload;
        IsValid = false;
        RefreshRequested = false;
        RefreshRects.Clear();
        IngestRects.Clear();
        _smallBehindStreak = 0;
        _wideBehindStreak = 0;
    }

    public void Dispose()
    {
        (PlatformState as IDisposable)?.Dispose();
        PlatformState = null;
        IsValid = false;
        RefreshRequested = false;
        RefreshRects.Clear();
        IngestRects.Clear();
    }
}

/// <summary>
/// How a frame's damage touched a backdrop, ordered from the least to the most
/// work it causes.
/// </summary>
internal enum BackdropTouch
{
    /// <summary>Damage only above the sample point.</summary>
    Above,

    /// <summary>Damage beneath a small part of the glass.</summary>
    SmallBehind,

    /// <summary>Damage beneath most of the glass, or of unknown extent.</summary>
    WideBehind
}
