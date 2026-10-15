using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// A GPU draws a frame of a run whose transform keeps changing sharp, as a static frame at
    /// that transform, while the frame's rasterization budget affords it, and stretches an
    /// earlier raster otherwise. The runs of a frame share one budget, and a frame draws sharp
    /// only when the text that moved, or was about to move, in the frame before it was estimated
    /// at less than twice the budget. A frame that runs out of budget stretches every run for a
    /// while, or until the motion stops, rather than switching between sharp and stretched
    /// frames, and the frame after that while tries again; a try that runs out of budget again
    /// doubles the next hold. The budget is what the frame interval leaves after the time the
    /// frame spends outside text and a safety margin, up to a cap. Costs, budgets, intervals and
    /// the time outside text are injected, so every decision here is independent of the machine.
    /// </summary>
    [Collection(MotionBudgetCollection.Name)]
    public class MotionRasterBudgetTests
    {
        private const string Text = "Hamburgefonstiv 0123456789";
        private const int Width = 520;
        private const int Height = 420;

        /// <summary>The injected cost of one glyph; a run of <see cref="Text"/> is estimated at this times 26.</summary>
        private const double GlyphCost = 1;

        private static readonly double s_runCost = Text.Length * GlyphCost;

        public enum Target
        {
            SoftwareGl,
            HardwareGl,
            HardwareAngle,
            SoftwareMetal,
            HardwareMetal,
        }

        public enum Motion
        {
            Rotation,
            Zoom,
            ZoomRotation,
        }

        public static IEnumerable<object[]> Motions()
        {
            foreach (var target in Enum.GetValues<Target>())
            {
                foreach (var motion in Enum.GetValues<Motion>())
                {
                    yield return new object[] { target, motion };
                }
            }
        }

        public static IEnumerable<object[]> Targets()
        {
            foreach (var target in Enum.GetValues<Target>())
            {
                yield return new object[] { target };
            }
        }

        private static Matrix At(Motion motion, int frame, double y = 0) => motion switch
        {
            Motion.Rotation => Matrix.CreateRotation(Math.PI * (5 + frame * 1.7) / 180) *
                               Matrix.CreateTranslation(160.4, 120.7 + y),
            Motion.Zoom => Matrix.CreateScale(1 + 0.03 * frame, 1 + 0.03 * frame) *
                           Matrix.CreateTranslation(10.25, 12.5 + y),
            _ => Matrix.CreateScale(1 + 0.03 * frame, 1 + 0.03 * frame) * Matrix.CreateRotation(Math.PI * 15 / 180) *
                 Matrix.CreateTranslation(60.4, 40.7 + y),
        };

        [Theory]
        [MemberData(nameof(Motions))]
        public void Motion_The_Budget_Affords_Draws_Every_Moving_Frame_Like_A_Static_Unhinted_Draw(Target target, Motion motion)
        {
            using var output = new FrameTarget(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var budget = new InjectedBudget(budget: 1e9, glyphCost: GlyphCost);
            using var animated = WideRunMaskTests.CreateRun(typeface, Text, 18, new Point(8.37, 32.61));

            for (var frame = 0; frame < 16; frame++)
            {
                var unsettled = TextTestApplication.ThreadState.StretchedDraws;
                var drawn = output.Frame(context =>
                {
                    context.Transform = At(motion, frame);
                    context.DrawGlyphRun(Brushes.Black, animated);
                });

                // A sharp frame is what a static frame at the transform draws without hinting, once
                // the motion is recognized: a moving run's glyphs keep their shapes from one scale
                // to the next. The frames before draw as static frames.
                Assert.True(unsettled == TextTestApplication.ThreadState.StretchedDraws, $"{target} {motion} frame {frame} stretched");

                using var fresh = WideRunMaskTests.CreateRun(typeface, Text, 18, new Point(8.37, 32.61));

                var expected = output.Frame(context =>
                {
                    context.Transform = At(motion, frame);
                    if (frame >= TransformChurnGuard.Threshold)
                    {
                        context.TextOptions = context.TextOptions with { TextHintingMode = TextHintingMode.None };
                    }

                    context.DrawGlyphRun(Brushes.Black, fresh);
                });

                Assert.True(Array.Exists(expected, b => b != 0));
                TransformedAtlasTests.AssertEqual(expected, drawn, $"{target} {motion} frame {frame}");
            }
        }

        [Theory]
        [MemberData(nameof(Motions))]
        public void Motion_The_Budget_Cannot_Afford_Stretches(Target target, Motion motion)
        {
            using var output = new FrameTarget(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var budget = new InjectedBudget(budget: s_runCost / 2, glyphCost: GlyphCost);
            using var animated = WideRunMaskTests.CreateRun(typeface, Text, 18, new Point(8.37, 32.61));

            var stretched = new List<int>();
            var expected = new List<int>();

            for (var frame = 0; frame < 16; frame++)
            {
                var before = TextTestApplication.ThreadState.StretchedDraws;

                output.Frame(context =>
                {
                    context.Transform = At(motion, frame);
                    context.DrawGlyphRun(Brushes.Black, animated);
                });

                if (TextTestApplication.ThreadState.StretchedDraws != before)
                {
                    stretched.Add(frame);
                }

                if (frame >= TransformChurnGuard.Threshold && !CrossesBand(target, motion, frame))
                {
                    expected.Add(frame);
                }
            }

            Assert.Equal(expected, stretched);
        }

        [Theory]
        [MemberData(nameof(Motions))]
        public void The_Runs_Of_A_Frame_Share_One_Budget(Target target, Motion motion)
        {
            // Room for three runs a frame: all three draw sharp, in every frame. Ten frames keep
            // the zooms within the stretch band.
            using var output = new FrameTarget(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var budget = new InjectedBudget(budget: 3.5 * s_runCost, glyphCost: GlyphCost);
            using var runs = new Runs(typeface, 3);

            var stretched = Animate(output, runs.All, 10, motion, (_, _) => true);

            Assert.All(stretched, frames => Assert.Empty(frames));
        }

        [Theory]
        [MemberData(nameof(Motions))]
        public void The_Runs_After_The_Budget_Is_Spent_Stretch_And_So_Do_The_Frames_After(Target target,
            Motion motion)
        {
            // Room for two runs a frame: the third run of the first moving frame stretches, and
            // so does every run of the frames after it.
            using var output = new FrameTarget(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var budget = new InjectedBudget(budget: 2.5 * s_runCost, glyphCost: GlyphCost);
            using var runs = new Runs(typeface, 3);

            const int moving = TransformChurnGuard.Threshold;
            var stretched = Animate(output, runs.All, 10, motion, (_, _) => true);

            Assert.Equal(Range(moving + 1, 10), stretched[0]);
            Assert.Equal(Range(moving + 1, 10), stretched[1]);
            Assert.Equal(Range(moving, 10), stretched[2]);
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void A_Frame_Past_The_Budget_Stretches_Every_Run_For_The_Hold(Target target)
        {
            // In the first moving frame a third run takes the room the last run would have had,
            // and then leaves. Every run stretches for the hold although the budget would afford
            // the two left at once, and they draw sharp after it.
            using var output = new FrameTarget(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var budget = new InjectedBudget(budget: 2.5 * s_runCost, glyphCost: GlyphCost);
            using var runs = new Runs(typeface, 3);

            const int moving = TransformChurnGuard.Threshold;
            const int end = moving + MotionRasterBudget.HoldFrames + 1;
            var stretched = Animate(output, runs.All, end + 4, Motion.Rotation,
                (run, frame) => run != 1 || frame <= moving);

            Assert.Equal(Range(moving + 1, end), stretched[0]);
            Assert.Equal(Range(moving, end), stretched[2]);
        }

        [Theory]
        [MemberData(nameof(Motions))]
        public void A_Scene_Estimated_Past_Twice_The_Budget_Stretches_From_Its_First_Moving_Frame(Target target,
            Motion motion)
        {
            // Three runs and room for one. The frames before the motion is recognized draw the
            // runs under changing transforms and show the scene at more than twice the budget, so
            // the first moving frame stretches every run instead of trying the budget.
            using var output = new FrameTarget(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var budget = new InjectedBudget(budget: 1.2 * s_runCost, glyphCost: GlyphCost);
            using var runs = new Runs(typeface, 3);

            const int moving = TransformChurnGuard.Threshold;
            var stretched = Animate(output, runs.All, 10, motion, (_, _) => true);

            Assert.All(stretched, frames => Assert.Equal(Range(moving, 10), frames));
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void A_Scene_Estimated_Past_Twice_The_Budget_Is_Never_Tried(Target target)
        {
            // The same scene keeps stretching every run through what would be several holds.
            using var output = new FrameTarget(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var budget = new InjectedBudget(budget: 1.2 * s_runCost, glyphCost: GlyphCost);
            using var runs = new Runs(typeface, 3);

            const int moving = TransformChurnGuard.Threshold;
            const int frames = moving + 3 * (MotionRasterBudget.HoldFrames + 1) + 4;
            var stretched = Animate(output, runs.All, frames, Motion.Rotation, (_, _) => true);

            Assert.All(stretched, run => Assert.Equal(Range(moving, frames), run));
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void A_Scene_That_Keeps_Running_Out_Of_Budget_Is_Tried_Again_After_Doubling_Holds(Target target)
        {
            // Three runs and room for two: every try runs out of budget on the third run, so each
            // hold lasts twice as long as the one before. The frame before each retry estimates
            // the scene at half its cost, under the budget, as a scene whose estimate fits but
            // whose frames do not.
            using var output = new FrameTarget(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var budget = new InjectedBudget(budget: 2.5 * s_runCost, glyphCost: GlyphCost);
            using var runs = new Runs(typeface, 3);

            const int moving = TransformChurnGuard.Threshold;
            const int hold = MotionRasterBudget.HoldFrames;
            var tried = new[] { moving, moving + hold + 1, moving + hold + 1 + 2 * hold + 1 };
            var frames = tried[2] + 4;
            var stretched = Animate(output, runs.All, frames, Motion.Rotation, (_, _) => true,
                glyphCost: frame => Array.IndexOf(tried, frame + 1) > 0 ? GlyphCost / 2 : GlyphCost);
            var expected = Range(moving, frames).FindAll(frame => Array.IndexOf(tried, frame) < 0);

            Assert.Equal(expected, stretched[0]);
            Assert.Equal(expected, stretched[1]);
            Assert.Equal(Range(moving, frames), stretched[2]);
        }

        public static IEnumerable<object[]> Zooms()
        {
            foreach (var target in Enum.GetValues<Target>())
            {
                yield return new object[] { target, Motion.Zoom };
                yield return new object[] { target, Motion.ZoomRotation };
            }
        }

        [Theory]
        [MemberData(nameof(Zooms))]
        public void A_Sawtooth_Zoom_That_Keeps_Running_Out_Of_Budget_Doubles_Its_Holds_Across_Wraps(Target target,
            Motion motion)
        {
            // Three runs and room for two, zooming by 1 % a frame and jumping back to the start
            // scale every 20 frames, which the frames before the motion was recognized left
            // cached: the frames that pass through cached scales still move, so every try runs
            // out of budget on the third run and each hold lasts twice as long as the one before.
            // The frame before each retry estimates the scene at half its cost, under the budget.
            const int period = 20;
            const int moving = TransformChurnGuard.Threshold;
            const int hold = MotionRasterBudget.HoldFrames;

            using var output = new FrameTarget(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var budget = new InjectedBudget(budget: 2.5 * s_runCost, glyphCost: GlyphCost);
            using var runs = new Runs(typeface, 3);

            var expected = new List<int> { moving, moving + hold + 1 };

            expected.Add(expected[1] + 2 * hold + 1);
            expected.Add(expected[2] + 4 * hold + 1);

            var frames = expected[3] + 4;
            var tried = new List<int>();

            for (var frame = 0; frame < frames; frame++)
            {
                var scale = 1 + 0.01 * (frame % period);
                var before = ThreadTriedFrames;

                var cost = expected.IndexOf(frame + 1) > 0 ? GlyphCost / 2 : GlyphCost;

                TestRasterTuning.Set(t => t with { MotionGlyphCostMicroseconds = cost });

                output.Frame(context =>
                {
                    for (var i = 0; i < runs.All.Length; i++)
                    {
                        context.Transform = motion == Motion.Zoom
                            ? Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(10.25, 12.5 + 40 * i)
                            : Matrix.CreateScale(scale, scale) * Matrix.CreateRotation(Math.PI * 15 / 180) *
                              Matrix.CreateTranslation(60.4, 40.7 + 40 * i);
                        context.DrawGlyphRun(Brushes.Black, runs.All[i]);
                    }
                });

                if (ThreadTriedFrames != before)
                {
                    tried.Add(frame);
                }
            }

            Assert.Equal(expected, tried);
        }

        [Theory]
        [MemberData(nameof(Zooms))]
        public void A_Sawtooth_Zoom_Estimated_Past_Twice_The_Budget_Is_Never_Tried_After_Its_Wraps(Target target,
            Motion motion)
        {
            // Three runs and room for one, so the scene is estimated past twice the budget. The
            // frames after each wrap draw rasters cached by the frames before the motion was
            // recognized; they still count towards the scene's estimate, so the frame after them
            // does not try either.
            const int period = 20;

            using var output = new FrameTarget(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var budget = new InjectedBudget(budget: 1.2 * s_runCost, glyphCost: GlyphCost);
            using var runs = new Runs(typeface, 3);

            var tried = new List<int>();

            for (var frame = 0; frame < 3 * period; frame++)
            {
                var scale = 1 + 0.01 * (frame % period);
                var before = ThreadTriedFrames;

                output.Frame(context =>
                {
                    for (var i = 0; i < runs.All.Length; i++)
                    {
                        context.Transform = motion == Motion.Zoom
                            ? Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(10.25, 12.5 + 40 * i)
                            : Matrix.CreateScale(scale, scale) * Matrix.CreateRotation(Math.PI * 15 / 180) *
                              Matrix.CreateTranslation(60.4, 40.7 + 40 * i);
                        context.DrawGlyphRun(Brushes.Black, runs.All[i]);
                    }
                });

                if (ThreadTriedFrames != before)
                {
                    tried.Add(frame);
                }
            }

            Assert.Empty(tried);
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void A_Scene_Whose_Tries_Run_Out_Of_Budget_At_A_Flat_Cost_Stops_Trying_Under_Twice_The_Budget(
            Target target)
        {
            // Three runs and room for two: the scene is estimated under twice the budget, so its
            // first moving frame tries and runs out, and so does the try after the hold. That
            // second try cost as much per glyph as the first: the scene is not warming up, and a
            // further try needs the estimate to fit the budget itself, which this scene's never does.
            const int moving = TransformChurnGuard.Threshold;
            const int hold = MotionRasterBudget.HoldFrames;

            var tried = TriedFrames(target, moving + 6 * hold, _ => GlyphCost);

            Assert.Equal(new[] { moving, moving + hold + 1 }, tried);
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void A_Scene_Whose_Cost_Wobbles_Between_Failed_Tries_Stops_Trying_Under_Twice_The_Budget(Target target)
        {
            // The same scene, but its second try costs 15 % less per glyph than its first, as the
            // timing of a warm scene can vary: a drop that small is not code still compiling, and a
            // further try needs the estimate to fit the budget itself.
            const int moving = TransformChurnGuard.Threshold;
            const int hold = MotionRasterBudget.HoldFrames;
            var tries = new[] { moving, moving + hold + 1, moving + hold + 1 + 2 * hold + 1 };

            var tried = TriedFrames(target, moving + 6 * hold,
                frame => Array.IndexOf(tries, frame) is var i and >= 0 ? GlyphCost * (i % 2 == 0 ? 1 : 0.85) : GlyphCost);

            Assert.Equal(new[] { moving, moving + hold + 1 }, tried);
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void A_Scene_Whose_Cost_Falls_Between_Failed_Tries_Keeps_Trying_Under_Twice_The_Budget(Target target)
        {
            // Twelve runs and room for two and a half at the cost of a try's first frame, and each
            // try costs half as much per glyph as the one before, as code still compiling does, yet
            // still runs out of budget: it keeps trying after every hold, which doubles. The frames
            // between tries estimate the scene at a quarter of the first try's cost per glyph, under
            // twice the budget.
            const int moving = TransformChurnGuard.Threshold;
            const int hold = MotionRasterBudget.HoldFrames;
            var expected = new[] { moving, moving + hold + 1, moving + hold + 1 + 2 * hold + 1 };

            var tried = TriedFrames(target, expected[2] + 4,
                frame => Array.IndexOf(expected, frame) is var i and >= 0 ? GlyphCost / (1 << i) : GlyphCost / 4,
                runCount: 12);

            Assert.Equal(expected, tried);
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void A_Failed_Scene_Estimated_Past_Twice_The_Budget_Is_Tried_Once_When_The_Hold_Reaches_Four_Times_The_First(
            Target target)
        {
            // Three runs and room for two: the first moving frame tries and runs out, and every frame
            // after it costs twice as much per glyph, which estimates the scene past twice the
            // budget, as a scene first measured cold. Its holds keep doubling without a try, and the
            // frame after the first hold of four times the first length tries once more, within the
            // first gesture of a process. That try runs out too, and the scene then stretches
            // through further holds of the longest length.
            const int moving = TransformChurnGuard.Threshold;
            const int hold = MotionRasterBudget.HoldFrames;
            const int longest = MotionRasterBudget.MaxHoldFrames;
            var retry = moving + (hold + 1) + (2 * hold + 1) + (4 * hold + 1);

            Assert.Equal(16 * hold, longest);

            var tried = TriedFrames(target, retry + 2 * (longest + 1) + 4,
                frame => frame <= moving ? GlyphCost : 2 * GlyphCost);

            Assert.Equal(new[] { moving, retry }, tried);
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void A_Scene_Stopped_Within_Twice_The_Budget_After_A_Flat_Cost_Is_Tried_Once_More_And_Then_Never(
            Target target)
        {
            // The scene whose two tries ran out of budget at a flat cost needs its estimate to fit
            // the budget itself, which it never does. When its hold has doubled to four times the
            // first, it is tried once more, as code still compiling may have
            // cost both tries alike; that try runs out at the same cost, and past the frame a hold
            // of the longest length would end on, it still does not try.
            const int moving = TransformChurnGuard.Threshold;
            const int hold = MotionRasterBudget.HoldFrames;
            const int longest = MotionRasterBudget.MaxHoldFrames;
            var second = moving + hold + 1;
            var third = second + (2 * hold + 1) + (4 * hold + 1);
            var longestEnd = second + (2 * hold + 1) + (4 * hold + 1) + (8 * hold + 1) + (longest + 1);

            var tried = TriedFrames(target, longestEnd + longest + 4, _ => GlyphCost);

            Assert.Equal(new[] { moving, second, third }, tried);
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void A_Scene_Whose_Cold_Tries_Ran_Out_At_A_Flat_Cost_Becomes_Sharp_Once_Its_Code_Runs_Warm(
            Target target)
        {
            // The first tries of a process run on code still compiling, and two of them can cost
            // alike per glyph while both cost several times the warm code. The scene is tried once
            // more when its hold has doubled to four times the first; by
            // then its code runs warm, at half the cost per glyph, the try fits, and every frame
            // after it draws sharp.
            const int moving = TransformChurnGuard.Threshold;
            const int hold = MotionRasterBudget.HoldFrames;
            var second = moving + hold + 1;
            var third = second + (2 * hold + 1) + (4 * hold + 1);
            var frames = third + 40;

            var expected = new List<int> { moving, second };

            expected.AddRange(Range(third, frames));

            var tried = TriedFrames(target, frames, frame => frame < third ? GlyphCost : GlyphCost / 2);

            Assert.Equal(expected, tried);
        }

        /// <summary>
        /// Rotates <paramref name="runCount"/> runs, with room for two and a half at a cost per
        /// glyph of <see cref="GlyphCost"/>, for <paramref name="frames"/> frames at the frame's
        /// <paramref name="glyphCost"/>, and returns the frames that tried the budget.
        /// </summary>
        private static List<int> TriedFrames(Target target, int frames, Func<int, double> glyphCost, int runCount = 3)
        {
            using var output = new FrameTarget(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var budget = new InjectedBudget(budget: 2.5 * s_runCost, glyphCost: GlyphCost);
            using var runs = new Runs(typeface, runCount);

            var tried = new List<int>();

            for (var frame = 0; frame < frames; frame++)
            {
                var before = ThreadTriedFrames;
                var current = frame;

                TestRasterTuning.Set(t => t with { MotionGlyphCostMicroseconds = glyphCost(frame) });

                output.Frame(context =>
                {
                    for (var i = 0; i < runs.All.Length; i++)
                    {
                        context.Transform = At(Motion.Rotation, current, 25 * i);
                        context.DrawGlyphRun(Brushes.Black, runs.All[i]);
                    }
                });

                if (ThreadTriedFrames != before)
                {
                    tried.Add(frame);
                }
            }

            return tried;
        }

        [Theory]
        [InlineData(Target.HardwareGl)]
        [InlineData(Target.HardwareAngle)]
        public void Sharp_Frames_Of_A_Gesture_Never_Evict_Its_Band_Crossings(Target target)
        {
            // The zoom stretches and crosses the band while the budget affords nothing, then the
            // budget affords every frame and six sharp frames lay out sprite sets of their own,
            // more than a run keeps for its other scales. A jump back to the crossing's scale
            // still finds the crossing's sprites and lays out nothing.
            using var output = new FrameTarget(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var budget = new InjectedBudget(budget: 0, glyphCost: GlyphCost);
            using var runs = new Runs(typeface, 1);

            static Matrix Zoom(double scale) => Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(10.25, 12.5);

            // The sprite sets the frame laid out; its glyph masks may come from the mask cache.
            long Draw(double scale)
            {
                var before = GlyphRasterDiagnostics.SpriteSetBuildsOnThread;

                output.Frame(context =>
                {
                    context.Transform = Zoom(scale);
                    context.DrawGlyphRun(Brushes.Black, runs.All[0]);
                });

                return GlyphRasterDiagnostics.SpriteSetBuildsOnThread - before;
            }

            var crossing = 0.0;

            for (var frame = 0; frame < 12; frame++)
            {
                var scale = 1 + 0.04 * frame;

                if (Draw(scale) > 0 && frame >= TransformChurnGuard.Threshold && crossing == 0)
                {
                    crossing = scale;
                }
            }

            Assert.True(crossing > 0, "the zoom never crossed the band");

            TestRasterTuning.Set(t => t with { MotionBudgetMicroseconds = 1e9 });

            for (var frame = 12; frame < 18; frame++)
            {
                Assert.True(Draw(1 + 0.04 * frame) > 0, $"frame {frame} did not draw sharp");
            }

            TestRasterTuning.Set(t => t with { MotionBudgetMicroseconds = 0 });

            Assert.Equal(0, Draw(crossing));
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void A_Try_That_Fits_The_Budget_Ends_The_Doubling(Target target)
        {
            // The first try runs out of budget; the third run then leaves, the try after the hold
            // fits, and when the third run comes back and the budget runs out again the hold is
            // the first one's length again.
            using var output = new FrameTarget(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var budget = new InjectedBudget(budget: 2.5 * s_runCost, glyphCost: GlyphCost);
            using var runs = new Runs(typeface, 3);

            const int moving = TransformChurnGuard.Threshold;
            const int hold = MotionRasterBudget.HoldFrames;
            const int back = moving + hold + 7;
            const int frames = back + hold + 5;
            var stretched = Animate(output, runs.All, frames, Motion.Rotation,
                (run, frame) => run != 1 || frame <= moving || frame >= back,
                glyphCost: frame => frame == back + hold ? GlyphCost / 2 : GlyphCost);

            // The first run is sharp on the first try, after the first hold, until the third run
            // comes back, and on the try after the second hold, which the frame before it, at half
            // the cost, estimated under the budget.
            var sharp = new List<int> { moving, back + hold + 1 };

            for (var frame = moving + hold + 1; frame <= back; frame++)
            {
                sharp.Add(frame);
            }

            Assert.Equal(Range(moving, frames).FindAll(frame => !sharp.Contains(frame)), stretched[0]);
            Assert.Equal(Range(moving, frames).FindAll(frame => frame < moving + hold + 1 || frame >= back),
                stretched[2]);
        }

        [Theory]
        [MemberData(nameof(Motions))]
        public void A_Frame_With_Little_Headroom_Stretches_Text_That_Fits_The_Cap(Target target, Motion motion)
        {
            // The frame spends nearly all of its interval outside text: what is left after the
            // safety margin cannot take the run, though the cap could many times over.
            const double nonText = 5000;
            const double headroom = 10;

            using var output = new FrameTarget(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var budget = new InjectedBudget(budget: 100 * s_runCost, glyphCost: GlyphCost,
                interval: nonText + MotionRasterBudget.SafetyMarginMicroseconds + headroom, nonText: nonText);
            using var runs = new Runs(typeface, 1);

            const int moving = TransformChurnGuard.Threshold;
            var stretched = Animate(output, runs.All, 10, motion, (_, _) => true);

            Assert.Equal(Range(moving, 10), stretched[0]);
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void A_Frame_That_Spends_Little_Outside_Text_Gets_The_Whole_Cap(Target target)
        {
            // An otherwise idle frame at 120 Hz: three runs that need nearly the whole cap draw
            // sharp in every frame.
            using var output = new FrameTarget(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var budget = new InjectedBudget(budget: 3.2 * s_runCost, glyphCost: GlyphCost,
                interval: 1e6 / 120, nonText: 300);
            using var runs = new Runs(typeface, 3);

            var stretched = Animate(output, runs.All, 10, Motion.Rotation, (_, _) => true);

            Assert.All(stretched, frames => Assert.Empty(frames));
        }

        [Fact]
        public void The_Time_Outside_Text_Is_Smoothed_Over_Recent_Frames()
        {
            // Frames of 5 ms with 1 ms of text: 4 ms outside text, whatever came before, and
            // moved only part of the way by one slow frame.
            var budget = new MotionRasterBudget(new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes));

            for (var frame = 0; frame < 60; frame++)
            {
                budget.RecordFrame(renderMicroseconds: 5000, textMicroseconds: 1000);
            }

            Assert.InRange(budget.NonTextMicroseconds(GlyphRasterTuning.Default), 3990, 4010);

            budget.RecordFrame(renderMicroseconds: 13000, textMicroseconds: 1000);

            Assert.InRange(budget.NonTextMicroseconds(GlyphRasterTuning.Default), 4500, 7000);
        }

        [Fact]
        public void The_Frame_Interval_Is_A_120_Hz_Frame_Unless_Injected()
        {
            Assert.Equal(1e6 / 120, MotionRasterBudget.FrameIntervalMicroseconds(GlyphRasterTuning.Default), 3);
            Assert.Equal(16667, MotionRasterBudget.FrameIntervalMicroseconds(
                GlyphRasterTuning.Default with { MotionFrameIntervalMicroseconds = 16667 }), 3);
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void Motion_After_A_Stop_Is_Weighed_Anew(Target target)
        {
            // A held frame ends the motion, and with it the hold: the next motion draws sharp
            // when the moving text of the frame before it fit the budget.
            using var output = new FrameTarget(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var budget = new InjectedBudget(budget: 2.5 * s_runCost, glyphCost: GlyphCost);
            using var runs = new Runs(typeface, 3);

            const int moving = TransformChurnGuard.Threshold;
            const int held = moving + 4;

            // The rotation holds for one frame and moves on.
            var stretched = Animate(output, runs.All, held + 8, Motion.Rotation,
                (run, frame) => run != 1 || frame <= moving,
                frame => frame <= held ? frame : frame - 1);

            Assert.Equal(Range(moving + 1, held + 1), stretched[0]);
            Assert.Equal(Range(moving, held + 1), stretched[2]);
        }

        /// <summary>The frames in which a moving run of this thread's application drew sharp, a running total.</summary>
        private static int ThreadTriedFrames => GlyphRasterServices.Current.ForCurrentThread().Motion.TriedFrames;

        /// <summary>The frames from <paramref name="from"/> up to, not including, <paramref name="to"/>.</summary>
        private static List<int> Range(int from, int to)
        {
            var frames = new List<int>();

            for (var frame = from; frame < to; frame++)
            {
                frames.Add(frame);
            }

            return frames;
        }

        /// <summary>
        /// Draws <paramref name="frames"/> frames of <paramref name="runs"/>, run i 40 px further
        /// down per index and only where <paramref name="drawn"/> says so, at the transform of the
        /// frame's <paramref name="step"/>, with the frame's <paramref name="glyphCost"/> when
        /// given; returns per run the frames it stretched in.
        /// </summary>
        private static List<int>[] Animate(FrameTarget output, ManagedGlyphRunImpl[] runs, int frames, Motion motion,
            Func<int, int, bool> drawn, Func<int, int>? step = null, Func<int, double>? glyphCost = null)
        {
            var stretched = new List<int>[runs.Length];

            for (var i = 0; i < runs.Length; i++)
            {
                stretched[i] = new List<int>();
            }

            for (var frame = 0; frame < frames; frame++)
            {
                var at = step?.Invoke(frame) ?? frame;
                var current = frame;

                if (glyphCost is not null)
                {
                    TestRasterTuning.Set(t => t with { MotionGlyphCostMicroseconds = glyphCost(frame) });
                }

                output.Frame(context =>
                {
                    for (var i = 0; i < runs.Length; i++)
                    {
                        if (!drawn(i, current))
                        {
                            continue;
                        }

                        var before = TextTestApplication.ThreadState.StretchedDraws;

                        context.Transform = At(motion, at, 40 * i);
                        context.DrawGlyphRun(Brushes.Black, runs[i]);

                        if (TextTestApplication.ThreadState.StretchedDraws != before)
                        {
                            stretched[i].Add(current);
                        }
                    }
                });
            }

            return stretched;
        }

        /// <summary>
        /// Whether a stretching run's zoom-rotation at <paramref name="frame"/> leaves the band
        /// around the scale it last rasterized at, the last frame before the motion was recognized
        /// or the last crossing, and draws that frame sharp.
        /// </summary>
        private static bool CrossesBand(Target target, Motion motion, int frame)
        {
            // A software GPU stretches an upright zoom without bound, and a hardware GPU stretches
            // the sprites of its grid crossings to every frame.
            if (motion == Motion.Rotation || frame < TransformChurnGuard.Threshold || motion == Motion.Zoom)
            {
                return false;
            }

            static double Scale(int f) => 1 + 0.03 * f;

            var anchor = Scale(TransformChurnGuard.Threshold - 1);

            for (var f = TransformChurnGuard.Threshold; f <= frame; f++)
            {
                if (Scale(f) / anchor > MaskGlyphRunRenderer.MaxStretchScale)
                {
                    if (f == frame)
                    {
                        return true;
                    }

                    anchor = Scale(f);
                }
            }

            return false;
        }

        /// <summary>Runs of <see cref="Text"/> at 18 px, disposed together.</summary>
        private sealed class Runs : IDisposable
        {
            public Runs(GlyphTypeface typeface, int count)
            {
                All = new ManagedGlyphRunImpl[count];

                for (var i = 0; i < count; i++)
                {
                    All[i] = WideRunMaskTests.CreateRun(typeface, Text, 18, new Point(8.37, 32.61));
                }
            }

            public ManagedGlyphRunImpl[] All { get; }

            public void Dispose()
            {
                foreach (var run in All)
                {
                    run.Dispose();
                }
            }
        }

        /// <summary>
        /// Sets the budget's cap, the cost per glyph, the frame interval and the time a frame spends
        /// outside text for the scope; the time outside text is none unless given.
        /// </summary>
        private sealed class InjectedBudget : IDisposable
        {
            private readonly IDisposable _tuning;

            public InjectedBudget(double budget, double glyphCost, double? interval = null, double nonText = 0)
            {
                _tuning = TestRasterTuning.Change(t => t with
                {
                    MotionBudgetMicroseconds = budget,
                    MotionGlyphCostMicroseconds = glyphCost,
                    MotionFrameIntervalMicroseconds = interval,
                    MotionNonTextMicroseconds = nonText,
                });
            }

            public void Dispose() => _tuning.Dispose();
        }

        /// <summary>
        /// A GPU surface drawn by a new context per frame, as a window's render passes draw it,
        /// so every frame has a budget of its own.
        /// </summary>
        private sealed class FrameTarget : IDisposable
        {
            private readonly GpuTestContext _gpu;
            private readonly SKSurface _surface;

            public FrameTarget(Target target)
            {
                _gpu = TransformedAtlasTests.CreateGpu(
                    target switch
                    {
                        Target.HardwareAngle => GpuBackend.Angle,
                        Target.SoftwareMetal or Target.HardwareMetal => GpuBackend.Metal,
                        _ => GpuBackend.NativeGl,
                    },
                    software: target is Target.SoftwareGl or Target.SoftwareMetal);

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

                using (var context = TransformedAtlasTests.CreateContext(_gpu, _surface))
                {
                    draw(context);
                }

                _gpu.GrContext.Flush();

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
                _gpu.GrContext.Flush();
                _gpu.Dispose();
            }
        }
    }
}
