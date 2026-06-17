using System;
using System.Collections.Generic;
using Avalonia.Input;
using Avalonia.Media;

namespace Avalonia.Controls;

/// <summary>
/// Carries the SVG element chain hit by a pointer event on the <see cref="SvgControl"/>,
/// together with the originating pointer event.
/// </summary>
public class SvgElementPointerEventArgs : EventArgs
{
    internal SvgElementPointerEventArgs(IReadOnlyList<SvgElement> elements, PointerEventArgs pointerArgs)
    {
        Elements = elements;
        PointerArgs = pointerArgs;
    }

    /// <summary>
    /// The hit chain in SVG event-target order: the innermost hit element first,
    /// followed by its ancestors up to the document root. Never empty.
    /// </summary>
    public IReadOnlyList<SvgElement> Elements { get; }

    /// <summary>The innermost hit element — the SVG event target.</summary>
    public SvgElement Element => Elements[0];

    /// <summary>The pointer event that triggered the hit test.</summary>
    public PointerEventArgs PointerArgs { get; }
}
