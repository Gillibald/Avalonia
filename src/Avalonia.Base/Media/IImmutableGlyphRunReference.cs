using System;
using System.Threading;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Platform;
using Avalonia.Utilities;

namespace Avalonia.Media;

public interface IImmutableGlyphRunReference : IDisposable
{
    internal IRef<IGlyphRunImpl>? GlyphRun { get; }
}

internal class ImmutableGlyphRunReference : IImmutableGlyphRunReference
{
    private IRef<IGlyphRunImpl>? _glyphRun;

    public ImmutableGlyphRunReference(IRef<IGlyphRunImpl>? glyphRun)
    {
        _glyphRun = glyphRun;

        // A custom draw operation draws the run through this reference, so the run keeps the
        // state drawing builds while the reference lives.
        (glyphRun?.Item as ManagedGlyphRunImpl)?.AddDrawingReference();
    }

    public IRef<IGlyphRunImpl>? GlyphRun => _glyphRun;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _glyphRun, null) is not { } glyphRun)
            return;

        (glyphRun.Item as ManagedGlyphRunImpl)?.ReleaseDrawingReference();
        glyphRun.Dispose();
    }
}
