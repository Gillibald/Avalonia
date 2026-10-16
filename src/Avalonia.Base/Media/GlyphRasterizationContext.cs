using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia.Media.Fonts.Rasterization;

namespace Avalonia.Media
{
    /// <summary>
    /// The state <see cref="TextRasterizationMode.Managed"/> glyph rasterization keeps for one
    /// application: its glyph caches and what each thread that draws text decides from frame to
    /// frame. The rendering setup creates it and registers it with the application.
    /// </summary>
    public sealed class GlyphRasterizationContext
    {
        private readonly ThreadLocal<TextRasterThreadState> _threads;
        private readonly TextRasterizationDefaults _processDefaults = TextRasterizationDefaults.ForProcess();
        private LcdRunAtlas? _lcdAtlas;
        private volatile bool _disposed;

        internal GlyphRasterizationContext()
        {
            _threads = new ThreadLocal<TextRasterThreadState>(() => new TextRasterThreadState(this),
                trackAllValues: true);
            Budget = new GlyphCacheBudget(static () => AvaloniaLocator.Current.GetService<GlyphRasterizationOptions>(),
                TextRasterizationDefaults.CurrentPlatform());
        }

        /// <summary>
        /// Gets the context of the current application, or <c>null</c> when its rendering setup
        /// registered none.
        /// </summary>
        public static GlyphRasterizationContext? Current => AvaloniaLocator.Current.GetService<GlyphRasterizationContext>();

        /// <summary>
        /// Releases the memory that the glyph caches of <see cref="TextRasterizationMode.Managed"/>
        /// rasterization hold beyond what the frames being drawn use: rasterized glyph masks,
        /// glyph atlas pages, composed run masks, hinting state and glyph outlines.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Call it when the application learns that memory is low, for example from a signal of
        /// its own; the platforms that report memory pressure or a move to the background call it
        /// already. Text drawn afterwards rasterizes its glyphs again, so calling it while text
        /// is animating costs frame time.
        /// </para>
        /// <para>
        /// Safe to call from any thread. Atlas pages and per-run masks that a frame on another
        /// thread may be drawing from at the time are released when the next frame begins. The
        /// glyph caches of <see cref="TextRasterizationMode.Backend"/> rasterization belong to the
        /// render backend and are not affected.
        /// </para>
        /// </remarks>
        public void TrimCaches() => Budget.TrimForMemoryPressure();

        /// <summary>
        /// The context of the current application. Code on a render thread resolves it once per
        /// drawing context.
        /// </summary>
        /// <exception cref="InvalidOperationException">The application registered no context.</exception>
        internal static GlyphRasterizationContext Required => Current ?? throw new InvalidOperationException(
            "Managed glyph rasterization needs the GlyphRasterizationContext the rendering setup registers; " +
            "the current application has none.");

        /// <summary>
        /// The budget of every glyph cache of the application, limited by
        /// <see cref="GlyphRasterizationOptions.GlyphCacheLimitBytes"/> or the platform default.
        /// </summary>
        internal GlyphCacheBudget Budget { get; }

        /// <summary>
        /// The application's options, looked up on every read: the application may register them
        /// after the rendering setup made the context.
        /// </summary>
        internal static GlyphRasterizationOptions? Options => AvaloniaLocator.Current.GetService<GlyphRasterizationOptions>();

        /// <summary>
        /// What the application renders when it does not choose: the defaults bound in its locator,
        /// else the running platform's.
        /// </summary>
        internal TextRasterizationDefaults RasterizationDefaults
            => AvaloniaLocator.Current.GetService<TextRasterizationDefaults>() ?? _processDefaults;

        /// <summary>The engine that rasterizes the application's glyphs.</summary>
        internal TextRasterizationMode TextRasterizationMode
            => Options?.TextRasterizationMode ?? RasterizationDefaults.PlatformDefault;

        /// <summary>
        /// The atlas the hardware GPU contexts of the application without an updatable-texture
        /// registration place their subpixel run masks in; contexts with one use the registration's.
        /// </summary>
        internal LcdRunAtlas LcdAtlas
            => Volatile.Read(ref _lcdAtlas) ??
               Interlocked.CompareExchange(ref _lcdAtlas, new LcdRunAtlas(Budget), null) ??
               _lcdAtlas!;

        /// <summary>The diagnostics switches of the application.</summary>
        internal TextRasterDiagnostics Diagnostics { get; } = new();

        /// <summary>Whether the application ended the context.</summary>
        internal bool IsDisposed => _disposed;

        /// <summary>
        /// The state of the calling thread. A drawing context takes it once and hands it to what
        /// draws through it.
        /// </summary>
        internal TextRasterThreadState ForCurrentThread()
        {
            if (!_disposed)
            {
                try
                {
                    return _threads.Value!;
                }
                catch (ObjectDisposedException)
                {
                    // Disposed by the owner's thread meanwhile.
                }
            }

            // Drawing after the application ended keeps working, on state no one else sees.
            return new TextRasterThreadState(this);
        }

        /// <summary>
        /// The states of the threads that drew text for the application so far, for diagnostics that
        /// add up the counters of every thread. Each state is written by its own thread only, so a
        /// reader on another thread sees totals that may lag by the draw in progress.
        /// </summary>
        internal IList<TextRasterThreadState> GetThreadStates()
        {
            if (!_disposed)
            {
                try
                {
                    return _threads.Values;
                }
                catch (ObjectDisposedException)
                {
                    // Disposed by the owner's thread meanwhile.
                }
            }

            return Array.Empty<TextRasterThreadState>();
        }

        /// <summary>
        /// Ends the context with its application: the glyph caches give their memory back and the
        /// typefaces bound to its budget drop their caches.
        /// </summary>
        internal void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _threads.Dispose();
            Budget.Dispose();
        }
    }
}
