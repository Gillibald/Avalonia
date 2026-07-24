using System.Collections.Generic;
using Avalonia.Platform;

namespace Avalonia.Rendering.Composition.Server;

/// <summary>
/// A side-effect-free view over a dirty-rect tracker's currently accumulating
/// working set. Reads never finalize, inflate or optimize the tracker, so they
/// stay valid mid-pass - the update walk and the backdrop expansion read it
/// while rects are still being added. Rects are in the tracker's own space,
/// which for the render target's tracker is device space.
/// </summary>
internal readonly struct DirtyRectWorkingSet
{
    private readonly IDirtyRectTracker? _tracker;

    public DirtyRectWorkingSet(IDirtyRectTracker? tracker) => _tracker = tracker;

    /// <summary>Cheap emptiness query.</summary>
    public bool IsEmpty => _tracker?.IsEmpty ?? true;

    /// <summary>Appends the working-set rects to <paramref name="buffer"/>.</summary>
    public void CollectTo(List<LtrbRect> buffer)
    {
        if (_tracker == null || _tracker.IsEmpty)
            return;

        _tracker.CollectWorkingSet(buffer);
    }
}
