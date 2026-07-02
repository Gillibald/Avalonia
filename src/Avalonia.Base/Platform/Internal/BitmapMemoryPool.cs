using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Logging;
using Avalonia.Media.Imaging;

namespace Avalonia.Platform.Internal
{
    /// <summary>
    /// The default <see cref="IBitmapMemoryAllocator"/>: a bucketed native pool with
    /// power-of-two size classes and a retention ceiling counted in bucket bytes.
    /// </summary>
    /// <remarks>
    /// The ceiling bounds idle (pooled) memory, not live rentals: returning a buffer
    /// that would exceed the ceiling first trims idle buffers of other, least recently
    /// used size classes to make room; when that is not enough the buffer is freed
    /// instead of pooled, so a single oversized image cannot permanently inflate the
    /// process. Rentals carry a finalizer backstop that returns leaked buffers.
    /// </remarks>
    /// <summary>
    /// Resolves the allocator the imaging layer uses: an application/test-bound
    /// <see cref="IBitmapMemoryAllocator"/>, or the shared pool.
    /// </summary>
    internal static class BitmapMemoryAllocator
    {
        public static IBitmapMemoryAllocator Current =>
            AvaloniaLocator.Current.GetService<IBitmapMemoryAllocator>() ?? BitmapMemoryPool.Shared;
    }

    internal sealed class BitmapMemoryPool : IBitmapMemoryAllocator
    {
        internal const long MinBucketBytes = 4096;

        private static BitmapMemoryPool? s_shared;

        private readonly object _lock = new();
        private readonly Dictionary<long, Stack<IntPtr>> _idle = new();
        private readonly Dictionary<long, long> _lastUse = new();
        private readonly long _fixedMaxPoolBytes;
        private readonly bool _trackOptions;
        private long _pooledBytes;
        private long _clock;

        public BitmapMemoryPool(long maxPoolBytes)
        {
            if (maxPoolBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(maxPoolBytes));

            _fixedMaxPoolBytes = maxPoolBytes;
        }

        private BitmapMemoryPool()
        {
            _trackOptions = true;
        }

        /// <summary>
        /// Gets the process-wide pool. Its ceiling follows
        /// <see cref="ImagingOptions.DecodePoolMaxBytes"/> on every return, so options
        /// bound after the first imaging call still take effect.
        /// </summary>
        public static BitmapMemoryPool Shared
        {
            get
            {
                var shared = Volatile.Read(ref s_shared);

                if (shared is null)
                {
                    shared = new BitmapMemoryPool();

                    shared = Interlocked.CompareExchange(ref s_shared, shared, null) ?? shared;
                }

                return shared;
            }
        }

        internal long MaxPoolBytes =>
            _trackOptions ? ImagingOptions.Effective.DecodePoolMaxBytes : _fixedMaxPoolBytes;

        /// <summary>
        /// Gets the bucket bytes currently held idle in the pool.
        /// </summary>
        internal long PooledBytes
        {
            get
            {
                lock (_lock)
                {
                    return _pooledBytes;
                }
            }
        }

        public IBitmapMemory Rent(long byteCount)
        {
            if (byteCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(byteCount));

            var bucket = RoundUpToBucket(byteCount);

            lock (_lock)
            {
                if (_idle.TryGetValue(bucket, out var stack) && stack.Count > 0)
                {
                    var pooled = stack.Pop();

                    _pooledBytes -= bucket;

                    return new PooledBitmapMemory(this, pooled, byteCount, bucket);
                }
            }

            var address = Marshal.AllocHGlobal(new IntPtr(bucket));

            GC.AddMemoryPressure(bucket);

            return new PooledBitmapMemory(this, address, byteCount, bucket);
        }

        /// <summary>
        /// Frees all idle buffers.
        /// </summary>
        internal void Trim()
        {
            lock (_lock)
            {
                foreach (var pair in _idle)
                {
                    while (pair.Value.Count > 0)
                    {
                        Free(pair.Value.Pop(), pair.Key);
                    }
                }

                _idle.Clear();
                _lastUse.Clear();
                _pooledBytes = 0;
            }
        }

