// Copyright (c) The Avalonia Project. All rights reserved.
// Licensed under the MIT license. See licence.md file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Direct2D1.Media;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Controls;
using Avalonia.Controls.Platform.Surfaces;
using Avalonia.Direct2D1.Media.Imaging;
using Avalonia.Rendering;

namespace Avalonia
{
    public static class Direct2DApplicationExtensions
    {
        public static T UseDirect2D1<T>(this T builder) where T : AppBuilderBase<T>, new()
        {
            builder.UseRenderingSubsystem(Direct2D1.Direct2D1Platform.Initialize, "Direct2D1");
            return builder;
        }
    }
}

namespace Avalonia.Direct2D1
{
    using Avalonia.Win32.Interop;

    using SharpDX.Direct3D9;
    using SharpDX.Mathematics.Interop;

    public class Direct2D1Platform : IPlatformRenderInterface
    {
        private static readonly Direct2D1Platform s_instance = new Direct2D1Platform();

        private static SharpDX.Direct2D1.Factory s_d2D1Factory;

        private static SharpDX.DirectWrite.Factory s_dwfactory;

        private static SharpDX.WIC.ImagingFactory s_imagingFactory;

        private static SharpDX.DXGI.Device s_dxgiDevice;

        private static SharpDX.Direct2D1.Device s_d2D1Device;

        private static readonly object s_initLock = new object();
        private static bool s_initialized = false;

        internal static void InitializeDirect2D()
        {
            lock (s_initLock)
            {
                if (s_initialized)
                    return;
#if DEBUG
                try
                {
                    s_d2D1Factory =

                        new SharpDX.Direct2D1.Factory1(SharpDX.Direct2D1.FactoryType.MultiThreaded,
                            SharpDX.Direct2D1.DebugLevel.Error);
                }
                catch
                {
                    //
                }
#endif
                s_dwfactory = new SharpDX.DirectWrite.Factory();
                s_imagingFactory = new SharpDX.WIC.ImagingFactory();
                if (s_d2D1Factory == null)
                    s_d2D1Factory = new SharpDX.Direct2D1.Factory1(SharpDX.Direct2D1.FactoryType.MultiThreaded,
                        SharpDX.Direct2D1.DebugLevel.None);


                var featureLevels = new[]
                {
                    SharpDX.Direct3D.FeatureLevel.Level_11_1,
                    SharpDX.Direct3D.FeatureLevel.Level_11_0,
                    SharpDX.Direct3D.FeatureLevel.Level_10_1,
                    SharpDX.Direct3D.FeatureLevel.Level_10_0,
                    SharpDX.Direct3D.FeatureLevel.Level_9_3,
                    SharpDX.Direct3D.FeatureLevel.Level_9_2,
                    SharpDX.Direct3D.FeatureLevel.Level_9_1,
                };

                using (var d3dDevice = new SharpDX.Direct3D11.Device(
                    SharpDX.Direct3D.DriverType.Hardware,
                    SharpDX.Direct3D11.DeviceCreationFlags.BgraSupport |
                    SharpDX.Direct3D11.DeviceCreationFlags.VideoSupport,
                    featureLevels))
                {
                    s_dxgiDevice = d3dDevice.QueryInterface<SharpDX.DXGI.Device>();
                }

                using (var factory1 = s_d2D1Factory.QueryInterface<SharpDX.Direct2D1.Factory1>())
                {
                    s_d2D1Device = new SharpDX.Direct2D1.Device(factory1, s_dxgiDevice);
                }
                s_initialized = true;
            }
        }

        public static void Initialize()
        {
            InitializeDirect2D();
            AvaloniaLocator.CurrentMutable
                        .Bind<IPlatformRenderInterface>().ToConstant(s_instance)
                        .BindToSelf(s_d2D1Factory)
                        .BindToSelf(s_dwfactory)
                        .BindToSelf(s_imagingFactory)
                        .BindToSelf(s_dxgiDevice)
                        .BindToSelf(s_d2D1Device);
            SharpDX.Configuration.EnableReleaseOnFinalizer = true;
        }

        public IBitmapImpl CreateBitmap(int width, int height)
        {
            return new WicBitmapImpl(s_imagingFactory, width, height);
        }

        public IFormattedTextImpl CreateFormattedText(
            string text,
            Typeface typeface,
            TextAlignment textAlignment,
            TextWrapping wrapping,
            Size constraint,
            IReadOnlyList<FormattedTextStyleSpan> spans)
        {
            return new FormattedTextImpl(
                text,
                typeface,
                textAlignment,
                wrapping,
                constraint,
                spans);
        }

        Surface renderTarget = null;
        Surface offscreenPlainSurface = null;
        Device d3dDevice = AvaloniaLocator.Current.GetService<Device>();

