using System.Collections.Generic;
using Avalonia.Platform;
using Avalonia.Rendering.Composition.Server;
using Moq;
using Xunit;

namespace Avalonia.Base.UnitTests.Rendering;

/// <summary>
/// The working-set view is the mid-pass read the backdrop classification and
/// expansion depend on: it must reflect every add made so far without
/// finalizing, optimizing or otherwise disturbing the tracker.
/// </summary>
public class DirtyRectWorkingSetTests
{
    private static List<LtrbRect> Collect(IDirtyRectCollector collector)
    {
        var buffer = new List<LtrbRect>();
        collector.GetWorkingSet().CollectTo(buffer);
        return buffer;
    }

    private static IPlatformRenderInterface RegionPlatform()
    {
        var platform = new Mock<IPlatformRenderInterface>();
        platform.Setup(x => x.SupportsRegions).Returns(true);
        platform.Setup(x => x.CreateRegion()).Returns(() => Mock.Of<IPlatformRenderInterfaceRegion>());
        return platform.Object;
    }

    [Fact]
    public void Single_Working_Set_Is_The_Running_Union_And_Reflects_Later_Adds()
    {
        var tracker = new SingleDirtyRectTracker();
        tracker.Initialize(new LtrbRect(0, 0, 200, 200));

        tracker.AddRect(new LtrbRect(10, 10, 20, 20));
        var first = Collect(tracker);
        Assert.Equal(new LtrbRect(10, 10, 20, 20), Assert.Single(first));

        tracker.AddRect(new LtrbRect(40, 40, 50, 50));
        var second = Collect(tracker);
        Assert.Equal(new LtrbRect(10, 10, 50, 50), Assert.Single(second));
    }

    [Fact]
    public void Multi_Working_Set_Reads_Raw_Regions_And_Reflects_Later_Adds()
    {
        var tracker = new MultiDirtyRectTracker(RegionPlatform(), maxDirtyRects: 8, maxOverhead: 1000);
        tracker.Initialize(new LtrbRect(0, 0, 200, 200));

        tracker.AddRect(new LtrbRect(10, 10, 20, 20));
        tracker.AddRect(new LtrbRect(100, 100, 120, 120));
        Assert.Equal(2, Collect(tracker).Count);

        // Reads must not set the optimized flag: a later add has to remain
        // visible to the next read.
        tracker.AddRect(new LtrbRect(150, 10, 160, 20));
        Assert.Equal(3, Collect(tracker).Count);
    }

    [Fact]
    public void Region_Working_Set_Reads_The_Raw_List()
    {
        var tracker = new RegionDirtyRectTracker(RegionPlatform());
        tracker.Initialize(new LtrbRect(0, 0, 200, 200));

        tracker.AddRect(new LtrbRect(10, 10, 20, 20));
        tracker.AddRect(new LtrbRect(30, 30, 40, 40));

        Assert.Equal(2, Collect(tracker).Count);

        tracker.Initialize(new LtrbRect(0, 0, 200, 200));
        Assert.Empty(Collect(tracker));
    }

    [Fact]
    public void Space_Mapping_Round_Trips_And_Matches_The_Offset_Scale_Form()
    {
        var mapping = new DirtyRectSpaceMapping(new Vector(5, -3), 2, 0.5);
        var host = new LtrbRect(10, 20, 30, 40);

        var tracker = mapping.HostToTracker(host);
        Assert.Equal(new LtrbRect((10 + 5) * 2, (20 - 3) * 0.5, (30 + 5) * 2, (40 - 3) * 0.5), tracker);
        Assert.Equal(host, mapping.TrackerToHost(tracker));
    }

    [Fact]
    public void Zero_Scale_Mapping_Is_Unusable()
    {
        var view = new DirtyRectWorkingSet(new SingleDirtyRectTracker(), new DirtyRectSpaceMapping(default, 0, 0));
        Assert.False(view.IsUsable);

        var buffer = new List<LtrbRect>();
        view.CollectTo(buffer);
        Assert.Empty(buffer);
    }
}
