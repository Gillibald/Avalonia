using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Grid fitting snaps a glyph's stems and heights to the pixel grid differently at each size,
    /// so a zoom drawn hinted changes the glyphs' shapes from frame to frame. A run whose transform
    /// keeps changing therefore rasterizes its glyphs unhinted, as
    /// <see cref="TextHintingMode.None"/> draws them, and never draws a grid-fitted raster cached
    /// for a static frame at its scale; its first frame under a held transform draws what a static
    /// frame draws, grid-fitted.
    /// </summary>
    [Collection(MotionBudgetCollection.Name)]
    public class MotionHintingTests
    {
        private const string Text = "Hamburgefonstiv 0123456789";
        private const int Width = 520;
        private const int Height = 160;
        private const double EmSize = 18;
        private const int Period = 10;

        private static readonly Point s_origin = new(8.37, 32.61);

        public enum Target
        {
            Raster,
            SoftwareGl,
            HardwareGl,
            HardwareAngle,
        }

        public static IEnumerable<object[]> Targets()
        {
            foreach (var target in Enum.GetValues<Target>())
            {
                yield return new object[] { target };
            }
        }

        public static IEnumerable<object[]> TargetsAndBudgets()
        {
            foreach (var target in Enum.GetValues<Target>())
            {
                yield return new object[] { target, true };
                yield return new object[] { target, false };
            }
        }

        /// <summary>A sawtooth zoom by 1 % a frame that returns to the first frame's scale every <see cref="Period"/> frames.</summary>
        private static Matrix Zoom(int frame)
        {
            var scale = 1 + 0.01 * (frame % Period);

            return Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(10.25, 12.5);
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void A_Zoom_Draws_Every_Moving_Frame_Unhinted_Even_Over_Scales_With_Grid_Fitted_Rasters_Cached(
            Target target)
        {
            // The frames before the motion is recognized draw as static frames and cache their
            // grid-fitted rasters; the sawtooth passes those scales again twice while moving, and
            // each moving frame draws exactly what a static unhinted draw at its transform draws,
            // never what a static grid-fitted one draws. The budget affords every frame, so each
            // moving frame rasterizes or draws a raster cached for moving text.
            using var output = new Frames(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var referenceScope = WideRunMaskTests.CreateEnvironment(out var referenceTypeface);
            using var budget = new AffordingBudget();
            using var animated = WideRunMaskTests.CreateRun(typeface, Text, EmSize, s_origin);

            for (var frame = 0; frame < 3 * Period; frame++)
            {
                var drawn = output.Frame(context =>
                {
                    context.Transform = Zoom(frame);
                    context.DrawGlyphRun(Brushes.Black, animated);
                });

                var hinted = Reference(output, referenceTypeface, Zoom(frame), TextHintingMode.Unspecified);
                var unhinted = Reference(output, referenceTypeface, Zoom(frame), TextHintingMode.None);

                Assert.True(Array.Exists(hinted, b => b != 0));
                Assert.False(hinted.AsSpan().SequenceEqual(unhinted), $"frame {frame}: grid fitting changes nothing");

                if (frame < TransformChurnGuard.Threshold)
                {
                    TransformedAtlasTests.AssertEqual(hinted, drawn, $"{target} static frame {frame}");
                }
                else
                {
                    Assert.False(hinted.AsSpan().SequenceEqual(drawn), $"{target} frame {frame} drew grid-fitted glyphs");
                    TransformedAtlasTests.AssertEqual(unhinted, drawn, $"{target} frame {frame}");
                }
            }

            // No moving frame left a grid-fitted glyph mask behind at a scale only moving frames drew.
            for (var frame = TransformChurnGuard.Threshold; frame < Period; frame++)
            {
                Assert.False(HasGridFittedMask(typeface, animated, Zoom(frame).M11 * EmSize),
                    $"{target}: a grid-fitted mask at the scale of frame {frame}");
            }
        }

        [Theory]
        [MemberData(nameof(TargetsAndBudgets))]
        public void The_First_Held_Frame_After_A_Zoom_Draws_The_Static_Grid_Fitted_Run(Target target, bool affordable)
        {
            // Sharp unhinted frames while the budget affords them, stretched frames otherwise:
            // either way the frame that repeats the last transform draws what a run drawn once at
            // that transform draws, with its glyphs grid-fitted, and so does the frame after it.
            using var output = new Frames(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var referenceScope = WideRunMaskTests.CreateEnvironment(out var referenceTypeface);
            using var budget = affordable ? new AffordingBudget() : null;
            using var animated = WideRunMaskTests.CreateRun(typeface, Text, EmSize, s_origin);

            const int moving = 8;

            for (var frame = 0; frame < moving; frame++)
            {
                output.Frame(context =>
                {
                    context.Transform = Zoom(frame);
                    context.DrawGlyphRun(Brushes.Black, animated);
                });
            }

            var expected = Reference(output, referenceTypeface, Zoom(moving - 1), TextHintingMode.Unspecified);

            for (var held = 0; held < 2; held++)
            {
                var drawn = output.Frame(context =>
                {
                    context.Transform = Zoom(moving - 1);
                    context.DrawGlyphRun(Brushes.Black, animated);
                });

                TransformedAtlasTests.AssertEqual(expected, drawn, $"{target} held frame {held}");
            }
        }

        /// <summary>
        /// Whether <paramref name="typeface"/>'s mask cache holds a grid-fitted mask of a glyph of
        /// <paramref name="run"/> at <paramref name="pixelsPerEm"/>, at any pen phase.
        /// </summary>
        private static bool HasGridFittedMask(GlyphTypeface typeface, ManagedGlyphRunImpl run, double pixelsPerEm)
        {
            var scale = GlyphMaskKey.QuantizeScale((float)pixelsPerEm);

            foreach (var glyph in run.GlyphIndices)
            {
                for (byte phase = 0; phase < GlyphMaskKey.PhaseCount; phase++)
                {
                    foreach (var strong in new[] { false, true })
                    {
                        foreach (var mode in new[] { GlyphMaskMode.Antialiased, GlyphMaskMode.Subpixel })
                        {
                            if (typeface.MaskCache.TryGet(new GlyphMaskKey(glyph, scale, phase, mode, true, strong), out _))
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>A run of <paramref name="typeface"/> drawn once under <paramref name="transform"/>.</summary>
        private static byte[] Reference(Frames output, GlyphTypeface typeface, Matrix transform, TextHintingMode hinting)
        {
            using var run = WideRunMaskTests.CreateRun(typeface, Text, EmSize, s_origin);

            return output.Frame(context =>
            {
                context.Transform = transform;
                context.TextOptions = context.TextOptions with { TextHintingMode = hinting };
                context.DrawGlyphRun(Brushes.Black, run);
            });
        }

        /// <summary>
        /// A motion budget that affords every frame, and a CPU zoom that prefers rasterizing to
        /// stretching, for the scope.
        /// </summary>
        private sealed class AffordingBudget : IDisposable
        {
            private readonly double? _budget = MotionRasterBudget.BudgetOverride;
            private readonly double? _glyphCost = MotionRasterBudget.GlyphCostOverride;
            private readonly double? _nonText = MotionRasterBudget.NonTextOverride;
            private readonly double _zoomRasterPixelCost = MaskGlyphRunRenderer.ZoomRasterPixelCost;

            public AffordingBudget()
            {
                MotionRasterBudget.BudgetOverride = 1e9;
                MotionRasterBudget.GlyphCostOverride = 1;
                MotionRasterBudget.NonTextOverride = 0;
                MaskGlyphRunRenderer.ZoomRasterPixelCost = 0;
            }

            public void Dispose()
            {
                MotionRasterBudget.BudgetOverride = _budget;
                MotionRasterBudget.GlyphCostOverride = _glyphCost;
                MotionRasterBudget.NonTextOverride = _nonText;
                MaskGlyphRunRenderer.ZoomRasterPixelCost = _zoomRasterPixelCost;
            }
        }

        /// <summary>
        /// A CPU or GPU surface drawn by a new context per frame, as a window's render passes draw
        /// it, so every frame has a motion budget of its own.
        /// </summary>
        private sealed class Frames : IDisposable
        {
            private readonly GpuTestContext? _gpu;
            private readonly SKSurface _surface;

            public Frames(Target target)
            {
                if (target == Target.Raster)
                {
                    _surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
                    return;
                }

                _gpu = TransformedAtlasTests.CreateGpu(target == Target.HardwareAngle ? GpuBackend.Angle : GpuBackend.NativeGl,
                    software: target == Target.SoftwareGl);

                var surface = SKSurface.Create(_gpu.GrContext, true,
                    new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));

                if (surface is null)
                {
                    _gpu.Dispose();
                    Assert.Skip("GPU surface creation failed.");
                }

                _surface = surface;
            }

            public unsafe byte[] Frame(Action<DrawingContextImpl> draw)
            {
                _surface.Canvas.Clear(SKColors.Transparent);

                using (var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
                       {
                           Surface = _surface,
                           GrContext = _gpu?.GrContext,
                           Dpi = new Vector(96, 96),
                       }))
                {
                    draw(context);
                }

                _gpu?.GrContext.Flush();

                var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
                var pixels = new byte[info.BytesSize];

                fixed (byte* p = pixels)
                {
                    Assert.True(_surface.ReadPixels(info, (IntPtr)p, info.RowBytes, 0, 0));
                }

                return pixels;
            }

            public void Dispose()
            {
                _surface.Dispose();
                _gpu?.GrContext.Flush();
                _gpu?.Dispose();
            }
        }
    }
}
