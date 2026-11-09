using System;
using System.Runtime.InteropServices;
using Avalonia.Platform.Internal;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Imaging
{
    public class BitmapMemoryPoolTests
    {
        [Theory]
        [InlineData(1, 4096)]
        [InlineData(4096, 4096)]
        [InlineData(4097, 8192)]
        [InlineData(100_000, 131_072)]
        [InlineData(131_072, 131_072)]
        public void RoundUpToBucket_Rounds_To_Power_Of_Two(long requested, long expectedBucket)
        {
            Assert.Equal(expectedBucket, BitmapMemoryPool.RoundUpToBucket(requested));
        }

        [Fact]
        public void Rent_Returns_Buffer_Of_At_Least_Requested_Size()
        {
            var pool = new BitmapMemoryPool(1024 * 1024);

            using var memory = pool.Rent(10_000);

            Assert.NotEqual(IntPtr.Zero, memory.Address);
            Assert.Equal(10_000, memory.ByteCount);
        }

        [Fact]
        public void Rent_Reuses_Returned_Buffer_Of_Same_Bucket()
        {
            var pool = new BitmapMemoryPool(1024 * 1024);

            IntPtr first;

            using (var memory = pool.Rent(10_000))
            {
                first = memory.Address;
            }

            Assert.Equal(BitmapMemoryPool.RoundUpToBucket(10_000), pool.PooledBytes);

            using (var memory = pool.Rent(12_000))
            {
                Assert.Equal(first, memory.Address);
            }
        }

        [Fact]
        public void Return_Beyond_Ceiling_Frees_Instead_Of_Pooling()
        {
            var pool = new BitmapMemoryPool(16_384);

            var first = pool.Rent(16_384);
            var second = pool.Rent(16_384);

            first.Dispose();
            second.Dispose();

            Assert.Equal(16_384, pool.PooledBytes);
        }

        [Fact]
        public void Return_Trims_Least_Recently_Used_Other_Size_Class_First()
        {
            var pool = new BitmapMemoryPool(32_768);

            // Pool a small bucket first (older last-use).
            pool.Rent(4096).Dispose();

            Assert.Equal(4096, pool.PooledBytes);

            // Returning a bucket that would exceed the ceiling trims the small class.
            pool.Rent(32_768).Dispose();

            Assert.Equal(32_768, pool.PooledBytes);
        }

        [Fact]
        public void Trim_Frees_All_Idle_Buffers()
        {
            var pool = new BitmapMemoryPool(1024 * 1024);

            pool.Rent(4096).Dispose();
            pool.Rent(65_536).Dispose();

            Assert.NotEqual(0, pool.PooledBytes);

            pool.Trim();

            Assert.Equal(0, pool.PooledBytes);
        }

        [Fact]
        public void Double_Dispose_Returns_Only_Once()
        {
            var pool = new BitmapMemoryPool(1024 * 1024);

            var memory = pool.Rent(4096);

            memory.Dispose();
            memory.Dispose();

            Assert.Equal(4096, pool.PooledBytes);
        }

        [Fact]
        public void Address_After_Dispose_Throws()
        {
            var pool = new BitmapMemoryPool(1024 * 1024);

            var memory = pool.Rent(4096);

            memory.Dispose();

            Assert.Throws<ObjectDisposedException>(() => memory.Address);
        }

        [Fact]
        public void Rent_Rejects_Non_Positive_Sizes()
        {
            var pool = new BitmapMemoryPool(1024 * 1024);

            Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(-1));
        }

        [Fact]
        public void Zero_Ceiling_Pools_Nothing()
        {
            var pool = new BitmapMemoryPool(0);

            pool.Rent(4096).Dispose();

            Assert.Equal(0, pool.PooledBytes);
        }

        [Fact]
        public void Fresh_Rent_Is_Zeroed()
        {
            // A zero ceiling frees every returned buffer, so the native heap is free to hand
            // the dirtied block straight back to the next rent of the same size.
            var pool = new BitmapMemoryPool(0);
            var dirty = new byte[4096];

            Array.Fill(dirty, (byte)0xCD);

            for (var i = 0; i < 16; i++)
            {
                using (var used = pool.Rent(4096))
                    Marshal.Copy(dirty, 0, used.Address, dirty.Length);

                using var fresh = pool.Rent(4096);
                var bytes = new byte[4096];

                Marshal.Copy(fresh.Address, bytes, 0, bytes.Length);

                Assert.All(bytes, b => Assert.Equal(0, b));
            }
        }
    }
}
