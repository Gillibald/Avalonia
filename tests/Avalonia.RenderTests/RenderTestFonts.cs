using System;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace Avalonia.Skia.RenderTests;

/// <summary>
/// The fonts of one render test class: a locator scope with a font manager and the render
/// subsystem's system font collection, which the class's tests share and which ends after its
/// last test, so the system fonts are enumerated once per class. Each test still gets a glyph
/// rasterization context of its own (<see cref="TestRenderHelper.BeginTest"/>), so the glyph caches
/// of the shared typefaces start over with every test.
/// </summary>
public sealed class RenderTestFonts : IDisposable
{
    private readonly IDisposable _scope;
    private readonly FontManager _fontManager;

    public RenderTestFonts()
    {
        // The platform services belong to the root scope, which outlives every class.
        TestRenderHelper.RegisterPlatformServices();

        _scope = AvaloniaLocator.EnterScope();
        _fontManager = new FontManager();

        AvaloniaLocator.CurrentMutable.Bind<FontManager>().ToConstant(_fontManager);

        // The app builder registers the render subsystem's system fonts; render tests skip the builder.
        _fontManager.AddFontCollection(new SystemFontCollection(FontManager.SystemFontsKey, new SkiaFontProvider()));
    }

    public void Dispose()
    {
        ((IDisposable)_fontManager).Dispose();
        _scope.Dispose();
    }
}
