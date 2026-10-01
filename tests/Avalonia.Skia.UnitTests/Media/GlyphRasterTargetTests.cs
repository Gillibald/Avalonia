using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Skia.Helpers;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// A drawing context reports whether a CPU raster pipeline, a software implementation of
    /// a GPU API or a hardware GPU produces its pixels.
    /// </summary>
    public class GlyphRasterTargetTests
    {
        [Theory]
        [InlineData("llvmpipe (LLVM 22.1.8, 256 bits)")]
        [InlineData("softpipe")]
        [InlineData("SwiftShader Device (Subzero)")]
        [InlineData("ANGLE (Google, Vulkan 1.3.0 (SwiftShader Device (Subzero) (0x0000C0DE)), SwiftShader driver)")]
        [InlineData("ANGLE (Microsoft, Microsoft Basic Render Driver Direct3D11 vs_5_0 ps_5_0, D3D11)")]
        [InlineData("GDI Generic")]
        public void Software_Gl_Renderers_Are_Recognized(string renderer)
        {
            Assert.True(SkiaGpuRasterizer.IsSoftwareGlRenderer(renderer));
        }

        [Theory]
        [InlineData("NVIDIA GeForce RTX 4060/PCIe/SSE2")]
        [InlineData("ANGLE (NVIDIA, NVIDIA GeForce RTX 4060 (0x00002882) Direct3D11 vs_5_0 ps_5_0, D3D11-32.0.16.1047)")]
        [InlineData("AMD Radeon(TM) Graphics")]
        [InlineData("Mesa Intel(R) UHD Graphics 620 (KBL GT2)")]
        [InlineData("Apple M1")]
        [InlineData(null)]
        public void Hardware_Gl_Renderers_Are_Not_Software(string? renderer)
        {
            Assert.False(SkiaGpuRasterizer.IsSoftwareGlRenderer(renderer));
        }

        [Theory]
        [InlineData(SkiaGpuRasterizer.VulkanCpuDeviceType, "llvmpipe (LLVM 22.1.8, 256 bits)", true)]
        [InlineData(SkiaGpuRasterizer.VulkanCpuDeviceType, "Unnamed CPU device", true)]
        [InlineData(1, "SwiftShader Device (Subzero)", true)]
        [InlineData(2, "NVIDIA GeForce RTX 4060", false)]
        [InlineData(1, "Intel(R) UHD Graphics 620", false)]
        public void Vulkan_Devices_Are_Classified_By_Type_And_Name(int deviceType, string name, bool software)
        {
            Assert.Equal(software, SkiaGpuRasterizer.IsSoftwareVulkanDevice(deviceType, name));
        }

        [Fact]
        public void A_Raster_Context_Reports_Raster()
        {
            var info = new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(info);
            using var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
            {
                Surface = surface,
                Dpi = new Vector(96, 96),
            });

            Assert.Equal(GlyphRasterTarget.Raster, context.GlyphRasterTarget);

            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var wrapped = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));

            Assert.Equal(GlyphRasterTarget.Raster, wrapped.GlyphRasterTarget);
        }

        [Theory]
        [InlineData(GpuBackend.NativeGl)]
        [InlineData(GpuBackend.Angle)]
        public void A_Gpu_Context_Reports_What_Its_Grcontext_Was_Classified_As(GpuBackend backend)
        {
            using var gpu = GpuTestContext.TryCreate(backend, out var reason);

            Assert.SkipWhen(gpu is null, $"No usable {backend} context: {reason}");

            // Nobody classified this context, so it counts as hardware.
            Assert.Equal(GlyphRasterTarget.HardwareGpu, CreateTarget(gpu!));

            try
            {
                SkiaGpuRasterizer.Register(gpu!.GrContext, isSoftware: true);
                Assert.Equal(GlyphRasterTarget.SoftwareGpu, CreateTarget(gpu));

                SkiaGpuRasterizer.Register(gpu.GrContext, isSoftware: false);
                Assert.Equal(GlyphRasterTarget.HardwareGpu, CreateTarget(gpu));
            }
            finally
            {
                SkiaGpuRasterizer.Register(gpu!.GrContext, isSoftware: false);
            }
        }

        private static GlyphRasterTarget CreateTarget(GpuTestContext gpu)
        {
            var info = new SKImageInfo(16, 16, SKColorType.Rgba8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(gpu.GrContext, true, info);

            Assert.SkipWhen(surface is null, "GPU surface creation failed.");

            using var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
            {
                Surface = surface,
                GrContext = gpu.GrContext,
                Dpi = new Vector(96, 96),
            });

            return context.GlyphRasterTarget;
        }
    }
}
