using System.Collections.Generic;
using Avalonia.Platform;

namespace Avalonia.Rendering.Composition.Server;

/// <summary>
/// A side-effect-free view over a dirty-rect tracker's currently accumulating
/// working set, together with the mapping from the owning host's local space
/// to the tracker's storage space. Reads never finalize, inflate or optimize
/// the tracker, so they stay valid mid-pass - the update walk and the backdrop
/// expansion read it while rects are still being added.
/// </summary>
internal readonly struct DirtyRectWorkingSet
{
    private readonly IDirtyRectTracker? _tracker;
    public readonly DirtyRectSpaceMapping Mapping;

    public DirtyRectWorkingSet(IDirtyRectTracker? tracker, DirtyRectSpaceMapping mapping)
    {
        _tracker = tracker;
        Mapping = mapping;
    }

    /// <summary>
    /// Whether host-space reads are meaningful: there is a tracker and its
    /// mapping is invertible. Collectors without one (redirects, unions, a
    /// cache that has never drawn) yield an unusable view, which backdrop
    /// classification treats as unknown provenance.
    /// </summary>
    public bool IsUsable => _tracker != null && Mapping.IsUsable;

    /// <summary>Cheap emptiness query.</summary>
    public bool IsEmpty => _tracker?.IsEmpty ?? true;

    /// <summary>
    /// Appends the working-set rects, mapped into the host's local space, to
    /// <paramref name="buffer"/>.
    /// </summary>
    public void CollectTo(List<LtrbRect> buffer)
    {
        if (_tracker == null || _tracker.IsEmpty || !Mapping.IsUsable)
            return;

        var start = buffer.Count;
        _tracker.CollectWorkingSet(buffer);

        if (Mapping.IsIdentity)
            return;
        for (var i = start; i < buffer.Count; i++)
            buffer[i] = Mapping.TrackerToHost(buffer[i]);
    }
}
