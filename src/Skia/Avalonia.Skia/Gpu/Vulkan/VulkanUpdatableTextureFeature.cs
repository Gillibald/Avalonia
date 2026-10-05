using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia.Vulkan;
using SkiaSharp;

namespace Avalonia.Skia.Vulkan;

/// <summary>
/// R8 Vulkan images of one Vulkan-backed <see cref="GRContext"/>, updated through a staging
/// buffer and <c>vkCmdCopyBufferToImage</c> on the device's main queue.
/// </summary>
/// <remarks>
/// <para>
/// An image stays in <c>SHADER_READ_ONLY_OPTIMAL</c> whenever Skia can see it, and Skia is told
/// so when the image is wrapped. Skia keeps its own record of a wrapped image's layout and
/// records no barrier when it samples an image already in that read-only layout; it would
/// move the image only to copy or read from it, which nothing does with these images, so its
/// record stays true. An update starts from that layout and returns to it: it moves the image to
/// <c>TRANSFER_DST_OPTIMAL</c>, copies the rectangle and moves it back, all in one command
/// buffer submitted at once: queue order puts the copy after every draw Skia submitted before
/// and before every draw it has recorded but not yet submitted, and the barriers order the copy
/// against the reads of both.
/// </para>
/// <para>
/// Each submission has its own host-visible staging buffer and fence in a small ring; a slot is
/// reused once its fence has signalled. The entry points are resolved through the instance's
/// public proc-address lookup, and everything that touches the queue or the command pool runs
/// under the device lock. Skia calls an image's release callback once its own command buffers
/// using the image have finished; the image is destroyed then, after any copy into it.
/// </para>
/// </remarks>
internal sealed unsafe class VulkanUpdatableTextureFeature : ISkiaUpdatableTextureFeature, IDisposable
{
    private const uint FormatR8Unorm = 9;
    private const uint ImageType2D = 1;
    private const uint TilingOptimal = 0;
    private const uint UsageTransferSrc = 0x1;
    private const uint UsageTransferDst = 0x2;
    private const uint UsageSampled = 0x4;

    // Skia wraps only images with both transfer usages, though it never copies from these.
    private const uint ImageUsage = UsageTransferSrc | UsageTransferDst | UsageSampled;
    private const uint LayoutUndefined = 0;
    private const uint LayoutShaderReadOnly = 5;
    private const uint LayoutTransferDst = 7;
    private const uint AccessShaderRead = 0x20;
    private const uint AccessTransferWrite = 0x1000;
    private const uint StageTopOfPipe = 0x1;
    private const uint StageFragmentShader = 0x80;
    private const uint StageTransfer = 0x1000;
    private const uint MemoryDeviceLocal = 0x1;
    private const uint MemoryHostVisible = 0x2;
    private const uint MemoryHostCoherent = 0x4;
    private const uint AspectColor = 0x1;
    private const uint QueueFamilyIgnored = ~0u;
    private const uint CommandPoolResetCommandBuffer = 0x2;
    private const uint CommandBufferOneTimeSubmit = 0x1;
    private const int SlotCount = 4;
    private const int MinStagingBytes = 64 * 1024;

    // A whole page goes up when a texture is made, up to 2 MB; a slot keeps at most this much
    // afterwards, enough for the rectangles of ordinary updates.
    private const int MaxRetainedStagingBytes = 256 * 1024;

    private const uint StructureSubmitInfo = 4;
    private const uint StructureMemoryAllocateInfo = 5;
    private const uint StructureFenceCreateInfo = 8;
    private const uint StructureBufferCreateInfo = 12;
    private const uint StructureImageCreateInfo = 14;
    private const uint StructureCommandPoolCreateInfo = 39;
    private const uint StructureCommandBufferAllocateInfo = 40;
    private const uint StructureCommandBufferBeginInfo = 42;
    private const uint StructureImageMemoryBarrier = 45;

