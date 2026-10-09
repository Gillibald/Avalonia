using System;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Counts the bytes the calling thread allocates while the measured code runs.
    /// </summary>
    /// <remarks>
    /// <see cref="GC.GetAllocatedBytesForCurrentThread"/> counts the thread's current allocation
    /// context as allocated, less its unused end. While other threads allocate heavily, as test
    /// classes running in parallel do, the counter can advance by that unused end, up to the
    /// size of one allocation context, in code that allocates nothing. A gen0 collection retires
    /// the context first, so a measurement that starts right after one reads zero for code that
    /// allocates nothing.
    /// </remarks>
    internal static class ThreadAllocations
    {
        /// <summary>Starts a measurement; pass the result to <see cref="Since"/>.</summary>
        public static long Start()
        {
            GC.Collect(0, GCCollectionMode.Forced, blocking: true);

            return GC.GetAllocatedBytesForCurrentThread();
        }

        /// <summary>The bytes the calling thread allocated since <paramref name="start"/>.</summary>
        public static long Since(long start) => GC.GetAllocatedBytesForCurrentThread() - start;
    }
}
