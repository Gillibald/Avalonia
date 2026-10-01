namespace Avalonia.OpenGL;

/// <summary>
/// Reports which implementation renders for a GL context when <see cref="GlInterface.Renderer"/>
/// does not tell it, as a browser's WebGL context reports "WebKit WebGL" whatever GPU or software
/// rasterizer is behind it. Render backends query it from the context's <c>TryGetFeature</c> to
/// tell a software rasterizer from a GPU.
/// </summary>
public interface IGlContextRendererInfoFeature
{
    /// <summary>
    /// The renderer's name as the platform reports it, or <c>null</c> when the platform does not
    /// know it.
    /// </summary>
    string? RendererName { get; }
}
