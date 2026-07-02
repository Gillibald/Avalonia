using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Internal;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Imaging
{
    // The limiter is process-global; these tests serialize on the same collection to
    // avoid cross-talk on the active count.
    [Collection(nameof(DecodeConcurrencyLimiterTests))]
    [CollectionDefinition(nameof(DecodeConcurrencyLimiterTests), DisableParallelization = true)]
    public class DecodeConcurrencyLimiterTests
    {
        [Fact]
        public async Task Enter_Blocks_At_The_Configured_Limit_And_Proceeds_On_Release()
        {
            using var scope = AvaloniaLocator.EnterScope();

            AvaloniaLocator.CurrentMutable.Bind<ImagingOptions>()
                .ToConstant(new ImagingOptions { MaxConcurrentDecodes = 2 });

            var first = DecodeConcurrencyLimiter.Enter(CancellationToken.None);
            var second = DecodeConcurrencyLimiter.Enter(CancellationToken.None);

            Assert.Equal(2, DecodeConcurrencyLimiter.Active);

            var third = Task.Run(() =>
            {
                using (DecodeConcurrencyLimiter.Enter(CancellationToken.None))
                {
                    return DecodeConcurrencyLimiter.Active;
                }
            }, TestContext.Current.CancellationToken);

            // The third acquisition must not complete while both slots are held.
            var winner = await Task.WhenAny(third, Task.Delay(200, TestContext.Current.CancellationToken));

            Assert.NotSame(third, winner);

            first.Dispose();

            Assert.Equal(2, await third.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

            second.Dispose();

            Assert.Equal(0, DecodeConcurrencyLimiter.Active);
        }

        [Fact]
        public void Enter_Observes_Cancellation_While_Blocked()
        {
            using var scope = AvaloniaLocator.EnterScope();

            AvaloniaLocator.CurrentMutable.Bind<ImagingOptions>()
                .ToConstant(new ImagingOptions { MaxConcurrentDecodes = 1 });

            using var held = DecodeConcurrencyLimiter.Enter(CancellationToken.None);
            using var cancellation = new CancellationTokenSource(millisecondsDelay: 100);

            Assert.Throws<OperationCanceledException>(
                () => DecodeConcurrencyLimiter.Enter(cancellation.Token));

            Assert.Equal(1, DecodeConcurrencyLimiter.Active);
        }

        [Fact]
        public void Double_Dispose_Releases_Only_Once()
        {
            using var scope = AvaloniaLocator.EnterScope();

            var slot = DecodeConcurrencyLimiter.Enter(CancellationToken.None);

            slot.Dispose();
            slot.Dispose();

            Assert.Equal(0, DecodeConcurrencyLimiter.Active);
        }

        [Fact]
        public void Limit_Below_One_Is_Clamped()
        {
            using var scope = AvaloniaLocator.EnterScope();

            AvaloniaLocator.CurrentMutable.Bind<ImagingOptions>()
                .ToConstant(new ImagingOptions { MaxConcurrentDecodes = 0 });

            using var slot = DecodeConcurrencyLimiter.Enter(CancellationToken.None);

            Assert.Equal(1, DecodeConcurrencyLimiter.Active);
        }
    }
}
