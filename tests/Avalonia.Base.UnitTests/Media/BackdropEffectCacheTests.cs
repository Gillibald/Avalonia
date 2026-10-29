using System;
using Avalonia.Media;
using Xunit;

namespace Avalonia.Base.UnitTests.Media;

public class BackdropEffectCacheTests
{
    [Fact]
    public void Parse_Parses_Retained()
        => Assert.IsType<RetainedBackdropEffectCache>(BackdropEffectCache.Parse("Retained"));

    [Fact]
    public void Parse_Parses_Volatile()
        => Assert.IsType<VolatileBackdropEffectCache>(BackdropEffectCache.Parse("Volatile"));

    [Fact]
    public void Parse_Default_Returns_Null()
        => Assert.Null(BackdropEffectCache.Parse("Default"));

    [Fact]
    public void Invalid_Parse_Fails()
        => Assert.Throws<ArgumentException>(() => BackdropEffectCache.Parse("Nope"));
}
