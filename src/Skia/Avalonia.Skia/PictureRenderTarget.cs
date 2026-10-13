using System;
using Avalonia.Platform;
using Avalonia.Reactive;
using SkiaSharp;

namespace Avalonia.Skia;

internal class PictureRenderTarget : IDisposable
{
    private readonly ISkiaGpu? _gpu;
    private readonly GRContext? _grContext;
    private readonly Vector _dpi;
    private SKPicture? _picture;

    public PictureRenderTarget(ISkiaGpu? gpu, GRContext? grContext, Vector dpi)
    {
        _gpu = gpu;
        _grContext = grContext;
        _dpi = dpi;
    }

    public SKPicture GetPicture()
    {
        var rv = _picture ?? throw new InvalidOperationException();
        _picture = null;
        return rv;
    }
    
    /// <summary>Begins recording a picture of <paramref name="size"/>.</summary>
    /// <param name="size">The size of the picture, in DIPs.</param>
    /// <param name="scaleToDpi">Whether the picture is recorded in pixels at the target's DPI.</param>
    /// <param name="playbackScale">
    /// The scale the picture is shown at on the device, which text recorded into it rasterizes at
    /// (<see cref="DrawingContextImpl.CreateInfo.PlaybackScale"/>).
    /// </param>
    public IDrawingContextImpl CreateDrawingContext(Size size, bool scaleToDpi = true, Vector? playbackScale = null)
    {
        if (scaleToDpi)
            size *= (_dpi / 96);
        var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0, 0, (float)size.Width,
            (float)size.Height));
        
        canvas.RestoreToCount(-1);
        canvas.ResetMatrix();
            
        var createInfo = new DrawingContextImpl.CreateInfo
        {
            Canvas = canvas,
            ScaleDrawingToDpi = scaleToDpi,
            PlaybackScale = playbackScale,
            Dpi = _dpi,
            DisableSubpixelTextRendering = true,
            GrContext = _grContext,
            Gpu = _gpu,
        };
        return new DrawingContextImpl(createInfo, Disposable.Create(() =>
        {
            _picture = recorder.EndRecording();
            canvas.Dispose();
            recorder.Dispose();
        }));
    }

    public void Dispose() => _picture?.Dispose();
}
