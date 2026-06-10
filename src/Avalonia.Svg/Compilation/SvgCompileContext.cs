using System;
using System.Collections.Generic;
using Avalonia.Rendering.Composition;

namespace Avalonia.Svg.Compilation;

/// <summary>
/// Per-compilation state: the document being compiled, the viewport, and the
/// cycle guard for <c>&lt;use&gt;</c> recursion. Shared sub-recordings are cached
/// on the <see cref="SvgDocument"/> so they are reused across compilations and
/// released by <see cref="SvgDocument.Dispose"/>.
/// </summary>
internal sealed class SvgCompileContext
{
    private HashSet<SvgElement>? _useStack;

    public SvgCompileContext(SvgDocument document, Size viewport)
    {
        Document = document;
        Viewport = viewport;
    }

    public SvgDocument Document { get; }

    public Size Viewport { get; }

    /// <summary>
    /// Guards against reference cycles while expanding <c>&lt;use&gt;</c>.
    /// Returns false when <paramref name="target"/> is already being expanded.
    /// </summary>
    public bool EnterUse(SvgElement target)
    {
        _useStack ??= new HashSet<SvgElement>();
        return _useStack.Add(target);
    }

    public void ExitUse(SvgElement target) => _useStack?.Remove(target);

    /// <summary>
    /// Gets (or compiles and caches) the shared recording for a referenced
    /// element. The content is compiled once with the default style context —
    /// use-site style inheritance into unstyled referenced content is not
    /// propagated (the recording is shared between all use sites).
    /// </summary>
    public DrawingRecording GetSharedRecording(SvgElement target)
    {
        if (Document.TryGetSharedRecording(target, Viewport, out var recording))
            return recording;

        recording = DrawingRecording.Create(ctx =>
        {
            var style = SvgStyle.CreateDefault(Viewport);

            if (target.Name is "symbol" or "svg")
            {
                // The symbol's viewport mapping (viewBox, width/height) is applied
                // at the use site so the recording itself stays shareable.
                style.Apply(target);
                foreach (var child in target.Children)
                    SvgCompiler.CompileElement(child, ctx, this, style);
            }
            else
            {
                SvgCompiler.CompileElement(target, ctx, this, style);
            }
        });

        Document.AddSharedRecording(target, Viewport, recording);
        return recording;
    }
}
