using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// A drawing context that is not drawn inside another on its thread is a frame of the glyph
    /// cache clock: a render target's frame, a bitmap rendered on its own. Layers and offscreen
    /// surfaces drawn within it belong to its frame.
    /// </summary>
    public class GlyphCacheFrameTests
    {
        [Fact]
        public void A_Top_Level_Drawing_Context_Begins_A_Frame_And_Its_Layers_Do_Not()
        {
            // On a thread of its own: a pooled test thread may still hold a context another test
            // left open across an await.
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    DrawWithLayer();
                }
                catch (Exception e)
                {
                    failure = e;
                }
            });

            thread.Start();
            thread.Join();

            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        private static void DrawWithLayer()
        {
            var budget = GlyphCacheBudget.Shared;
            var info = new SKImageInfo(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(info);

            Assert.Equal(0, budget.CurrentThreadFrame);

            using (var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
                   {
                       Surface = surface,
                       Dpi = new Vector(96, 96),
                   }))
            {
                var frame = budget.CurrentThreadFrame;

                Assert.NotEqual(0, frame);

                using (var layer = context.CreateLayer(new PixelSize(16, 16)))
                using (layer.CreateDrawingContext())
                {
                    Assert.Equal(frame, budget.CurrentThreadFrame);
                }

                Assert.Equal(frame, budget.CurrentThreadFrame);
            }

            Assert.Equal(0, budget.CurrentThreadFrame);
        }
    }
}
