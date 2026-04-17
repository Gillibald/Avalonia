using System;

namespace Avalonia.Media;

/// <summary>
/// A <see cref="DrawingContext"/> that is being used to build a
/// <see cref="Avalonia.Rendering.Composition.DrawingRecording"/>. Exposes
/// recording-only operations (e.g. element tagging) that have no meaning on
/// replay-only contexts such as the one used for immediate window rendering.
/// </summary>
/// <remarks>
/// Record delegates passed to <see cref="Avalonia.Rendering.Composition.DrawingRecording.Create(System.Action{DrawingRecordingContext})"/>
/// receive a <see cref="DrawingRecordingContext"/>. Regular drawing calls that work on
/// any <see cref="DrawingContext"/> are available unchanged through inheritance.
/// </remarks>
public abstract class DrawingRecordingContext : DrawingContext
{
    internal DrawingRecordingContext()
    {
    }

    /// <summary>
    /// Associates an opaque <paramref name="tag"/> with the subsequent draw operations
    /// up to the matching <see cref="DrawingContext.PushedState"/> disposal. Tags are
    /// preserved in the resulting
    /// <see cref="Avalonia.Rendering.Composition.DrawingRecording"/> and surfaced by
    /// <see cref="Avalonia.Rendering.Composition.DrawingRecording.HitTestElements(Point)"/>.
    /// They do not affect the rendered output.
    /// </summary>
    /// <param name="tag">An opaque identity value; typically the originating element.</param>
    /// <returns>A disposable used to pop the tag.</returns>
    public PushedState PushElementTag(object tag)
    {
        _ = tag ?? throw new ArgumentNullException(nameof(tag));
        PushElementTagCore(tag);
        return PushElementTagRestoreState();
    }

    /// <summary>
    /// When overridden in a derived class, associates the supplied tag with
    /// subsequent recorded draw operations until <see cref="PopElementTagCore"/>
    /// is called.
    /// </summary>
    protected abstract void PushElementTagCore(object tag);

    /// <summary>
    /// When overridden in a derived class, ends the scope of the most recent
    /// <see cref="PushElementTagCore"/>.
    /// </summary>
    protected internal abstract void PopElementTagCore();
}