        internal static long RoundUpToBucket(long byteCount)
        {
            return byteCount <= MinBucketBytes
                ? MinBucketBytes
                : (long)System.Numerics.BitOperations.RoundUpToPowerOf2((ulong)byteCount);
        }

        private void Return(IntPtr address, long bucket)
        {
            lock (_lock)
            {
                if (_pooledBytes + bucket > MaxPoolBytes)
                    TrimLeastRecentlyUsed(bucket, _pooledBytes + bucket - MaxPoolBytes);

                if (_pooledBytes + bucket <= MaxPoolBytes)
                {
                    if (!_idle.TryGetValue(bucket, out var stack))
                    {
                        stack = new Stack<IntPtr>();
                        _idle.Add(bucket, stack);
                    }

                    stack.Push(address);
                    _pooledBytes += bucket;
                    _lastUse[bucket] = ++_clock;

                    return;
                }
            }

            Free(address, bucket);
        }

        private void TrimLeastRecentlyUsed(long keepBucket, long bytesNeeded)
        {
            // Free idle buffers of other, least recently used size classes until the
            // requested amount is reclaimed, so a mixed-size workload cannot pin the
            // pool full of wrong-class buckets.
            while (bytesNeeded > 0)
            {
                long oldestBucket = 0;
                var oldestUse = long.MaxValue;

                foreach (var pair in _idle)
                {
                    if (pair.Key == keepBucket || pair.Value.Count == 0)
                        continue;

                    if (_lastUse.TryGetValue(pair.Key, out var lastUse) && lastUse < oldestUse)
                    {
                        oldestUse = lastUse;
                        oldestBucket = pair.Key;
                    }
                }

                if (oldestBucket == 0)
                    return;

                var stack = _idle[oldestBucket];

                while (stack.Count > 0 && bytesNeeded > 0)
                {
                    Free(stack.Pop(), oldestBucket);

                    _pooledBytes -= oldestBucket;
                    bytesNeeded -= oldestBucket;
                }

                if (stack.Count == 0)
                {
                    _idle.Remove(oldestBucket);
                    _lastUse.Remove(oldestBucket);
                }
            }
        }

        private static void Free(IntPtr address, long pressureBytes)
        {
            Marshal.FreeHGlobal(address);

            if (pressureBytes > 0)
                GC.RemoveMemoryPressure(pressureBytes);
        }

        private sealed class PooledBitmapMemory : IBitmapMemory
        {
            private readonly BitmapMemoryPool _pool;
            private readonly long _bucket;
            private IntPtr _address;

            public PooledBitmapMemory(BitmapMemoryPool pool, IntPtr address, long byteCount, long bucket)
            {
                _pool = pool;
                _address = address;
                _bucket = bucket;
                ByteCount = byteCount;
            }

            public IntPtr Address =>
                _address != IntPtr.Zero ? _address : throw new ObjectDisposedException(nameof(IBitmapMemory));

            public long ByteCount { get; }

            public void Dispose()
            {
                ReturnCore();
                GC.SuppressFinalize(this);
            }

            ~PooledBitmapMemory()
            {
                // Backstop for leaked rentals: deterministic disposal is the contract,
                // the finalizer only prevents a leak from becoming permanent.
                Logger.TryGet(LogEventLevel.Warning, "Imaging")
                    ?.Log(this, "A pooled bitmap buffer of {ByteCount} bytes was not disposed and is reclaimed by the finalizer.", ByteCount);

                ReturnCore();
            }

            private void ReturnCore()
            {
                var address = Interlocked.Exchange(ref _address, IntPtr.Zero);

                if (address != IntPtr.Zero)
                    _pool.Return(address, _bucket);
            }
        }
    }
}
