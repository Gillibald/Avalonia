using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Imaging
{
    public class ImagingBackendRegistrationTests
    {
        private sealed class FakeBackend : IImagingBackend
        {
            public FakeBackend(string name) => Name = name;

            public string Name { get; }

            public IReadOnlyList<IBitmapCodecInfo> SupportedCodecs { get; } = Array.Empty<IBitmapCodecInfo>();

            public int IdentifyPrefixLength => 4096;

            public bool TryIdentify(Stream stream, out BitmapImageInfo info)
            {
                info = default;
                return false;
            }

            public bool TryIdentify(ReadOnlySpan<byte> data, out BitmapImageInfo info)
            {
                info = default;
                return false;
            }

            public IBitmapDecoder CreateDecoder(Stream stream, bool ownsStream, BitmapDecodeOptions? options = null)
                => throw new NotSupportedException();

            public IBitmapEncoderImpl CreateEncoder(Guid containerFormat)
                => throw new NotSupportedException();
        }

        [Fact]
        public void Current_Throws_When_No_Backend_Is_Configured()
        {
            using var scope = AvaloniaLocator.EnterScope();

            Assert.Throws<InvalidOperationException>(() => ImagingBackend.Current);
            Assert.Null(ImagingBackend.CurrentOrNull);
        }

        [Fact]
        public void Default_Registration_Binds_When_Nothing_Is_Bound()
        {
            using var scope = AvaloniaLocator.EnterScope();

            var backend = new FakeBackend("Default");

            ImagingBackend.Register(backend, isDefault: true);

            Assert.Same(backend, ImagingBackend.Current);
        }

        [Fact]
        public void Default_Registration_Does_Not_Replace_An_Existing_Binding()
        {
            using var scope = AvaloniaLocator.EnterScope();

            var explicitBackend = new FakeBackend("Explicit");
            var defaultBackend = new FakeBackend("Default");

            ImagingBackend.Register(explicitBackend);
            ImagingBackend.Register(defaultBackend, isDefault: true);

            Assert.Same(explicitBackend, ImagingBackend.Current);
        }

        [Fact]
        public void Explicit_Registration_Overrides_The_Default()
        {
            using var scope = AvaloniaLocator.EnterScope();

            var defaultBackend = new FakeBackend("Default");
            var explicitBackend = new FakeBackend("Explicit");

            ImagingBackend.Register(defaultBackend, isDefault: true);
            ImagingBackend.Register(explicitBackend);

            Assert.Same(explicitBackend, ImagingBackend.Current);
        }

        [Fact]
        public void Two_Different_Explicit_Backends_Throw()
        {
            using var scope = AvaloniaLocator.EnterScope();

            ImagingBackend.Register(new FakeBackend("First"));

            var exception = Assert.Throws<InvalidOperationException>(
                () => ImagingBackend.Register(new FakeBackend("Second")));

            Assert.Contains("First", exception.Message);
            Assert.Contains("Second", exception.Message);
        }

        [Fact]
        public void Re_Registering_The_Same_Backend_Name_Is_Allowed()
        {
            using var scope = AvaloniaLocator.EnterScope();

            ImagingBackend.Register(new FakeBackend("Same"));

            var replacement = new FakeBackend("Same");

            ImagingBackend.Register(replacement);

            Assert.Same(replacement, ImagingBackend.Current);
        }
    }
}
