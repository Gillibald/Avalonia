using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Rendering.SceneGraph;
using Xunit;

namespace Avalonia.Skia.RenderTests
{
    /// <summary>
    /// The Mesa outputs of this suite run a GPU API on the CPU (llvmpipe for GL, lavapipe for
    /// Vulkan), and their drawing contexts must say so; the CPU outputs report raster.
    /// </summary>
    public class GlyphRasterTargetRenderTests : TestBase
    {
        public GlyphRasterTargetRenderTests()
            : base(@"Media\GlyphRun")
        {
        }

        [Fact]
        public async Task Every_Output_Reports_What_Rasterizes_It()
        {
            var seen = new List<GlyphRasterTarget>();
            var target = new Border
            {
                Width = 8,
                Height = 8,
                Background = Brushes.White,
                Child = new ProbeControl(seen),
            };

            await RenderToFile(target);

            var mesaOutputs = (MesaSoftwareRenderer.GlEnabled ? 1 : 0) + (MesaSoftwareRenderer.VulkanEnabled ? 1 : 0);

            Assert.Equal(mesaOutputs, seen.FindAll(t => t == GlyphRasterTarget.SoftwareGpu).Count);
            Assert.DoesNotContain(GlyphRasterTarget.HardwareGpu, seen);
            Assert.Contains(GlyphRasterTarget.Raster, seen);
        }

        private sealed class ProbeControl : Control
        {
            private readonly List<GlyphRasterTarget> _seen;

            public ProbeControl(List<GlyphRasterTarget> seen) => _seen = seen;

            public override void Render(DrawingContext context)
                => context.Custom(new ProbeOperation(new Rect(Bounds.Size), _seen));
        }

        private sealed class ProbeOperation : ICustomDrawOperation
        {
            private readonly List<GlyphRasterTarget> _seen;

            public ProbeOperation(Rect bounds, List<GlyphRasterTarget> seen)
            {
                Bounds = bounds;
                _seen = seen;
            }

            public Rect Bounds { get; }

            public bool HitTest(Point p) => false;

            public bool Equals(ICustomDrawOperation? other) => false;

            public void Dispose()
            {
            }

            public void Render(ImmediateDrawingContext context)
            {
                if (context.PlatformImpl is DrawingContextImpl skia)
                {
                    lock (_seen)
                    {
                        _seen.Add(skia.GlyphRasterTarget);
                    }
                }
            }
        }
    }
}
