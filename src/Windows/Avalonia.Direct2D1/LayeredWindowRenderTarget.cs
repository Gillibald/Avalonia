// Copyright (c) The Avalonia Project. All rights reserved.
// Licensed under the MIT license. See licence.md file in the project root for full license information.

using Avalonia.Direct2D1.Media;
using Avalonia.Direct2D1.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Win32.Interop;

using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.WIC;

namespace Avalonia.Direct2D1
{
    public class LayeredWindowRenderTarget : IRenderTarget, ILayerFactory
    {
        private readonly IPlatformHandle _window;
        private Size2 _savedSize;
        private Size2F _savedDpi;
        private SwapChain _swapChain;
        private SharpDX.Direct2D1.RenderTarget _renderTarget;

        public LayeredWindowRenderTarget(IPlatformHandle window)
        {
            _window = window;

            UnmanagedMethods.SetLayeredWindowAttributes(
                _window.Handle,
                0,
                byte.MaxValue,
                UnmanagedMethods.BlendFlags.ULW_COLORKEY);          

            D2D1Device = AvaloniaLocator.Current.GetService<SharpDX.Direct2D1.Device>();
            DxgiDevice = AvaloniaLocator.Current.GetService<SharpDX.DXGI.Device>();
            D2D1Factory = AvaloniaLocator.Current.GetService<SharpDX.Direct2D1.Factory>();
            DirectWriteFactory = AvaloniaLocator.Current.GetService<SharpDX.DirectWrite.Factory>();
            WicImagingFactory = AvaloniaLocator.Current.GetService<ImagingFactory>();
        }

        public ImagingFactory WicImagingFactory { get; }

        public SharpDX.DirectWrite.Factory DirectWriteFactory { get; }

        public SharpDX.Direct2D1.Device D2D1Device { get; }

        public SharpDX.DXGI.Device DxgiDevice { get; }

        public SharpDX.Direct2D1.Factory D2D1Factory { get; }

        public void Dispose()
        {
            this._renderTarget?.Dispose();

            this._swapChain?.Dispose();
        }

        public IDrawingContextImpl CreateDrawingContext(IVisualBrushRenderer visualBrushRenderer)
        {
            var size = GetWindowSize();
            var dpi = GetWindowDpi();

            if (size != _savedSize || dpi != _savedDpi)
            {
                _savedSize = size;
                _savedDpi = dpi;             

                this.CreateSwapChain();
            }

            return new DrawingContextImpl(
                visualBrushRenderer,
                this,
                _renderTarget,
                DirectWriteFactory,
                WicImagingFactory,
                _swapChain);
        }

        public IRenderTargetBitmapImpl CreateLayer(Size size)
        {
            if (this._renderTarget == null)
            {
                CreateSwapChain();
            }

            return D2DRenderTargetBitmapImpl.CreateCompatible(
                WicImagingFactory,
                DirectWriteFactory,
                _renderTarget,
                size);
        }

        private void CreateSwapChain()
        {
            //var margins = new UnmanagedMethods.MARGINS(-1, -1, -1, -1);

            //UnmanagedMethods.DwmExtendFrameIntoClientArea(this._window.Handle, ref margins);

            var swapChainDescription = new SwapChainDescription1
            {
                Width = _savedSize.Width,
                Height = _savedSize.Height,
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription
                {
                    Count = 1,
                    Quality = 0,
                },
                Usage = Usage.RenderTargetOutput,
                BufferCount = 1,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.Discard,
                Flags = SwapChainFlags.GdiCompatible,
            };

            using (var dxgiAdapter = DxgiDevice.Adapter)
            using (var dxgiFactory = dxgiAdapter.GetParent<SharpDX.DXGI.Factory2>())
            {
                _swapChain?.Dispose();
                _swapChain = new SwapChain1(dxgiFactory, DxgiDevice, _window.Handle, ref swapChainDescription);

                using (var backBuffer = SharpDX.Direct3D11.Resource.FromSwapChain<Texture2D>(_swapChain, 0))
                using (var surface = backBuffer.QueryInterface<Surface>())
                {
                    _renderTarget?.Dispose();
                    _renderTarget = new SharpDX.Direct2D1.RenderTarget(
                        D2D1Factory,
                        surface,
                        new RenderTargetProperties
                        {
                            DpiX = _savedDpi.Width,
                            DpiY = _savedDpi.Height,
                            MinLevel = FeatureLevel.Level_10,
                            PixelFormat =
                                    new SharpDX.Direct2D1.PixelFormat
                                    {
                                        AlphaMode = SharpDX.Direct2D1.AlphaMode.Premultiplied,
                                        Format = Format.B8G8R8A8_UNorm
                                    },
                            Type = RenderTargetType.Default,
                            Usage = RenderTargetUsage.GdiCompatible
                        });
                }
            }
        }

        private Size2F GetWindowDpi()
        {
            if (UnmanagedMethods.ShCoreAvailable)
            {
                var monitor = UnmanagedMethods.MonitorFromWindow(
                    _window.Handle,
                    UnmanagedMethods.MONITOR.MONITOR_DEFAULTTONEAREST);

                if (UnmanagedMethods.GetDpiForMonitor(
                        monitor,
                        UnmanagedMethods.MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI,
                        out var dpiX,
                        out var dpiY) == 0)
                {
                    return new Size2F(dpiX, dpiY);
                }
            }

            return new Size2F(96, 96);
        }

        private Size2 GetWindowSize()
        {
            UnmanagedMethods.GetClientRect(_window.Handle, out var rc);
            return new Size2(rc.right - rc.left, rc.bottom - rc.top);
        }
    }
}
