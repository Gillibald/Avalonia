#nullable enable

using System;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Skia.RenderTests.CrossUI;
using CrossUI;
using Xunit;

namespace Avalonia.Skia.RenderTests;

class CrossFactAttribute : FactAttribute
{
    public CrossFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1
    ) : base(sourceFilePath, sourceLineNumber)
    {
    }
}

class CrossTheoryAttribute : TheoryAttribute
{
    public CrossTheoryAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1
    ) : base(sourceFilePath, sourceLineNumber)
    {
    }
}

public class CrossTestBase : IDisposable, IClassFixture<RenderTestFonts>
{
    private readonly string _groupName;
    private readonly IDisposable _application;

    public CrossTestBase(string groupName)
    {
        _application = TestRenderHelper.BeginTest();
        _groupName = groupName;
    }

    protected void RenderAndCompare(CrossControl root, [CallerMemberName] string? testName = null, double dpi = 96)
    {
        ArgumentException.ThrowIfNullOrEmpty(testName, nameof(testName));

        var dir = Path.Combine(TestRenderHelper.GetTestsDirectory(), "TestFiles", "CrossTests", _groupName);
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        var flavor =
#if AVALONIA_SKIA
            "skia";
#else
            "d2d";
#endif
        var pathBase = Path.Combine(dir, testName);
        var renderPath = pathBase + "." + flavor + ".out.png";
        var compareWith = pathBase + ".wpf.png";
        var control = new AvaloniaCrossControl(root);
        TestRenderHelper.RenderToFile(control, renderPath, false, dpi);

        TestRenderHelper.AssertCompareImages(renderPath, compareWith);
    }

    public void Dispose()
    {
        _application.Dispose();
    }
}
