using System;
using System.Collections.Generic;
using System.Threading;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// The state managed glyph rasterization keeps for one application: what each thread that draws
    /// text decides from frame to frame (<see cref="ForCurrentThread"/>). The application's
    /// <see cref="FontManager"/> owns it and disposes it with itself, so nothing an application
    /// learned while drawing carries over into the next one in the same process.
    /// </summary>
    /// <remarks>
    /// Code on a render thread finds the services through the locator
    /// (<see cref="TryGetCurrent"/>), once per drawing context, never through
    /// <see cref="FontManager.Current"/>, which would create and register a font manager.
    /// </remarks>
    internal sealed class GlyphRasterServices : IDisposable
    {
        private readonly ThreadLocal<TextRasterThreadState> _threads;
        private volatile bool _disposed;

        public GlyphRasterServices()
        {
            _threads = new ThreadLocal<TextRasterThreadState>(() => new TextRasterThreadState(this), trackAllValues: true);
            Budget = new GlyphCacheBudget(static () => AvaloniaLocator.Current.GetService<FontManagerOptions>(),
                TextRasterizationDefaults.CurrentPlatform());
        }

        /// <summary>
        /// The budget of every glyph cache of the application, limited by
        /// <see cref="FontManagerOptions.GlyphCacheLimitBytes"/> or the platform default.
        /// </summary>
        public GlyphCacheBudget Budget { get; }

        private LcdRunAtlas? _lcdAtlas;

        /// <summary>
        /// The atlas the hardware GPU contexts of the application without an updatable-texture
        /// registration place their subpixel run masks in; contexts with one use the registration's.
        /// </summary>
        public LcdRunAtlas LcdAtlas
            => Volatile.Read(ref _lcdAtlas) ??
               Interlocked.CompareExchange(ref _lcdAtlas, new LcdRunAtlas(Budget), null) ??
               _lcdAtlas!;

        /// <summary>The diagnostics switches of the application.</summary>
        public TextRasterDiagnostics Diagnostics { get; } = new();

        /// <summary>Whether the owner has disposed the services.</summary>
        public bool IsDisposed => _disposed;

        /// <summary>
        /// The services of the current application, or <c>null</c> when it has no font manager.
        /// </summary>
        public static GlyphRasterServices? TryGetCurrent()
            => AvaloniaLocator.Current.GetService<FontManager>()?.RasterServices;

        /// <summary>The services of the current application.</summary>
        /// <exception cref="InvalidOperationException">The application has no font manager.</exception>
        public static GlyphRasterServices Current => TryGetCurrent() ?? throw new InvalidOperationException(
            "Managed glyph rasterization needs the FontManager of the current application; none is registered.");

        /// <summary>
        /// The state of the calling thread. A drawing context takes it once and hands it to what
        /// draws through it.
        /// </summary>
        public TextRasterThreadState ForCurrentThread()
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

            // Drawing after the owner went away keeps working, on state no one else sees.
            return new TextRasterThreadState(this);
        }

        /// <summary>
        /// The states of the threads that drew text for the application so far, for diagnostics that
        /// add up the counters of every thread. Each state is written by its own thread only, so a
        /// reader on another thread sees totals that may lag by the draw in progress.
        /// </summary>
        public IList<TextRasterThreadState> GetThreadStates()
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

        public void Dispose()
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
