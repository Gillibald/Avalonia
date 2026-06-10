using System;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Svg.Compilation;

namespace Avalonia.Svg;

/// <summary>
/// An <see cref="IImage"/> that renders an SVG document. The document is compiled
/// once into an immutable <see cref="DrawingRecording"/> at construction time and
/// replayed on every draw.
/// </summary>
public sealed class SvgImage : IImage, IDisposable
{
    private readonly DrawingRecording _recording;

    /// <summary>
    /// Compiles <paramref name="document"/> into a recording sized to the
    /// document's intrinsic size.
    /// </summary>
    public SvgImage(SvgDocument document)
    {
        _ = document ?? throw new ArgumentNullException(nameof(document));

        Size = document.GetIntrinsicSize();
        _recording = DrawingRecording.Create(ctx => SvgCompiler.CompileDocument(document, ctx, Size));
    }

    /// <inheritdoc/>
    public Size Size { get; }

    /// <summary>The compiled recording; replayable directly via <see cref="DrawingContext.DrawRecording(DrawingRecording)"/>.</summary>
    public DrawingRecording Recording => _recording;

    /// <inheritdoc/>
    public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
    {
        if (_recording.IsDisposed
            || sourceRect.Width <= 0 || sourceRect.Height <= 0
            || destRect.Width <= 0 || destRect.Height <= 0)
        {
            return;
        }

        var transform =
            Matrix.CreateTranslation(-sourceRect.X, -sourceRect.Y)
            * Matrix.CreateScale(destRect.Width / sourceRect.Width, destRect.Height / sourceRect.Height)
            * Matrix.CreateTranslation(destRect.X, destRect.Y);

        using (context.PushClip(destRect))
            context.DrawRecording(_recording, transform);
    }

    /// <inheritdoc/>
    public void Dispose() => _recording.Dispose();
}
