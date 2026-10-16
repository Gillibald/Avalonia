using System;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Reactive;
using Avalonia.UnitTests;

namespace Avalonia.Skia.UnitTests
{
    /// <summary>
    /// The application scopes of this assembly's tests. Each binds what the expectations here were
    /// recorded under, and a glyph rasterization context that owns what managed text keeps across
    /// frames; leaving the scope disposes it, as an application's shutdown would.
    /// </summary>
    internal static class TextTestApplication
    {
        /// <summary>
        /// The tuning of this assembly's applications. A GPU draws a moving run sharp while the
        /// frame's rasterization budget affords it, which depends on how fast the machine
        /// rasterizes; the expectations here record the stretched frames of an exhausted budget,
        /// and tests of the budget bind their own.
        /// </summary>
        public static GlyphRasterTuning Tuning { get; } = GlyphRasterTuning.Default with { MotionBudgetMicroseconds = 0 };

        /// <summary>
        /// The text rasterization defaults of this assembly's applications. The expectations here
        /// (goldens, ink bounds, routed tiers) are recorded against managed rasterization with
        /// unspecified text rendering subpixel on display surfaces; Linux, macOS and ARM64 hosts
        /// would otherwise render through the backend's text stack, and macOS hosts grayscale.
        /// Tests that exercise the backend choose it explicitly.
        /// </summary>
        public static TextRasterizationDefaults RasterizationDefaults { get; } =
            new(TextRasterizationMode.Managed, unspecifiedRendersSubpixel: true);

        /// <summary>
        /// Enters a locator scope with a new font manager, a glyph rasterization context and this
        /// assembly's bindings.
        /// </summary>
        public static IDisposable Enter()
        {
            var scope = AvaloniaLocator.EnterScope();
            var fontManager = new FontManager();
            var context = new GlyphRasterizationContext();

            AvaloniaLocator.CurrentMutable.Bind<FontManager>().ToConstant(fontManager);
            AvaloniaLocator.CurrentMutable.Bind<GlyphRasterizationContext>().ToConstant(context);
            BindDefaults();

            return Disposable.Create(() =>
            {
                // A test may have bound a font manager of its own over this one.
                if (AvaloniaLocator.Current.GetService<FontManager>() is { } bound && bound != fontManager)
                {
                    ((IDisposable)bound).Dispose();
                }

                ((IDisposable)fontManager).Dispose();
                context.Dispose();
                scope.Dispose();
            });
        }

        /// <summary>Starts a unit test application with this assembly's bindings.</summary>
        public static IDisposable Start(TestServices? services = null)
        {
            var application = UnitTestApplication.Start(services);

            BindDefaults();

            return application;
        }

        /// <summary>The text raster state of the calling thread in the current application.</summary>
        public static TextRasterThreadState ThreadState => GlyphRasterizationContext.Required.ForCurrentThread();

        /// <summary>The managed rasterization counters of the calling thread in the current application.</summary>
        public static TextRasterCounters Counters => ThreadState.Counters;

        /// <summary>The Skia text counters of the calling thread in the current application.</summary>
        public static SkiaTextCounters SkiaCounters => SkiaTextCounters.Of(ThreadState);

        /// <summary>Binds what this assembly's expectations were recorded under into the current scope.</summary>
        public static void BindDefaults()
        {
            AvaloniaLocator.CurrentMutable.Bind<GlyphRasterTuning>().ToConstant(Tuning);
            AvaloniaLocator.CurrentMutable.Bind<TextRasterizationDefaults>().ToConstant(RasterizationDefaults);
        }
    }
}
