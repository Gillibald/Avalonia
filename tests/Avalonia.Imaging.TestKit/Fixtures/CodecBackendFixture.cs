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

        /// <summary>
        /// Creates an encoded fixture tagged with EXIF orientation 6, or null when the
        /// backend project supplies none; the orientation contract tests skip then.
        /// </summary>
        public virtual OrientedImageFixture? CreateOrientedFixture() => null;

        /// <summary>
        /// Creates an encoded multi-frame fixture with distinguishable frames, or null
        /// when the backend project supplies none; the frame cursor contract tests
        /// skip then.
        /// </summary>
        public virtual MultiFrameImageFixture? CreateMultiFrameFixture() => null;
    }
}
