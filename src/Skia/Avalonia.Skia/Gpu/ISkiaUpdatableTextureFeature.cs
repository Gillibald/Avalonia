using System;
using System.Runtime.CompilerServices;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// Single-channel textures of one <see cref="GRContext"/> that can be updated in place, a
    /// rectangle at a time, from CPU memory. An <see cref="ISkiaGpu"/> that can do this returns
    /// the feature from <c>TryGetFeature</c> and registers it for its context with
    /// <see cref="SkiaUpdatableTextures.Register"/>.
    /// </summary>
    /// <remarks>
    /// Skia offers no partial upload into a texture it wraps, so each backend writes through its
    /// own API and keeps Skia's view of the texture unchanged: the image is made once and stays
    /// the same object across updates.
    /// </remarks>
    internal interface ISkiaUpdatableTextureFeature
    {
        /// <summary>
        /// Makes an A8 texture holding <paramref name="height"/> rows of <paramref name="pixels"/>,
        /// rows <paramref name="rowBytes"/> apart, wrapped once as an image of the context.
        /// </summary>
        /// <returns>The texture, or <c>null</c> when the backend or Skia refuse it.</returns>
        ISkiaUpdatableTexture? TryCreateAlpha8(int width, int height, ReadOnlySpan<byte> pixels, int rowBytes);

        /// <summary>
        /// Makes a premultiplied RGBA texture holding <paramref name="height"/> rows of
        /// <paramref name="pixels"/>, four bytes per texel in R, G, B, A order, rows
        /// <paramref name="rowBytes"/> apart, wrapped once as an image of the context.
        /// </summary>
        /// <returns>The texture, or <c>null</c> when the backend or Skia refuse it.</returns>
        ISkiaUpdatableTexture? TryCreateRgba8(int width, int height, ReadOnlySpan<byte> pixels, int rowBytes);
    }

    /// <summary>A single-channel texture of one GPU context that is updated in place.</summary>
    internal interface ISkiaUpdatableTexture : IDisposable
    {
        /// <summary>The texture as Skia draws it; the same image after every update.</summary>
        SKImage Image { get; }

        /// <summary>
        /// Copies the texels of every region from <paramref name="staging"/> into its rectangle of
        /// the texture, as one piece of backend work: one queue submission on Vulkan.
        /// </summary>
        /// <remarks>
        /// <paramref name="staging"/> is read before the call returns, so the caller may reuse it
        /// at once. The update takes effect before any draw Skia has not yet submitted, including
        /// draws of this image recorded earlier: those must only sample texels the update leaves
        /// as they were.
        /// </remarks>
        void Update(ReadOnlySpan<TextureRegion> regions, ReadOnlySpan<byte> staging);
    }

    /// <summary>
    /// A rectangle of an updatable texture and where its texels are in a staging buffer: the
    /// first texel at <see cref="Offset"/>, rows <see cref="RowBytes"/> apart.
    /// </summary>
    internal readonly record struct TextureRegion(PixelRect Target, int Offset, int RowBytes);

    /// <summary>
    /// The updatable textures registered per <see cref="GRContext"/>, and the glyph mask atlases
    /// that go with them. Drawing contexts only know their <see cref="GRContext"/>, so the GPU
    /// that made the context registers its feature here once, and removes it when it is disposed.
    /// </summary>
    internal static class SkiaUpdatableTextures
    {
        private static readonly ConditionalWeakTable<GRContext, Registration> s_registrations = new();
        private static readonly object s_lock = new();

        /// <summary>
        /// Registers <paramref name="feature"/> for <paramref name="context"/>, or removes the
        /// registration when it is <c>null</c>. A registration replaced or removed releases the
        /// atlases it made.
        /// </summary>
        /// <param name="context">The context drawing the textures.</param>
        /// <param name="feature">The context's updatable textures, if it has them.</param>
        /// <param name="maskAtlas">
        /// The atlas of the context's glyph masks, or <c>null</c> for one made for the context in
        /// each application that draws on it, charged to that application's glyph cache budget,
        /// whose pages keep no CPU copy when <paramref name="residentPages"/> is on. The
        /// atlas holds the masks of every typeface: its pages change whenever any typeface adds a
        /// glyph, which costs a context with updatable textures what was written only, so
        /// contexts without them keep an atlas per typeface. A resident page lives in a texture of
        /// this context alone, so every context has atlases of its own.
        /// </param>
        /// <param name="colorAtlas">
        /// The atlas of the context's colour glyph content, premultiplied RGBA pages, by the same
        /// rule as <paramref name="maskAtlas"/>.
        /// </param>
        /// <param name="residentPages">
        /// Whether the atlases made for the context keep no CPU copy of their pages
        /// (<see cref="SkiaOptions.ResidentGlyphPages"/>).
        /// </param>
        public static void Register(GRContext context, ISkiaUpdatableTextureFeature? feature,
            GlyphMaskAtlas? maskAtlas = null, GlyphMaskAtlas? colorAtlas = null, bool residentPages = true)
        {
            Registration? previous;

            lock (s_lock)
            {
                s_registrations.TryGetValue(context, out previous);

                if (feature is null)
                {
                    s_registrations.Remove(context);
                }
                else
                {
                    s_registrations.AddOrUpdate(context, new Registration(feature, maskAtlas, colorAtlas, residentPages));
                }
            }

            // Registration changes happen on the thread that drives the context, the thread the
            // page textures must be freed on.
            previous?.ReleaseOwnedAtlases();
            previous?.PageTextures.Clear();
        }

        /// <summary>The registration of <paramref name="context"/>, unless it has none or is lost.</summary>
        public static Registration? Get(GRContext context) =>
            !IsLost(context) && s_registrations.TryGetValue(context, out var registration) ? registration : null;

        /// <summary>Whether <paramref name="context"/> can no longer draw or own textures.</summary>
        // A disposed context has no handle left to ask; its owner abandons it first.
        public static bool IsLost(GRContext context) => context.Handle == IntPtr.Zero || context.IsAbandoned;

        /// <summary>
        /// The updatable textures of one context and the atlases its glyph masks go to. Atlases the
        /// registration was not given are made for the application whose drawing contexts first ask
        /// for them, charged to its glyph cache budget, and made again for the next application.
        /// </summary>
        internal sealed class Registration
        {
            private readonly object _lock = new();
            private readonly GlyphMaskAtlas? _maskAtlas;
            private readonly GlyphMaskAtlas? _colorAtlas;
            private GlyphRasterServices? _services;
            private GlyphMaskAtlas? _ownMaskAtlas;
            private GlyphMaskAtlas? _ownColorAtlas;
            private LcdRunAtlas? _lcdAtlas;

            /// <param name="feature">The context's updatable textures.</param>
            /// <param name="maskAtlas">The atlas of the context's glyph masks, or <c>null</c> to make one per application.</param>
            /// <param name="colorAtlas">The atlas of its colour glyph content, or <c>null</c> to make one per application.</param>
            /// <param name="residentPages">Whether atlases made here keep no CPU copy of their pages.</param>
            public Registration(ISkiaUpdatableTextureFeature feature, GlyphMaskAtlas? maskAtlas,
                GlyphMaskAtlas? colorAtlas, bool residentPages)
            {
                Feature = feature;
                _maskAtlas = maskAtlas;
                _colorAtlas = colorAtlas;
                ResidentPages = residentPages;
            }

            public ISkiaUpdatableTextureFeature Feature { get; }

            /// <summary>Whether the atlases made for the registration keep no CPU copy of their pages.</summary>
            public bool ResidentPages { get; }

            /// <summary>
            /// The glyph page textures made on the context, freed with the registration when the
            /// context goes.
            /// </summary>
            public DrawingContextImpl.PageTextureSet PageTextures { get; } = new();

            /// <summary>The atlas glyph masks drawn for <paramref name="services"/>' application go to.</summary>
            public GlyphMaskAtlas MaskAtlasFor(GlyphRasterServices services)
            {
                lock (_lock)
                {
                    return _maskAtlas ?? EnsureOwnAtlases(services).Mask;
                }
            }

            /// <summary>The atlas colour glyph content drawn for <paramref name="services"/>' application goes to.</summary>
            public GlyphMaskAtlas ColorAtlasFor(GlyphRasterServices services)
            {
                lock (_lock)
                {
                    return _colorAtlas ?? EnsureOwnAtlases(services).Color;
                }
            }

            /// <summary>
            /// The atlas the subpixel run masks drawn on the context for <paramref name="services"/>'
            /// application go to, made for that application.
            /// </summary>
            public LcdRunAtlas LcdAtlasFor(GlyphRasterServices services)
            {
                lock (_lock)
                {
                    EnsureOwnAtlases(services);

                    return _lcdAtlas!;
                }
            }

            /// <summary>Releases the atlases made for the registration: their pages and budget charges go.</summary>
            public void ReleaseOwnedAtlases()
            {
                lock (_lock)
                {
                    ReleaseOwn();
                }
            }

            // Called under the lock.
            private (GlyphMaskAtlas Mask, GlyphMaskAtlas Color) EnsureOwnAtlases(GlyphRasterServices services)
            {
                if (_services != services || services.IsDisposed || _ownMaskAtlas is null || _ownColorAtlas is null)
                {
                    ReleaseOwn();
                    _services = services;
                    _ownMaskAtlas = new GlyphMaskAtlas(services.Budget, residentPages: ResidentPages);
                    _ownColorAtlas = new GlyphMaskAtlas(services.Budget, format: GlyphAtlasFormat.Rgba,
                        residentPages: ResidentPages);
                    _lcdAtlas = new LcdRunAtlas(services.Budget);
                }

                return (_ownMaskAtlas, _ownColorAtlas);
            }

            // Called under the lock.
            private void ReleaseOwn()
            {
                _ownMaskAtlas?.Release();
                _ownColorAtlas?.Release();
                _lcdAtlas?.Release();
                _ownMaskAtlas = null;
                _ownColorAtlas = null;
                _lcdAtlas = null;
                _services = null;
            }
        }
    }
}
