using Avalonia.Skia.Metal;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// The storage mode of the Metal updatable textures: memory the CPU writes directly, in the
    /// mode the platform offers for it.
    /// </summary>
    public class MetalUpdatableTextureFeatureTests
    {
        private const nuint Shared = 0;
        private const nuint Managed = 1;

        [Fact]
        public void Devices_With_Unified_Memory_Use_Shared_Storage()
        {
            Assert.Equal(Shared, MetalUpdatableTextureFeature.SelectStorageMode(unifiedMemory: true, managedStorageAvailable: true));
            Assert.Equal(Shared, MetalUpdatableTextureFeature.SelectStorageMode(unifiedMemory: true, managedStorageAvailable: false));
        }

        [Fact]
        public void Macs_Without_Unified_Memory_Use_Managed_Storage()
        {
            Assert.Equal(Managed, MetalUpdatableTextureFeature.SelectStorageMode(unifiedMemory: false, managedStorageAvailable: true));
        }

        [Fact]
        public void Platforms_Without_Managed_Storage_Use_Shared_Storage()
        {
            // iOS and tvOS have no managed storage; the iOS simulator's device reports no unified
            // memory, and Metal rejects a managed texture there.
            Assert.Equal(Shared, MetalUpdatableTextureFeature.SelectStorageMode(unifiedMemory: false, managedStorageAvailable: false));
        }
    }
}
