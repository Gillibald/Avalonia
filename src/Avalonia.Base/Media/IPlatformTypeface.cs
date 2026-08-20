using System;
using Avalonia.Metadata;

namespace Avalonia.Media
{
    /// <summary>
    /// Opaque handle to a render backend's typeface, derived from a <see cref="GlyphTypeface"/>'s
    /// font data via <c>IPlatformRenderInterface.CreateTypeface</c> and cached 1:1 on
    /// <see cref="GlyphTypeface.PlatformTypeface"/>, exactly like <see cref="ITextShaperTypeface"/>
    /// on the shaping side. Only the producing backend looks inside.
    /// </summary>
    [NotClientImplementable]
    public interface IPlatformTypeface : IDisposable
    {
    }
}
