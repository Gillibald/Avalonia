using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Imaging.TestKit.Instrumentation
{
    /// <summary>
    /// A decorating <see cref="IBitmapMemoryAllocator"/> that counts rentals, so the
    /// contract tests can assert copy budgets, allocation-free paths and pool balance.
    /// </summary>
    public sealed class CountingAllocator : IBitmapMemoryAllocator
    {
        private readonly IBitmapMemoryAllocator _inner;
        private readonly object _gate = new();
        private readonly Dictionary<long, long> _rentsByRequestedSize = new();
        private readonly HashSet<IntPtr> _liveAddresses = new();
        private long _rentCount;
        private int _outstandingRentals;
        private long _liveBytes;
        private long _peakLiveBytes;

        /// <summary>Initializes the decorator over a plain native allocator.</summary>
        public CountingAllocator()
            : this(new NativeAllocator())
        {
        }

        /// <summary>Initializes the decorator over the given inner allocator.</summary>
        public CountingAllocator(IBitmapMemoryAllocator inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <summary>Gets the total number of rents.</summary>
        public long RentCount
        {
            get { lock (_gate) return _rentCount; }
        }

        /// <summary>Gets the number of rentals not yet returned.</summary>
        public int OutstandingRentals
        {
            get { lock (_gate) return _outstandingRentals; }
        }

        /// <summary>Gets the byte total of the outstanding rentals.</summary>
        public long LiveBytes
        {
            get { lock (_gate) return _liveBytes; }
        }

        /// <summary>Gets the highest value <see cref="LiveBytes"/> ever reached.</summary>
        public long PeakLiveBytes
        {
            get { lock (_gate) return _peakLiveBytes; }
        }

        /// <summary>Gets a snapshot of the rent counts keyed by requested byte count.</summary>
        public IReadOnlyDictionary<long, long> RentsBySize
        {
            get { lock (_gate) return new Dictionary<long, long>(_rentsByRequestedSize); }
        }

        /// <summary>Gets a snapshot of the addresses of the outstanding rentals.</summary>
        public IReadOnlyCollection<IntPtr> LiveAddresses
        {
            get { lock (_gate) return new List<IntPtr>(_liveAddresses); }
        }

        /// <summary>Counts the rents that requested at least <paramref name="minBytes"/> bytes.</summary>
        public long FrameSizedRents(long minBytes)
        {
            lock (_gate)
            {
                var count = 0L;

                foreach (var pair in _rentsByRequestedSize)
                {
                    if (pair.Key >= minBytes)
                        count += pair.Value;
                }

                return count;
            }
        }

        /// <summary>Fails the current test when any rental is still outstanding.</summary>
        public void AssertBalanced()
        {
            int outstanding;
            long liveBytes;

            lock (_gate)
            {
                outstanding = _outstandingRentals;
                liveBytes = _liveBytes;
            }

            if (outstanding != 0)
            {
                Assert.Fail(
                    $"The allocator is not balanced: {outstanding} rental(s) holding {liveBytes} byte(s) were never returned.");
            }
        }

        public IBitmapMemory Rent(long byteCount)
        {
            if (byteCount < 0)
                throw new ArgumentOutOfRangeException(nameof(byteCount));

            var memory = _inner.Rent(byteCount);

            lock (_gate)
            {
                _rentCount++;
                _outstandingRentals++;
                _liveBytes += memory.ByteCount;

                if (_liveBytes > _peakLiveBytes)
                    _peakLiveBytes = _liveBytes;

                _rentsByRequestedSize[byteCount] =
                    _rentsByRequestedSize.TryGetValue(byteCount, out var count) ? count + 1 : 1;
                _liveAddresses.Add(memory.Address);
            }

            return new CountedMemory(this, memory);
        }

        private void Return(IBitmapMemory memory)
        {
            lock (_gate)
            {
                _outstandingRentals--;
                _liveBytes -= memory.ByteCount;
                _liveAddresses.Remove(memory.Address);
            }
        }

        private sealed class CountedMemory : IBitmapMemory
        {
            private readonly CountingAllocator _owner;
            private readonly IBitmapMemory _inner;
            private int _disposed;

            public CountedMemory(CountingAllocator owner, IBitmapMemory inner)
            {
                _owner = owner;
                _inner = inner;
            }

            public IntPtr Address => _inner.Address;

            public long ByteCount => _inner.ByteCount;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;

                _owner.Return(_inner);
                _inner.Dispose();
            }
        }

        private sealed class NativeAllocator : IBitmapMemoryAllocator
        {
            public IBitmapMemory Rent(long byteCount) => new NativeMemory(byteCount);

            private sealed class NativeMemory : IBitmapMemory
            {
                private IntPtr _address;

                public NativeMemory(long byteCount)
                {
                    _address = Marshal.AllocHGlobal(new IntPtr(byteCount));
                    ByteCount = byteCount;
                }

                public IntPtr Address => _address != IntPtr.Zero
                    ? _address
                    : throw new ObjectDisposedException(nameof(IBitmapMemory));

                public long ByteCount { get; }

                public void Dispose()
                {
                    if (_address == IntPtr.Zero)
                        return;

                    Marshal.FreeHGlobal(_address);
                    _address = IntPtr.Zero;
                }
            }
        }
    }
}
