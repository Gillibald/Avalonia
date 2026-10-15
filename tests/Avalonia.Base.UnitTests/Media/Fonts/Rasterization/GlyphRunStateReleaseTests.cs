using System;
using System.Threading;
using Avalonia.Media.Fonts.Rasterization;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization
{
    /// <summary>
    /// Released run state is disposed once the frame that last drew the run has ended, so the
    /// frame's pending draws never lose what they hold. Each test uses a budget of its own.
    /// </summary>
    public class GlyphRunStateReleaseTests
    {
        [Fact]
        public void State_No_Open_Frame_Drew_Is_Disposed_At_Once()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var state = new CountingState();
            long drawn;

            using (budget.BeginFrame())
            {
                drawn = budget.Frame;
            }

            budget.ReleaseRunState(state, drawn, Environment.CurrentManagedThreadId);

            Assert.Equal(1, state.Disposals);
            Assert.Equal(0, budget.PendingRunStateReleases);
        }

        [Fact]
        public void State_Drawn_By_An_Open_Frame_Is_Disposed_When_A_Frame_Begins_After_It_Ends()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var state = new CountingState();

            using (budget.BeginFrame())
            {
                budget.ReleaseRunState(state, budget.Frame, Environment.CurrentManagedThreadId);

                Assert.Equal(0, state.Disposals);
            }

            Assert.Equal(0, state.Disposals);

            using (budget.BeginFrame())
            {
                Assert.Equal(1, state.Disposals);
            }

            using (budget.BeginFrame())
            {
            }

            Assert.Equal(1, state.Disposals);
            Assert.Equal(0, budget.PendingRunStateReleases);
        }

        [Fact]
        public void A_Release_From_Another_Thread_Waits_For_The_Drawing_Threads_Frame()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var state = new CountingState();

            using (budget.BeginFrame())
            {
                var drawn = budget.Frame;
                var drawer = Environment.CurrentManagedThreadId;
                var releaser = new Thread(() => budget.ReleaseRunState(state, drawn, drawer));

                releaser.Start();
                releaser.Join();

                Assert.Equal(0, state.Disposals);
            }

            using (budget.BeginFrame())
            {
            }

            Assert.Equal(1, state.Disposals);
        }

        [Fact]
        public void Another_Threads_Open_Frame_Does_Not_Hold_Back_State_It_Did_Not_Draw()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var state = new CountingState();
            using var started = new ManualResetEventSlim();
            using var finish = new ManualResetEventSlim();

            // A long frame of another thread, such as a bitmap rendered on the UI thread.
            var other = new Thread(() =>
            {
                using (budget.BeginFrame())
                {
                    started.Set();
                    finish.Wait();
                }
            });

            other.Start();
            started.Wait(TestContext.Current.CancellationToken);

            try
            {
                long drawn;

                using (budget.BeginFrame())
                {
                    drawn = budget.Frame;
                }

                budget.ReleaseRunState(state, drawn, Environment.CurrentManagedThreadId);

                Assert.Equal(1, state.Disposals);
            }
            finally
            {
                finish.Set();
                other.Join();
            }
        }

        [Fact]
        public void A_Trim_After_Frames_Stop_Disposes_Held_State()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var state = new CountingState();

            using (budget.BeginFrame())
            {
                budget.ReleaseRunState(state, budget.Frame, Environment.CurrentManagedThreadId);
            }

            budget.TrimForMemoryPressure();

            Assert.Equal(1, state.Disposals);
            Assert.Equal(0, budget.PendingRunStateReleases);
        }

        private sealed class CountingState : IDisposable
        {
            public int Disposals { get; private set; }

            public void Dispose() => Disposals++;
        }
    }
}
