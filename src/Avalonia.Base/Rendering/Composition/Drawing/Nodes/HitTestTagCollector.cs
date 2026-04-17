using System.Collections.Generic;
using Avalonia.Utilities;

namespace Avalonia.Rendering.Composition.Drawing.Nodes;

/// <summary>
/// Walks a recorded draw tree collecting <see cref="RenderDataElementTagNode.Tag"/>
/// values whose subtree contains the supplied hit point.
///
/// Traversal is in document order: nested tags appear before their containing tag,
/// so <c>[inner, outer]</c> is produced for <c>PushTag(outer){PushTag(inner){Rect}}</c>
/// when the point hits the rectangle. Sibling tags appear in the order they were
/// drawn; consumers that want top-most-first (SVG-style) reverse the result.
/// </summary>
internal static class HitTestTagCollector
{
    public static void Collect(IRenderDataItem item, Point p, List<object> results)
    {
        switch (item)
        {
            case RenderDataElementTagNode tag:
                if (AnyChildHitTests(tag.Children, p))
                {
                    foreach (var ch in tag.Children)
                        Collect(ch, p, results);
                    results.Add(tag.Tag);
                }
                break;

            case RenderDataPushMatrixNode matrix:
                if (matrix.Matrix.TryInvert(out var inv))
                {
                    var pt = p.Transform(inv);
                    foreach (var ch in matrix.Children)
                        Collect(ch, pt, results);
                }
                break;

            case RenderDataClipNode clip:
                if (clip.Rect.Rect.Contains(p))
                    foreach (var ch in clip.Children)
                        Collect(ch, p, results);
                break;

            case RenderDataGeometryClipNode geomClip:
                if (geomClip.Contains(p))
                    foreach (var ch in geomClip.Children)
                        Collect(ch, p, results);
                break;

            case RenderDataPushNode push:
                // Opacity, opacity mask, render options, text options —
                // none affect hit-test coordinates.
                foreach (var ch in push.Children)
                    Collect(ch, p, results);
                break;

            case RenderDataRecordingItemListNode sub:
                sub.Items.CollectHitTestTags(p, results);
                break;

            case RenderDataRecordingCompositionNode comp:
                comp.Client.CollectHitTestTags(p, results);
                break;

            // Leaf draw nodes carry no tags — nothing to collect.
        }
    }

    private static bool AnyChildHitTests(PooledInlineList<IRenderDataItem> children, Point p)
    {
        foreach (var ch in children)
            if (ch.HitTest(p))
                return true;
        return false;
    }
}
