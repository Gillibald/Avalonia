using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Server;
using Avalonia.UnitTests;
using Avalonia.VisualTree;
using Xunit;

namespace Avalonia.Base.UnitTests.Rendering;

/// <summary>
/// Every server visual keeps a count of the registered backdrops beneath it,
/// maintained along the parent chain as effects come and go and visuals move.
/// The update walk trusts it to keep descending clean spines toward glass, so
/// a drifted count either skips a backdrop's classification or walks for
/// nothing. Random edits are checked against a brute-force recount.
/// </summary>
public class BackdropSubtreeCounterTests : CompositorTestsBase
{
    private static int Recount(ServerCompositionVisual visual, Dictionary<ServerCompositionVisual, int> counts)
    {
        var below = 0;
        if (visual.Children != null)
        {
            foreach (var child in visual.Children)
                below += Recount(child, counts) + (child.BackdropCache != null ? 1 : 0);
        }

        counts[visual] = below;
        return below;
    }

    private static int AssertCountsMatch(CompositorTestServices s, Visual root)
    {
        s.RunJobs();
        var server = (ServerCompositionVisual)ElementComposition.GetElementVisual(root)!.Server!;
        var expected = new Dictionary<ServerCompositionVisual, int>();
        Recount(server, expected);
        foreach (var (visual, count) in expected)
            Assert.Equal(count, visual._backdropsInSubTree);
        return expected[server];
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Subtree_Backdrop_Counts_Match_A_Recount_Under_Random_Edits(int seed)
    {
        var random = new Random(seed);
        using var s = new CompositorCanvas();
        var panels = new List<Canvas> { s.Canvas };
        var mostGlass = 0;

        for (var step = 0; step < 80; step++)
        {
            // Adds and toggles outweigh removals so the tree grows nested glass.
            switch (random.Next(10) switch { < 4 => 0, < 7 => 1, 7 => 2, _ => 3 })
            {
                case 0:
                {
                    var parent = panels[random.Next(panels.Count)];
                    var panel = new Canvas { Width = 20, Height = 20, Background = Brushes.Gray };
                    parent.Children.Add(panel);
                    panels.Add(panel);
                    break;
                }
                case 1:
                {
                    var panel = panels[random.Next(panels.Count)];
                    panel.BackdropEffect = panel.BackdropEffect == null ? new ImmutableBlurEffect(2) : null;
                    break;
                }
                case 2 when panels.Count > 1:
                {
                    // Detach a whole subtree, glass inside it included.
                    var panel = panels[1 + random.Next(panels.Count - 1)];
                    ((Canvas)panel.Parent!).Children.Remove(panel);
                    RemoveSubtree(panels, panel);
                    break;
                }
                case 3 when panels.Count > 2:
                {
                    // Move a subtree under a panel outside it.
                    var panel = panels[1 + random.Next(panels.Count - 1)];
                    var target = panels[random.Next(panels.Count)];
                    if (IsInSubtree(target, panel))
                        break;
                    ((Canvas)panel.Parent!).Children.Remove(panel);
                    target.Children.Add(panel);
                    break;
                }
            }

            mostGlass = Math.Max(mostGlass, AssertCountsMatch(s, s.Canvas));
        }

        Assert.True(mostGlass > 1, $"The edits never stacked glass: at most {mostGlass} at once");
    }

    private static bool IsInSubtree(Visual candidate, Visual root)
    {
        for (var v = candidate; v != null; v = v.GetVisualParent())
        {
            if (ReferenceEquals(v, root))
                return true;
        }

        return false;
    }

    private static void RemoveSubtree(List<Canvas> panels, Canvas root)
    {
        panels.Remove(root);
        foreach (var child in root.Children)
        {
            if (child is Canvas canvas)
                RemoveSubtree(panels, canvas);
        }
    }
}
