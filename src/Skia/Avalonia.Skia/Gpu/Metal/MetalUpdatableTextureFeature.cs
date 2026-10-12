using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Avalonia.Skia.Metal;

/// <summary>
/// R8 and RGBA8 Metal textures of one Metal-backed <see cref="GRContext"/>, updated with
/// <c>replaceRegion:mipmapLevel:withBytes:bytesPerRow:</c>.
/// </summary>
/// <remarks>
/// <para>
/// The textures live in memory the CPU writes directly: shared storage where the device has
/// unified memory (Apple silicon), managed storage otherwise (Intel Macs with a discrete GPU),
/// whose CPU copy Metal brings to the GPU before the next command buffer reads it. A region copy
/// runs on the CPU at once, before Skia commits the command buffer it is recording, so draws of
/// the texture recorded earlier in the frame see the new texels; command buffers already in
/// flight only sample texels an update leaves as they were.
/// </para>
/// <para>
/// A texture is made with one reference, which Skia's release callback for its image gives up;
/// Skia keeps its own reference while it uses the texture. Disposing the feature after the
/// context gives up the references of the images Skia never released.
/// </para>
/// </remarks>
internal sealed unsafe partial class MetalUpdatableTextureFeature : ISkiaUpdatableTextureFeature, IDisposable
{
    private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";

    private const nuint PixelFormatR8Unorm = 10;
    private const nuint PixelFormatRgba8Unorm = 70;
    private const nuint UsageShaderRead = 1;
    private const nuint StorageModeShared = 0;
    private const nuint StorageModeManaged = 1;

    private static readonly SKImageTextureReleaseDelegate s_release = static state => ((Texture)state).Release();

    private static readonly IntPtr s_descriptorClass = objc_getClass("MTLTextureDescriptor");
    private static readonly IntPtr s_texture2DDescriptor = sel_registerName("texture2DDescriptorWithPixelFormat:width:height:mipmapped:");
    private static readonly IntPtr s_setUsage = sel_registerName("setUsage:");
    private static readonly IntPtr s_setStorageMode = sel_registerName("setStorageMode:");
    private static readonly IntPtr s_newTexture = sel_registerName("newTextureWithDescriptor:");
    private static readonly IntPtr s_replaceRegion = sel_registerName("replaceRegion:mipmapLevel:withBytes:bytesPerRow:");
    private static readonly IntPtr s_hasUnifiedMemory = sel_registerName("hasUnifiedMemory");
    private static readonly IntPtr s_releaseSelector = sel_registerName("release");

