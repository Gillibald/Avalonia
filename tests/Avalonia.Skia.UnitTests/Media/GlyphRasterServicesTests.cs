using System.Threading;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// What managed text keeps across frames belongs to the application's font manager: one state
    /// per thread, a new one per application, and drawing contexts take the state and tuning of the
    /// application they are created in.
    /// </summary>
    public class GlyphRasterServicesTests
    {
        [Fact]
        public void Each_Thread_Of_An_Application_Has_One_State()
        {
            using var app = TextTestApplication.Enter();
            var services = GlyphRasterServices.Current;
            var here = services.ForCurrentThread();
            TextRasterThreadState? other = null;
            var thread = new Thread(() => other = services.ForCurrentThread());

            thread.Start();
            thread.Join();

            Assert.Same(here, services.ForCurrentThread());
            Assert.NotSame(here, other);
            Assert.Same(services, here.Services);
        }

        [Fact]
        public void A_New_Application_Has_New_Services()
        {
            GlyphRasterServices first;

            using (TextTestApplication.Enter())
            {
                first = GlyphRasterServices.Current;
            }

            Assert.True(first.IsDisposed);

            using (TextTestApplication.Enter())
            {
                Assert.NotSame(first, GlyphRasterServices.Current);
                Assert.False(GlyphRasterServices.Current.IsDisposed);
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

            Assert.Same(GlyphRasterServices.Current.ForCurrentThread(), target.RasterState);
            Assert.Same(tuning, target.RasterTuning);
        }
    }
}