    private static readonly SKImageTextureReleaseDelegate s_release = static state => ((Texture)state).Release();

    private readonly IVulkanPlatformGraphicsContext _vulkan;
    private readonly GRContext _context;
    private readonly IntPtr _device;
    private readonly uint _queueFamily;
    private readonly Api _api;
    private readonly uint[] _memoryTypeFlags;
    private readonly Slot[] _slots = new Slot[SlotCount];
    private readonly HashSet<Texture> _textures = new();
    private ulong _commandPool;
    private int _nextSlot;
    private ulong _submitted;
    private bool _failed;
    private bool _disposed;

    private VulkanUpdatableTextureFeature(IVulkanPlatformGraphicsContext vulkan, GRContext context, Api api,
        uint[] memoryTypeFlags)
    {
        _vulkan = vulkan;
        _context = context;
        _device = vulkan.Device.Handle;
        _queueFamily = vulkan.Device.GraphicsQueueFamilyIndex;
        _api = api;
        _memoryTypeFlags = memoryTypeFlags;
    }

    /// <summary>
    /// Resolves the entry points and makes the command pool for <paramref name="context"/>.
    /// </summary>
    /// <returns>The feature, or <c>null</c> when an entry point is missing or the pool fails.</returns>
    public static VulkanUpdatableTextureFeature? TryCreate(IVulkanPlatformGraphicsContext vulkan, GRContext context)
    {
        var device = vulkan.Device;

        if (!Api.TryResolve(vulkan, out var api))
        {
            return null;
        }

        // VkPhysicalDeviceMemoryProperties: a uint32 type count, then 32 types of two uint32
        // (property flags, heap index), then the heaps; 1 KB holds the whole struct.
        var properties = stackalloc byte[1024];

        api.GetPhysicalDeviceMemoryProperties(device.PhysicalDeviceHandle, properties);

        var count = Math.Min(*(uint*)properties, 32u);
        var flags = new uint[count];

        for (var i = 0; i < count; i++)
        {
            flags[i] = *(uint*)(properties + 4 + i * 8);
        }

        var feature = new VulkanUpdatableTextureFeature(vulkan, context, api, flags);

        using (device.Lock())
        {
            var info = new VkCommandPoolCreateInfo
            {
                sType = StructureCommandPoolCreateInfo,
                flags = CommandPoolResetCommandBuffer,
                queueFamilyIndex = feature._queueFamily,
            };

            ulong pool;

            if (api.CreateCommandPool(feature._device, &info, IntPtr.Zero, &pool) < 0)
            {
                return null;
            }

            feature._commandPool = pool;
        }

        return feature;
    }

    public ISkiaUpdatableTexture? TryCreateAlpha8(int width, int height, ReadOnlySpan<byte> pixels, int rowBytes)
    {
        using var _ = _vulkan.Device.Lock();

        if (_failed || _disposed || width <= 0 || height <= 0)
        {
            return null;
        }

        var imageInfo = new VkImageCreateInfo
        {
            sType = StructureImageCreateInfo,
            imageType = ImageType2D,
            format = FormatR8Unorm,
            width = (uint)width,
            height = (uint)height,
            depth = 1,
            mipLevels = 1,
            arrayLayers = 1,
            samples = 1,
            tiling = TilingOptimal,
            usage = ImageUsage,
            initialLayout = LayoutUndefined,
        };

        ulong image;

        if (_api.CreateImage(_device, &imageInfo, IntPtr.Zero, &image) < 0)
        {
            return null;
        }

        VkMemoryRequirements requirements;

        _api.GetImageMemoryRequirements(_device, image, &requirements);

        var memory = Allocate(requirements, MemoryDeviceLocal);

        if (memory == 0 || _api.BindImageMemory(_device, image, memory, 0) < 0)
        {
            Free(image, memory);
            return null;
        }

        var texture = new Texture(this, image, memory);

        if (!Upload(texture, new PixelRect(0, 0, width, height), pixels, rowBytes, LayoutUndefined))
        {
            Free(image, memory);
            return null;
        }

        _textures.Add(texture);

        var info = new GRVkImageInfo
        {
            Image = image,
            Alloc = new GRVkAlloc { Memory = memory, Size = requirements.size },
            ImageTiling = TilingOptimal,
            ImageLayout = LayoutShaderReadOnly,
            Format = FormatR8Unorm,
            ImageUsageFlags = ImageUsage,
            SampleCount = 1,
            LevelCount = 1,
            CurrentQueueFamily = _queueFamily,
        };

        SKImage? wrapped;

        using (var backend = new GRBackendTexture(width, height, info))
        {
            wrapped = SKImage.FromTexture(_context, backend, GRSurfaceOrigin.TopLeft, SKColorType.Alpha8,
                SKAlphaType.Premul, null, s_release, texture);
        }

        if (wrapped is null)
        {
            texture.Release();
            return null;
        }

        texture.Image = wrapped;

        return texture;
    }