        private void Render(IntPtr hWnd)
        {
            UnmanagedMethods.GetWindowRect(hWnd, out var rect);

            var windowWidth = rect.right - rect.left;
            var windowHeight = rect.bottom - rect.top;

            if (offscreenPlainSurface == null)
            {
                offscreenPlainSurface = Surface.CreateOffscreenPlain(d3dDevice, windowWidth, windowHeight, Format.X8R8G8B8, Pool.SystemMemory);
                renderTarget = Surface.CreateRenderTarget(d3dDevice, windowWidth, windowHeight, Format.X8R8G8B8, MultisampleType.None, 0, false);
            }
            else
            {
                var surfaceDescription = offscreenPlainSurface.Description;

                if (surfaceDescription.Width != windowWidth || surfaceDescription.Width != windowHeight)
                {
                    offscreenPlainSurface.Dispose();
                    renderTarget.Dispose();
                    offscreenPlainSurface = Surface.CreateOffscreenPlain(d3dDevice, windowWidth, windowHeight, Format.X8R8G8B8, Pool.SystemMemory);
                    renderTarget = Surface.CreateRenderTarget(d3dDevice, windowWidth, windowHeight, Format.X8R8G8B8, MultisampleType.None, 0, false);
                }
            }

            if (offscreenPlainSurface == null || renderTarget == null)
            {
                return;
            }

            d3dDevice.SetRenderTarget(0, renderTarget);
            d3dDevice.Clear(ClearFlags.Target, new RawColorBGRA(), 0, 0);

            d3dDevice.BeginScene();

            //Draw stuff

            d3dDevice.EndScene();

            d3dDevice.Present();

            d3dDevice.GetRenderTargetData(renderTarget, offscreenPlainSurface);

            IntPtr deviceContext = offscreenPlainSurface.GetDC();
            UnmanagedMethods.Point point = new UnmanagedMethods.Point(0, 0);
            UnmanagedMethods.Size size = new UnmanagedMethods.Size(windowWidth, windowHeight);

            UnmanagedMethods.BLENDFUNCTION blend;
            blend.AlphaFormat = UnmanagedMethods.AC_SRC_ALPHA;
            blend.SourceConstantAlpha = 0;
            blend.BlendFlags = 0;
            blend.BlendOp = UnmanagedMethods.AC_SRC_OVER;

            UnmanagedMethods.UpdateLayeredWindow(
                hWnd,
                IntPtr.Zero,
                ref point,
                ref size,
                deviceContext,
                ref point,
                0,
                ref blend,
                UnmanagedMethods.ULW_ALPHA);

            offscreenPlainSurface.ReleaseDC(deviceContext);
        }      

        public IRenderTarget CreateRenderTarget(IEnumerable<object> surfaces)
        {
            foreach (var s in surfaces)
            {
                if (s is IPlatformHandle nativeWindow)
                {
                    if (nativeWindow.HandleDescriptor != "HWND")
                        throw new NotSupportedException("Don't know how to create a Direct2D1 renderer from " +
                                                        nativeWindow.HandleDescriptor);
                    return new HwndRenderTarget(nativeWindow);
                }
                if (s is IExternalDirect2DRenderTargetSurface external)
                    return new ExternalRenderTarget(external, s_dwfactory, s_imagingFactory);
                if (s is IFramebufferPlatformSurface fb)
                    return new FramebufferShimRenderTarget(fb, s_imagingFactory, s_d2D1Factory, s_dwfactory);
            }
            throw new NotSupportedException("Don't know how to create a Direct2D1 renderer from any of provided surfaces");
        }

        public IRenderTargetBitmapImpl CreateRenderTargetBitmap(
            int width,
            int height,
            double dpiX,
            double dpiY)
        {
            return new WicRenderTargetBitmapImpl(
                s_imagingFactory,
                s_d2D1Factory,
                s_dwfactory,
                width,
                height,
                dpiX,
                dpiY);
        }

        public IWriteableBitmapImpl CreateWriteableBitmap(int width, int height, PixelFormat? format = null)
        {
            return new WriteableWicBitmapImpl(s_imagingFactory, width, height, format);
        }

        public IStreamGeometryImpl CreateStreamGeometry()
        {
            return new StreamGeometryImpl();
        }

        public IBitmapImpl LoadBitmap(string fileName)
        {
            return new WicBitmapImpl(s_imagingFactory, fileName);
        }

        public IBitmapImpl LoadBitmap(Stream stream)
        {
            return new WicBitmapImpl(s_imagingFactory, stream);
        }

        public IBitmapImpl LoadBitmap(PixelFormat format, IntPtr data, int width, int height, int stride)
        {
            return new WicBitmapImpl(s_imagingFactory, format, data, width, height, stride);
        }
    }
}