    private static readonly IntPtr s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjCLibrary), "objc_msgSend");

    private readonly GRContext _context;
    private readonly IntPtr _device;
    private readonly nuint _storageMode;
    private readonly object _lock = new();
    private readonly HashSet<Texture> _textures = new();
    private bool _disposed;

    private MetalUpdatableTextureFeature(GRContext context, IntPtr device, bool unifiedMemory)
    {
        _context = context;
        _device = device;
        _storageMode = SelectStorageMode(unifiedMemory, OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst());
    }

    /// <summary>
    /// The storage mode of the textures: shared where the device has unified memory, managed
    /// otherwise, on platforms that have managed storage.
    /// </summary>
    internal static nuint SelectStorageMode(bool unifiedMemory, bool managedStorageAvailable)
        => unifiedMemory ? StorageModeShared : StorageModeManaged;

    /// <summary>Makes the feature for <paramref name="context"/> on <paramref name="device"/>.</summary>
    /// <returns>The feature, or <c>null</c> when the device is not available.</returns>
    public static MetalUpdatableTextureFeature? TryCreate(GRContext context, IntPtr device)
    {
        if (device == IntPtr.Zero || s_descriptorClass == IntPtr.Zero)
        {
            return null;
        }

        var unifiedMemory = ((delegate* unmanaged<IntPtr, IntPtr, byte>)s_msgSend)(device, s_hasUnifiedMemory) != 0;

        return new MetalUpdatableTextureFeature(context, device, unifiedMemory);
    }

    public ISkiaUpdatableTexture? TryCreateAlpha8(int width, int height, ReadOnlySpan<byte> pixels, int rowBytes)
        => TryCreate(width, height, pixels, rowBytes, 1);

    public ISkiaUpdatableTexture? TryCreateRgba8(int width, int height, ReadOnlySpan<byte> pixels, int rowBytes)
        => TryCreate(width, height, pixels, rowBytes, 4);

    private ISkiaUpdatableTexture? TryCreate(int width, int height, ReadOnlySpan<byte> pixels, int rowBytes,
        int bytesPerPixel)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var rgba = bytesPerPixel == 4;
        IntPtr handle;

        lock (_lock)
        {
            if (_disposed)
            {
                return null;
            }

            // The descriptor is autoreleased.
            using (new AutoReleasePool())
            {
                var descriptor = ((delegate* unmanaged<IntPtr, IntPtr, nuint, nuint, nuint, byte, IntPtr>)s_msgSend)(
                    s_descriptorClass, s_texture2DDescriptor, rgba ? PixelFormatRgba8Unorm : PixelFormatR8Unorm,
                    (nuint)width, (nuint)height, 0);

                if (descriptor == IntPtr.Zero)
                {
                    return null;
                }

                ((delegate* unmanaged<IntPtr, IntPtr, nuint, void>)s_msgSend)(descriptor, s_setUsage, UsageShaderRead);
                ((delegate* unmanaged<IntPtr, IntPtr, nuint, void>)s_msgSend)(descriptor, s_setStorageMode, _storageMode);

                handle = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr>)s_msgSend)(_device, s_newTexture, descriptor);
            }

            if (handle == IntPtr.Zero)
            {
                return null;
            }

            ReplaceRegion(handle, bytesPerPixel, new PixelRect(0, 0, width, height), pixels, rowBytes);
        }

        var texture = new Texture(this, handle, bytesPerPixel);

        lock (_lock)
        {
            _textures.Add(texture);
        }

        SKImage? image;

        using (var backend = new GRBackendTexture(width, height, false, new GRMtlTextureInfo(handle)))
        {
            image = SKImage.FromTexture(_context, backend, GRSurfaceOrigin.TopLeft,
                rgba ? SKColorType.Rgba8888 : SKColorType.Alpha8, SKAlphaType.Premul, null, s_release, texture);
        }

        if (image is null)
        {
            texture.Release();
            return null;
        }

        texture.Image = image;

        return texture;
    }

    /// <summary>Copies <paramref name="rect"/> of <paramref name="source"/> into the texture.</summary>
    private static void ReplaceRegion(IntPtr texture, int bytesPerPixel, PixelRect rect, ReadOnlySpan<byte> source,
        int rowBytes)
    {
        var region = new MTLRegion
        {
            X = (nuint)rect.X,
            Y = (nuint)rect.Y,
            Width = (nuint)rect.Width,
            Height = (nuint)rect.Height,
            Depth = 1,
        };

        fixed (byte* p = source)
        {
            ((delegate* unmanaged<IntPtr, IntPtr, MTLRegion, nuint, byte*, nuint, void>)s_msgSend)(
                texture, s_replaceRegion, region, 0, p + rect.Y * rowBytes + rect.X * bytesPerPixel, (nuint)rowBytes);
        }
    }

    /// <summary>
    /// Gives up the references of the textures Skia has not released. Images released later free
    /// nothing: a disposed context took its own references along.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (var texture in _textures)
            {
                Release(texture.Handle);
            }

            _textures.Clear();
        }
    }

    private static void Release(IntPtr handle) =>
        ((delegate* unmanaged<IntPtr, IntPtr, void>)s_msgSend)(handle, s_releaseSelector);

    [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_getClass(string name);

    [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr sel_registerName(string name);

    /// <summary>A region of a texture: origin x, y, z and size width, height, depth.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MTLRegion
    {
        public nuint X;
        public nuint Y;
        public nuint Z;
        public nuint Width;
        public nuint Height;
        public nuint Depth;
    }

    /// <summary>One texture of the device, wrapped for Skia.</summary>
    private sealed class Texture : ISkiaUpdatableTexture
    {
        private readonly MetalUpdatableTextureFeature _owner;
        private readonly int _bytesPerPixel;
        private bool _released;

        public Texture(MetalUpdatableTextureFeature owner, IntPtr handle, int bytesPerPixel)
        {
            _owner = owner;
            Handle = handle;
            _bytesPerPixel = bytesPerPixel;
        }

        public IntPtr Handle { get; }

        public SKImage Image { get; set; } = null!;

        public void Update(PixelRect rect, ReadOnlySpan<byte> source, int rowBytes)
        {
            lock (_owner._lock)
            {
                if (!_released && !_owner._disposed)
                {
                    ReplaceRegion(Handle, _bytesPerPixel, rect, source, rowBytes);
                }
            }
        }

        /// <summary>Drops the wrapper; the texture goes once Skia no longer uses it.</summary>
        public void Dispose() => Image.Dispose();

        /// <summary>Gives up the texture's reference; Skia calls this when it releases the image.</summary>
        public void Release()
        {
            lock (_owner._lock)
            {
                if (_released || _owner._disposed)
                {
                    return;
                }

                _released = true;
                _owner._textures.Remove(this);
                MetalUpdatableTextureFeature.Release(Handle);
            }
        }
    }
}
