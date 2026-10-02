using System;
using System.Runtime.InteropServices;
using Avalonia.Media;
using Xunit;

namespace Avalonia.Base.UnitTests.Media
{
    public class TextRasterizationDefaultsTests
    {
        [Fact]
        public void Windows_X64_Defaults_To_Managed()
        {
            Assert.Equal(TextRasterizationMode.Managed,
                TextRasterizationDefaults.Resolve(null, TextRasterizationPlatform.Windows, Architecture.X64));
        }

        [Fact]
        public void Windows_Arm64_Defaults_To_Backend()
        {
            Assert.Equal(TextRasterizationMode.Backend,
                TextRasterizationDefaults.Resolve(null, TextRasterizationPlatform.Windows, Architecture.Arm64));
        }

        [Theory]
        [InlineData(nameof(TextRasterizationPlatform.Linux), Architecture.X64)]
        [InlineData(nameof(TextRasterizationPlatform.Linux), Architecture.Arm64)]
        [InlineData(nameof(TextRasterizationPlatform.MacOS), Architecture.Arm64)]
        [InlineData(nameof(TextRasterizationPlatform.MacOS), Architecture.X64)]
        [InlineData(nameof(TextRasterizationPlatform.Android), Architecture.Arm64)]
        [InlineData(nameof(TextRasterizationPlatform.IOS), Architecture.Arm64)]
        [InlineData(nameof(TextRasterizationPlatform.Windows), Architecture.X86)]
        [InlineData(nameof(TextRasterizationPlatform.Other), Architecture.X64)]
        public void Untested_Platforms_Default_To_Backend(string platform, Architecture architecture)
        {
            // The platform travels by name: the enum is internal, and a public test method
            // cannot take it as a parameter.
            Assert.Equal(TextRasterizationMode.Backend,
                TextRasterizationDefaults.Resolve(null, Enum.Parse<TextRasterizationPlatform>(platform),
                    architecture));
        }

        [Fact]
        public void Browser_Wasm_Defaults_To_Managed()
        {
            Assert.Equal(TextRasterizationMode.Managed,
                TextRasterizationDefaults.Resolve(null, TextRasterizationPlatform.Browser, Architecture.Wasm));
        }

        [Fact]
        public void Explicit_Backend_On_Windows_X64_Is_Honoured()
        {
            Assert.Equal(TextRasterizationMode.Backend,
                TextRasterizationDefaults.Resolve(TextRasterizationMode.Backend,
                    TextRasterizationPlatform.Windows, Architecture.X64));
        }

        [Fact]
        public void Explicit_Managed_On_Linux_Is_Honoured()
        {
            Assert.Equal(TextRasterizationMode.Managed,
                TextRasterizationDefaults.Resolve(TextRasterizationMode.Managed,
                    TextRasterizationPlatform.Linux, Architecture.X64));
        }

        [Fact]
        public void Unset_Options_Report_The_Platform_Default()
        {
            Assert.Equal(TextRasterizationDefaults.PlatformDefault, new FontManagerOptions().TextRasterizationMode);
        }

        [Theory]
        [InlineData(TextRasterizationMode.Managed)]
        [InlineData(TextRasterizationMode.Backend)]
        public void Explicit_Option_Wins_Over_The_Platform_Default(TextRasterizationMode mode)
        {
            var options = new FontManagerOptions { TextRasterizationMode = mode };

            Assert.Equal(mode, options.TextRasterizationMode);
        }
    }
}
