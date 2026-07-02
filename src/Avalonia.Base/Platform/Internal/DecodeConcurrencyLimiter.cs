using System;
using System.Threading;
using Avalonia.Media.Imaging;

namespace Avalonia.Platform.Internal
{
    /// <summary>
    /// Bounds how many frame decodes hold pixel buffers concurrently, so N parallel
    /// decodes cannot multiply peak native memory without limit. The ceiling is read
    /// from <see cref="ImagingOptions.MaxConcurrentDecodes"/> on every acquisition, so
    /// options bound after the first decode still take effect.
    /// </summary>
    internal static class DecodeConcurrencyLimiter
    {
        private static readonly object s_lock = new();
        private static int s_active;

        /// <summary>
        /// Acquires a decode slot, blocking while the limit is reached. Dispose the
        /// returned token to release the slot; disposing more than once is safe.
        /// </summary>
        public static IDisposable Enter(CancellationToken cancellation)
        {
            lock (s_lock)
            {
                while (s_active >= EffectiveLimit())
                {
                    cancellation.ThrowIfCancellationRequested();

                    // The bounded wait re-samples the limit (options may be rebound)
                    // and the token, both of which cannot pulse the monitor.
                    Monitor.Wait(s_lock, 100);
                }

                s_active++;
            }

            return new Releaser();
        }

        /// <summary>
        /// Gets the number of currently held slots.
        /// </summary>
        internal static int Active
        {
            get
            {
                lock (s_lock)
                {
                    return s_active;
                }
            }
        }

        private static int EffectiveLimit() => Math.Max(1, ImagingOptions.Effective.MaxConcurrentDecodes);

        private sealed class Releaser : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;

                lock (s_lock)
                {
                    s_active--;
                    Monitor.Pulse(s_lock);
                }
            }
        }
    }
}