    /// <summary>
    /// Waits for every copy, then frees the images Skia has not released yet and the upload
    /// resources. Images released later free nothing: a disposed context took them along.
    /// </summary>
    public void Dispose()
    {
        using var _ = _vulkan.Device.Lock();

        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var slot in _slots)
        {
            if (slot is null)
            {
                continue;
            }

            Wait(slot);
            _api.DestroyFence(_device, slot.Fence, IntPtr.Zero);
            FreeStaging(slot);
        }

        foreach (var texture in _textures)
        {
            Free(texture.Handle, texture.Memory);
        }

        _textures.Clear();

        if (_commandPool != 0)
        {
            _api.DestroyCommandPool(_device, _commandPool, IntPtr.Zero);
            _commandPool = 0;
        }
    }

    /// <summary>
    /// Copies <paramref name="rect"/> of <paramref name="source"/> into the image in one
    /// submission, moving it from <paramref name="oldLayout"/> through the transfer layout to
    /// the layout Skia samples it in.
    /// </summary>
    private bool Upload(Texture texture, PixelRect rect, ReadOnlySpan<byte> source, int rowBytes, uint oldLayout)
    {
        var bytes = (long)rect.Width * rect.Height;
        var slot = AcquireSlot(bytes);

        if (slot is null)
        {
            return false;
        }

        // Rows go into the staging buffer tightly packed; whole rows in one block.
        var start = rect.Y * rowBytes + rect.X;

        if (rect.Width == rowBytes)
        {
            source.Slice(start, (int)bytes).CopyTo(new Span<byte>((void*)slot.Mapped, (int)bytes));
        }
        else
        {
            for (var y = 0; y < rect.Height; y++)
            {
                source.Slice(start + y * rowBytes, rect.Width)
                    .CopyTo(new Span<byte>((byte*)slot.Mapped + (long)y * rect.Width, rect.Width));
            }
        }

        var commandBuffer = slot.CommandBuffer;
        var begin = new VkCommandBufferBeginInfo
        {
            sType = StructureCommandBufferBeginInfo,
            flags = CommandBufferOneTimeSubmit,
        };

        // The pool lets each buffer be reset on its own, which beginning it again does.
        if (_api.BeginCommandBuffer(commandBuffer, &begin) < 0)
        {
            _failed = true;
            return false;
        }

        // Earlier draws only read texels this copy leaves alone, but the layout change applies
        // to the whole image, so it waits for their fragment reads.
        var toTransfer = Barrier(texture.Handle, oldLayout, LayoutTransferDst, 0, AccessTransferWrite);

        _api.CmdPipelineBarrier(commandBuffer, oldLayout == LayoutUndefined ? StageTopOfPipe : StageFragmentShader,
            StageTransfer, 0, 0, null, 0, null, 1, &toTransfer);

        var region = new VkBufferImageCopy
        {
            imageAspectMask = AspectColor,
            imageLayerCount = 1,
            imageOffsetX = rect.X,
            imageOffsetY = rect.Y,
            imageExtentWidth = (uint)rect.Width,
            imageExtentHeight = (uint)rect.Height,
            imageExtentDepth = 1,
        };

        _api.CmdCopyBufferToImage(commandBuffer, slot.Buffer, texture.Handle, LayoutTransferDst, 1, &region);

        var toShader = Barrier(texture.Handle, LayoutTransferDst, LayoutShaderReadOnly, AccessTransferWrite,
            AccessShaderRead);

        _api.CmdPipelineBarrier(commandBuffer, StageTransfer, StageFragmentShader, 0, 0, null, 0, null, 1,
            &toShader);

        if (_api.EndCommandBuffer(commandBuffer) < 0)
        {
            _failed = true;
            return false;
        }

        var fence = slot.Fence;

        if (_api.ResetFences(_device, 1, &fence) < 0)
        {
            _failed = true;
            return false;
        }

        var submit = new VkSubmitInfo
        {
            sType = StructureSubmitInfo,
            commandBufferCount = 1,
            pCommandBuffers = &commandBuffer,
        };

        if (_api.QueueSubmit(_vulkan.Device.MainQueueHandle, 1, &submit, fence) < 0)
        {
            _failed = true;
            return false;
        }

        slot.Serial = ++_submitted;
        slot.Pending = true;
        texture.LastSerial = slot.Serial;

        return true;
    }

    /// <summary>The next slot of the ring, once its last copy finished, with room for <paramref name="bytes"/>.</summary>
    private Slot? AcquireSlot(long bytes)
    {
        var index = _nextSlot;

        _nextSlot = (index + 1) % SlotCount;

        var slot = _slots[index];

        if (slot is null)
        {
            slot = CreateSlot();

            if (slot is null)
            {
                return null;
            }

            _slots[index] = slot;
        }
        else if (!Wait(slot))
        {
            return null;
        }

        if (slot.Capacity < bytes || slot.Capacity > MaxRetainedStagingBytes && bytes <= MaxRetainedStagingBytes)
        {
            FreeStaging(slot);

            if (!CreateStaging(slot, Math.Max(MinStagingBytes, (long)BitOperations.RoundUpToPowerOf2((ulong)bytes))))
            {
                return null;
            }
        }

        return slot;
    }

    private Slot? CreateSlot()
    {
        var allocate = new VkCommandBufferAllocateInfo
        {
            sType = StructureCommandBufferAllocateInfo,
            commandPool = _commandPool,
            commandBufferCount = 1,
        };

        IntPtr commandBuffer;

        if (_api.AllocateCommandBuffers(_device, &allocate, &commandBuffer) < 0)
        {
            return null;
        }

        var fenceInfo = new VkFenceCreateInfo { sType = StructureFenceCreateInfo };
        ulong fence;

        if (_api.CreateFence(_device, &fenceInfo, IntPtr.Zero, &fence) < 0)
        {
            _api.FreeCommandBuffers(_device, _commandPool, 1, &commandBuffer);
            return null;
        }

        return new Slot { CommandBuffer = commandBuffer, Fence = fence };
    }

    private bool CreateStaging(Slot slot, long size)
    {
        var info = new VkBufferCreateInfo
        {
            sType = StructureBufferCreateInfo,
            size = (ulong)size,
            usage = UsageTransferSrc,
        };

        ulong buffer;

        if (_api.CreateBuffer(_device, &info, IntPtr.Zero, &buffer) < 0)
        {
            return false;
        }

        VkMemoryRequirements requirements;

        _api.GetBufferMemoryRequirements(_device, buffer, &requirements);

        var memory = Allocate(requirements, MemoryHostVisible | MemoryHostCoherent);
        void* mapped = null;

        if (memory == 0 || _api.BindBufferMemory(_device, buffer, memory, 0) < 0 ||
            _api.MapMemory(_device, memory, 0, ulong.MaxValue, 0, &mapped) < 0)
        {
            _api.DestroyBuffer(_device, buffer, IntPtr.Zero);

            if (memory != 0)
            {
                _api.FreeMemory(_device, memory, IntPtr.Zero);
            }

            return false;
        }

        slot.Buffer = buffer;
        slot.Memory = memory;
        slot.Mapped = (IntPtr)mapped;
        slot.Capacity = size;

        return true;
    }

    private void FreeStaging(Slot slot)
    {
        if (slot.Buffer != 0)
        {
            _api.DestroyBuffer(_device, slot.Buffer, IntPtr.Zero);
            _api.FreeMemory(_device, slot.Memory, IntPtr.Zero);
        }

        slot.Buffer = 0;
        slot.Memory = 0;
        slot.Mapped = IntPtr.Zero;
        slot.Capacity = 0;
    }

    /// <summary>Waits until the slot's last copy finished; <c>false</c> once the device is lost.</summary>
    /// <remarks>
    /// Only a submitted fence is waited for: one reset for a submission that failed would never
    /// signal.
    /// </remarks>
    private bool Wait(Slot slot)
    {
        if (!slot.Pending)
        {
            return true;
        }

        var fence = slot.Fence;

        if (_api.WaitForFences(_device, 1, &fence, 1, ulong.MaxValue) < 0)
        {
            _failed = true;
            return false;
        }

        slot.Pending = false;

        return true;
    }

    /// <summary>Waits for the copies up to <paramref name="serial"/> that may still run.</summary>
    private void WaitFor(ulong serial)
    {
        foreach (var slot in _slots)
        {
            if (slot is not null && slot.Pending && slot.Serial <= serial)
            {
                Wait(slot);
            }
        }
    }

    private ulong Allocate(VkMemoryRequirements requirements, uint required)
    {
        for (var i = 0; i < _memoryTypeFlags.Length; i++)
        {
            if ((requirements.memoryTypeBits & (1u << i)) == 0 || (_memoryTypeFlags[i] & required) != required)
            {
                continue;
            }

            var info = new VkMemoryAllocateInfo
            {
                sType = StructureMemoryAllocateInfo,
                allocationSize = requirements.size,
                memoryTypeIndex = (uint)i,
            };

            ulong memory;

            return _api.AllocateMemory(_device, &info, IntPtr.Zero, &memory) < 0 ? 0 : memory;
        }

        return 0;
    }

    private void Free(ulong image, ulong memory)
    {
        _api.DestroyImage(_device, image, IntPtr.Zero);

        if (memory != 0)
        {
            _api.FreeMemory(_device, memory, IntPtr.Zero);
        }
    }

    private static VkImageMemoryBarrier Barrier(ulong image, uint oldLayout, uint newLayout, uint srcAccess,
        uint dstAccess) => new()
    {
        sType = StructureImageMemoryBarrier,
        srcAccessMask = srcAccess,
        dstAccessMask = dstAccess,
        oldLayout = oldLayout,
        newLayout = newLayout,
        srcQueueFamilyIndex = QueueFamilyIgnored,
        dstQueueFamilyIndex = QueueFamilyIgnored,
        image = image,
        aspectMask = AspectColor,
        levelCount = 1,
        layerCount = 1,
    };

    /// <summary>One image of the context, wrapped for Skia.</summary>
    private sealed class Texture : ISkiaUpdatableTexture
    {
        private readonly VulkanUpdatableTextureFeature _owner;
        private bool _released;

        public Texture(VulkanUpdatableTextureFeature owner, ulong handle, ulong memory)
        {
            _owner = owner;
            Handle = handle;
            Memory = memory;
        }

        public ulong Handle { get; }

        public ulong Memory { get; }

        /// <summary>The submission of the last copy into the image.</summary>
        public ulong LastSerial { get; set; }

        public SKImage Image { get; set; } = null!;

        public void Update(PixelRect rect, ReadOnlySpan<byte> source, int rowBytes)
        {
            using var _ = _owner._vulkan.Device.Lock();

            if (!_released && !_owner._failed && !_owner._disposed)
            {
                _owner.Upload(this, rect, source, rowBytes, LayoutShaderReadOnly);
            }
        }

        /// <summary>Drops the wrapper; the image is destroyed once Skia no longer uses it.</summary>
        public void Dispose() => Image.Dispose();

        /// <summary>
        /// Destroys the image once the last copy into it finished; Skia calls this after its
        /// own command buffers using the image have finished.
        /// </summary>
        public void Release()
        {
            using var _ = _owner._vulkan.Device.Lock();

            if (_released || _owner._disposed)
            {
                return;
            }

            _released = true;
            _owner.WaitFor(LastSerial);
            _owner._textures.Remove(this);
            _owner.Free(Handle, Memory);
        }
    }

    private sealed class Slot
    {
        public IntPtr CommandBuffer;
        public ulong Fence;
        public ulong Buffer;
        public ulong Memory;
        public IntPtr Mapped;
        public long Capacity;
        public ulong Serial;
        public bool Pending;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkImageCreateInfo
    {
        public uint sType;
        public void* pNext;
        public uint flags;
        public uint imageType;
        public uint format;
        public uint width;
        public uint height;
        public uint depth;
        public uint mipLevels;
        public uint arrayLayers;
        public uint samples;
        public uint tiling;
        public uint usage;
        public uint sharingMode;
        public uint queueFamilyIndexCount;
        public uint* pQueueFamilyIndices;
        public uint initialLayout;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkMemoryRequirements
    {
        public ulong size;
        public ulong alignment;
        public uint memoryTypeBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkMemoryAllocateInfo
    {
        public uint sType;
        public void* pNext;
        public ulong allocationSize;
        public uint memoryTypeIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkBufferCreateInfo
    {
        public uint sType;
        public void* pNext;
        public uint flags;
        public ulong size;
        public uint usage;
        public uint sharingMode;
        public uint queueFamilyIndexCount;
        public uint* pQueueFamilyIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkCommandPoolCreateInfo
    {
        public uint sType;
        public void* pNext;
        public uint flags;
        public uint queueFamilyIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkCommandBufferAllocateInfo
    {
        public uint sType;
        public void* pNext;
        public ulong commandPool;
        public uint level;
        public uint commandBufferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkCommandBufferBeginInfo
    {
        public uint sType;
        public void* pNext;
        public uint flags;
        public void* pInheritanceInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkFenceCreateInfo
    {
        public uint sType;
        public void* pNext;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkImageMemoryBarrier
    {
        public uint sType;
        public void* pNext;
        public uint srcAccessMask;
        public uint dstAccessMask;
        public uint oldLayout;
        public uint newLayout;
        public uint srcQueueFamilyIndex;
        public uint dstQueueFamilyIndex;
        public ulong image;
        public uint aspectMask;
        public uint baseMipLevel;
        public uint levelCount;
        public uint baseArrayLayer;
        public uint layerCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkBufferImageCopy
    {
        public ulong bufferOffset;
        public uint bufferRowLength;
        public uint bufferImageHeight;
        public uint imageAspectMask;
        public uint imageMipLevel;
        public uint imageBaseArrayLayer;
        public uint imageLayerCount;
        public int imageOffsetX;
        public int imageOffsetY;
        public int imageOffsetZ;
        public uint imageExtentWidth;
        public uint imageExtentHeight;
        public uint imageExtentDepth;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkSubmitInfo
    {
        public uint sType;
        public void* pNext;
        public uint waitSemaphoreCount;
        public ulong* pWaitSemaphores;
        public uint* pWaitDstStageMask;
        public uint commandBufferCount;
        public IntPtr* pCommandBuffers;
        public uint signalSemaphoreCount;
        public ulong* pSignalSemaphores;
    }

    /// <summary>The Vulkan entry points the feature calls, resolved once per device.</summary>
    private sealed class Api
    {
        public delegate* unmanaged[Stdcall]<IntPtr, byte*, void> GetPhysicalDeviceMemoryProperties;
        public delegate* unmanaged[Stdcall]<IntPtr, VkImageCreateInfo*, IntPtr, ulong*, int> CreateImage;
        public delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, void> DestroyImage;
        public delegate* unmanaged[Stdcall]<IntPtr, ulong, VkMemoryRequirements*, void> GetImageMemoryRequirements;
        public delegate* unmanaged[Stdcall]<IntPtr, ulong, ulong, ulong, int> BindImageMemory;
        public delegate* unmanaged[Stdcall]<IntPtr, VkMemoryAllocateInfo*, IntPtr, ulong*, int> AllocateMemory;
        public delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, void> FreeMemory;
        public delegate* unmanaged[Stdcall]<IntPtr, ulong, ulong, ulong, uint, void**, int> MapMemory;
        public delegate* unmanaged[Stdcall]<IntPtr, VkBufferCreateInfo*, IntPtr, ulong*, int> CreateBuffer;
        public delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, void> DestroyBuffer;
        public delegate* unmanaged[Stdcall]<IntPtr, ulong, VkMemoryRequirements*, void> GetBufferMemoryRequirements;
        public delegate* unmanaged[Stdcall]<IntPtr, ulong, ulong, ulong, int> BindBufferMemory;
        public delegate* unmanaged[Stdcall]<IntPtr, VkCommandPoolCreateInfo*, IntPtr, ulong*, int> CreateCommandPool;
        public delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, void> DestroyCommandPool;
        public delegate* unmanaged[Stdcall]<IntPtr, VkCommandBufferAllocateInfo*, IntPtr*, int> AllocateCommandBuffers;
        public delegate* unmanaged[Stdcall]<IntPtr, ulong, uint, IntPtr*, void> FreeCommandBuffers;
        public delegate* unmanaged[Stdcall]<IntPtr, VkCommandBufferBeginInfo*, int> BeginCommandBuffer;
        public delegate* unmanaged[Stdcall]<IntPtr, int> EndCommandBuffer;
        public delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, void*, uint, void*, uint,
            VkImageMemoryBarrier*, void> CmdPipelineBarrier;
        public delegate* unmanaged[Stdcall]<IntPtr, ulong, ulong, uint, uint, VkBufferImageCopy*, void>
            CmdCopyBufferToImage;
        public delegate* unmanaged[Stdcall]<IntPtr, uint, VkSubmitInfo*, ulong, int> QueueSubmit;
        public delegate* unmanaged[Stdcall]<IntPtr, VkFenceCreateInfo*, IntPtr, ulong*, int> CreateFence;
        public delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, void> DestroyFence;
        public delegate* unmanaged[Stdcall]<IntPtr, uint, ulong*, int> ResetFences;
        public delegate* unmanaged[Stdcall]<IntPtr, uint, ulong*, uint, ulong, int> WaitForFences;

        public static bool TryResolve(IVulkanPlatformGraphicsContext vulkan, out Api api)
        {
            var device = vulkan.Device;
            var instance = vulkan.Instance;
            var missing = false;

            IntPtr Device(string name)
            {
                var address = instance.GetDeviceProcAddress(device.Handle, name);

                missing |= address == IntPtr.Zero;

                return address;
            }

            var memoryProperties = instance.GetInstanceProcAddress(device.Instance.Handle,
                "vkGetPhysicalDeviceMemoryProperties");

            missing |= memoryProperties == IntPtr.Zero;

            api = new Api
            {
                GetPhysicalDeviceMemoryProperties =
                    (delegate* unmanaged[Stdcall]<IntPtr, byte*, void>)memoryProperties,
                CreateImage = (delegate* unmanaged[Stdcall]<IntPtr, VkImageCreateInfo*, IntPtr, ulong*, int>)
                    Device("vkCreateImage"),
                DestroyImage = (delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, void>)Device("vkDestroyImage"),
                GetImageMemoryRequirements = (delegate* unmanaged[Stdcall]<IntPtr, ulong, VkMemoryRequirements*, void>)
                    Device("vkGetImageMemoryRequirements"),
                BindImageMemory = (delegate* unmanaged[Stdcall]<IntPtr, ulong, ulong, ulong, int>)
                    Device("vkBindImageMemory"),
                AllocateMemory = (delegate* unmanaged[Stdcall]<IntPtr, VkMemoryAllocateInfo*, IntPtr, ulong*, int>)
                    Device("vkAllocateMemory"),
                FreeMemory = (delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, void>)Device("vkFreeMemory"),
                MapMemory = (delegate* unmanaged[Stdcall]<IntPtr, ulong, ulong, ulong, uint, void**, int>)
                    Device("vkMapMemory"),
                CreateBuffer = (delegate* unmanaged[Stdcall]<IntPtr, VkBufferCreateInfo*, IntPtr, ulong*, int>)
                    Device("vkCreateBuffer"),
                DestroyBuffer = (delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, void>)Device("vkDestroyBuffer"),
                GetBufferMemoryRequirements = (delegate* unmanaged[Stdcall]<IntPtr, ulong, VkMemoryRequirements*, void>)
                    Device("vkGetBufferMemoryRequirements"),
                BindBufferMemory = (delegate* unmanaged[Stdcall]<IntPtr, ulong, ulong, ulong, int>)
                    Device("vkBindBufferMemory"),
                CreateCommandPool =
                    (delegate* unmanaged[Stdcall]<IntPtr, VkCommandPoolCreateInfo*, IntPtr, ulong*, int>)
                    Device("vkCreateCommandPool"),
                DestroyCommandPool = (delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, void>)
                    Device("vkDestroyCommandPool"),
                AllocateCommandBuffers =
                    (delegate* unmanaged[Stdcall]<IntPtr, VkCommandBufferAllocateInfo*, IntPtr*, int>)
                    Device("vkAllocateCommandBuffers"),
                FreeCommandBuffers = (delegate* unmanaged[Stdcall]<IntPtr, ulong, uint, IntPtr*, void>)
                    Device("vkFreeCommandBuffers"),
                BeginCommandBuffer = (delegate* unmanaged[Stdcall]<IntPtr, VkCommandBufferBeginInfo*, int>)
                    Device("vkBeginCommandBuffer"),
                EndCommandBuffer = (delegate* unmanaged[Stdcall]<IntPtr, int>)Device("vkEndCommandBuffer"),
                CmdPipelineBarrier = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, void*, uint, void*,
                    uint, VkImageMemoryBarrier*, void>)Device("vkCmdPipelineBarrier"),
                CmdCopyBufferToImage =
                    (delegate* unmanaged[Stdcall]<IntPtr, ulong, ulong, uint, uint, VkBufferImageCopy*, void>)
                    Device("vkCmdCopyBufferToImage"),
                QueueSubmit = (delegate* unmanaged[Stdcall]<IntPtr, uint, VkSubmitInfo*, ulong, int>)
                    Device("vkQueueSubmit"),
                CreateFence = (delegate* unmanaged[Stdcall]<IntPtr, VkFenceCreateInfo*, IntPtr, ulong*, int>)
                    Device("vkCreateFence"),
                DestroyFence = (delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, void>)Device("vkDestroyFence"),
                ResetFences = (delegate* unmanaged[Stdcall]<IntPtr, uint, ulong*, int>)Device("vkResetFences"),
                WaitForFences = (delegate* unmanaged[Stdcall]<IntPtr, uint, ulong*, uint, ulong, int>)
                    Device("vkWaitForFences"),
            };

            return !missing;
        }
    }
}
