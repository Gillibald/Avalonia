using System.Threading;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// What managed text keeps across frames belongs to the application.s glyph rasterization context: one state
    /// per thread, a new one per application, and drawing contexts take the state and tuning of the
    /// application they are created in.
    /// </summary>
    public class GlyphRasterizationContextTests
    {
        [Fact]
        public void Each_Thread_Of_An_Application_Has_One_State()
        {
            using var app = TextTestApplication.Enter();
            var rasterization = GlyphRasterizationContext.Required;
            var here = rasterization.ForCurrentThread();
            TextRasterThreadState? other = null;
            var thread = new Thread(() => other = rasterization.ForCurrentThread());

            thread.Start();
            thread.Join();

            Assert.Same(here, rasterization.ForCurrentThread());
            Assert.NotSame(here, other);
            Assert.Same(rasterization, here.Context);
        }

        [Fact]
        public void A_New_Application_Has_A_New_Context()
        {
            GlyphRasterizationContext first;

            using (TextTestApplication.Enter())
            {
                first = GlyphRasterizationContext.Required;
            }

            Assert.True(first.IsDisposed);

            using (TextTestApplication.Enter())
            {
                Assert.NotSame(first, GlyphRasterizationContext.Required);
                Assert.False(GlyphRasterizationContext.Required.IsDisposed);
            }
        }

        [Fact]
        public void A_Drawing_Context_Takes_The_State_And_Tuning_Of_Its_Application()
        {
            using var app = TextTestApplication.Enter();
            var tuning = TestRasterTuning.Set(t => t with { });

            using var surface = SKSurface.Create(new SKImageInfo(8, 8));
            using var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo { Surface = surface });
            var target = (IGlyphTargetContext)context;

            Assert.Same(GlyphRasterizationContext.Required.ForCurrentThread(), target.RasterState);
            Assert.Same(tuning, target.RasterTuning);
        }
    }
}
