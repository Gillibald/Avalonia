using Avalonia.Controls;
using Avalonia.Media;
using Xunit;

namespace Avalonia.Markup.Xaml.UnitTests.Xaml;

public class BackdropEffectCacheTests : XamlTestBase
{
    [Theory]
    [InlineData("Retained", typeof(RetainedBackdropEffectCache))]
    [InlineData("Volatile", typeof(VolatileBackdropEffectCache))]
    public void Can_Specify_BackdropEffectCache_As_An_Attribute(string value, System.Type expected)
    {
        var xaml = $@"
<Border xmlns='https://github.com/avaloniaui' BackdropEffectCache='{value}'>
    <Border.BackdropEffect>
        <BlurEffect Radius='10'/>
    </Border.BackdropEffect>
</Border>";

        var target = AvaloniaRuntimeXamlLoader.Parse<Border>(xaml);

        Assert.IsType(expected, target.BackdropEffectCache);
    }

    [Fact]
    public void Default_Attribute_Leaves_The_Choice_To_The_Compositor()
    {
        var xaml = @"<Border xmlns='https://github.com/avaloniaui' BackdropEffectCache='Default'/>";

        var target = AvaloniaRuntimeXamlLoader.Parse<Border>(xaml);

        Assert.Null(target.BackdropEffectCache);
    }
}
