using Avalonia.Imaging.TestKit.Instrumentation;
using Avalonia.Platform;

namespace Avalonia.Imaging.TestKit.Fixtures
{
    /// <summary>
    /// The seam between the contract tests and a concrete backend: creates the backend
    /// under test, declares its capabilities and owns the counting allocator the
    /// backend must rent its frame memory from.
    /// </summary>
    public abstract class CodecBackendFixture
    {
        protected CodecBackendFixture()
        {
            Allocator = new CountingAllocator();
        }

        /// <summary>
        /// Gets the allocator the backend under test must use for frame, staging and
        /// encode buffers, so the contract tests can assert rent counts and balance.
        /// </summary>
        public CountingAllocator Allocator { get; }

        /// <summary>
        /// Gets the capability declarations the contract tests assert against.
        /// </summary>
        public abstract BackendManifest Manifest { get; }

        /// <summary>
        /// Creates the backend under test, wired to <see cref="Allocator"/>.
        /// </summary>
        public abstract IImagingBackend CreateBackend();
    }
}
