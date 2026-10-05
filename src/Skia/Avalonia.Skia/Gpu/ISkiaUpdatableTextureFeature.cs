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
    }

    /// <summary>A single-channel texture of one GPU context that is updated in place.</summary>
    internal interface ISkiaUpdatableTexture : IDisposable
    {
        /// <summary>The texture as Skia draws it; the same image after every update.</summary>
        SKImage Image { get; }

        /// <summary>
        /// Copies <paramref name="rect"/> of <paramref name="source"/>, which holds the whole
        /// texture with rows <paramref name="rowBytes"/> apart, into the same rectangle of the
        /// texture.
        /// </summary>
        /// <remarks>
        /// The update takes effect before any draw Skia has not yet submitted, including draws
        /// of this image recorded earlier: those must only sample texels the update leaves as
        /// they were.
        /// </remarks>
        void Update(PixelRect rect, ReadOnlySpan<byte> source, int rowBytes);
    }

    /// <summary>
    /// The updatable textures registered per <see cref="GRContext"/>, and the glyph mask atlas
    /// that goes with them. Drawing contexts only know their <see cref="GRContext"/>, so the GPU
    /// that made the context registers its feature here once.
    /// </summary>
    internal static class SkiaUpdatableTextures
    {
        private static readonly ConditionalWeakTable<GRContext, Registration> s_registrations = new();

        /// <summary>
        /// Registers <paramref name="feature"/> for <paramref name="context"/>, or removes the
        /// registration when it is <c>null</c>.
        /// </summary>
        /// <param name="context">The context drawing the textures.</param>
        /// <param name="feature">The context's updatable textures, if it has them.</param>
        /// <param name="maskAtlas">
        /// The atlas of the context's glyph masks; <see cref="GlyphMaskAtlas.Shared"/> unless the
        /// caller keeps the context's masks apart from other contexts'. Shared pages change
        /// whenever any typeface adds a glyph, which costs a context with updatable textures the
        /// written rectangle only, so contexts without them keep an atlas per typeface.
        /// </param>
        public static void Register(GRContext context, ISkiaUpdatableTextureFeature? feature,
            GlyphMaskAtlas? maskAtlas = null)
        {
            if (feature is null)
            {
                s_registrations.Remove(context);
                return;
            }

            s_registrations.AddOrUpdate(context, new Registration(feature, maskAtlas ?? GlyphMaskAtlas.Shared));
        }

        /// <summary>The registration of <paramref name="context"/>, unless it has none or is lost.</summary>
        public static Registration? Get(GRContext context) =>
            !IsLost(context) && s_registrations.TryGetValue(context, out var registration) ? registration : null;

        /// <summary>Whether <paramref name="context"/> can no longer draw or own textures.</summary>
        // A disposed context has no handle left to ask; its owner abandons it first.
        public static bool IsLost(GRContext context) => context.Handle == IntPtr.Zero || context.IsAbandoned;

        /// <summary>The updatable textures of one context and the atlas its glyph masks go to.</summary>
        internal sealed class Registration
        {
            public Registration(ISkiaUpdatableTextureFeature feature, GlyphMaskAtlas maskAtlas)
            {
                Feature = feature;
                MaskAtlas = maskAtlas;
            }

            public ISkiaUpdatableTextureFeature Feature { get; }

            public GlyphMaskAtlas MaskAtlas { get; }
        }
    }
}
