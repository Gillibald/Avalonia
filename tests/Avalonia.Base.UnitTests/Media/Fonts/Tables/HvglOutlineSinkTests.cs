using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Fonts.Tables.Hvgl;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Tables
{
    public class HvglOutlineSinkTests
    {
        [Fact]
        public void Paths_Should_Become_Closed_Quadratic_Figures()
        {
            var recorder = new RecordingGeometryContext();
            var sink = new HvglOutlineSink();

            sink.Begin(recorder, Matrix.Identity);

            Assert.True(sink.Apply(HvglRenderInstruction.BeginPart, default));
            Assert.True(sink.Apply(HvglRenderInstruction.BeginPath, default));
            Assert.True(sink.Apply(HvglRenderInstruction.AddPoint, new double[] { 0, 0 }));
            Assert.True(sink.Apply(HvglRenderInstruction.AddQuad, new double[] { 50, 100, 100, 0 }));
            Assert.True(sink.Apply(HvglRenderInstruction.AddQuad, new double[] { 50, -20, 0, 0 }));
            Assert.True(sink.Apply(HvglRenderInstruction.ClosePath, default));
            Assert.True(sink.Apply(HvglRenderInstruction.EndPath, default));
            Assert.True(sink.Apply(HvglRenderInstruction.BeginPath, default));
            Assert.True(sink.Apply(HvglRenderInstruction.AddPoint, new double[] { 10, 10 }));
            Assert.True(sink.Apply(HvglRenderInstruction.AddLine, new double[] { 20, 10 }));
            Assert.True(sink.Apply(HvglRenderInstruction.AddCubic, new double[] { 20, 20, 10, 20, 10, 10 }));
            Assert.True(sink.Apply(HvglRenderInstruction.ClosePath, default));
            Assert.True(sink.Apply(HvglRenderInstruction.EndPart, default));

            sink.End();

            Assert.False(sink.Failed);
            Assert.Equal(new[]
            {
                "M 0,0", "Q 50,100 100,0", "Q 50,-20 0,0", "Z",
                "M 10,10", "L 20,10", "C 20,20 10,20 10,10", "Z"
            }, recorder.Commands);
        }

        [Fact]
        public void Points_Should_Be_Mapped_By_The_Transform()
        {
            var recorder = new RecordingGeometryContext();
            var sink = new HvglOutlineSink();

            // Font units (y up) to a 2x scaled, y-down target, as the rasterizer asks for.
            sink.Begin(recorder, new Matrix(2, 0, 0, -2, 5, 7));

            sink.Apply(HvglRenderInstruction.AddPoint, new double[] { 1, 1 });
            sink.Apply(HvglRenderInstruction.AddQuad, new double[] { 2, 3, 4, 0 });

            sink.End();

            Assert.Equal(new[] { "M 7,5", "Q 9,1 13,7", "Z" }, recorder.Commands);
        }

        [Fact]
        public void A_Path_Left_Open_Should_Be_Closed_When_The_Part_Ends()
        {
            var recorder = new RecordingGeometryContext();
            var sink = new HvglOutlineSink();

            sink.Begin(recorder, Matrix.Identity);
            sink.Apply(HvglRenderInstruction.AddPoint, new double[] { 0, 0 });
            sink.Apply(HvglRenderInstruction.AddQuad, new double[] { 1, 1, 2, 0 });
            sink.Apply(HvglRenderInstruction.AddPoint, new double[] { 5, 5 });
            sink.Apply(HvglRenderInstruction.AddQuad, new double[] { 6, 6, 7, 5 });
            sink.End();

            Assert.Equal(new[] { "M 0,0", "Q 1,1 2,0", "Z", "M 5,5", "Q 6,6 7,5", "Z" }, recorder.Commands);
        }

        [Fact]
        public void A_Segment_Before_The_First_Point_Should_Fail_The_Glyph()
        {
            var recorder = new RecordingGeometryContext();
            var sink = new HvglOutlineSink();

            sink.Begin(recorder, Matrix.Identity);

            Assert.False(sink.Apply(HvglRenderInstruction.AddQuad, new double[] { 1, 1, 2, 0 }));
            Assert.True(sink.Failed);
            Assert.False(sink.Apply(HvglRenderInstruction.AddPoint, new double[] { 0, 0 }));

            sink.End();

            Assert.Empty(recorder.Commands);
        }

        [Fact]
        public void A_Short_Parameter_Block_Should_Fail_The_Glyph()
        {
            var sink = new HvglOutlineSink();

            sink.Begin(new RecordingGeometryContext(), Matrix.Identity);
            sink.Apply(HvglRenderInstruction.AddPoint, new double[] { 0, 0 });

            Assert.False(sink.Apply(HvglRenderInstruction.AddQuad, new double[] { 1, 1 }));
            Assert.True(sink.Failed);
        }

        [Fact]
        public void A_Sink_Should_Start_Clean_For_The_Next_Glyph()
        {
            var sink = new HvglOutlineSink();

            sink.Begin(new RecordingGeometryContext(), Matrix.Identity);
            sink.Apply(HvglRenderInstruction.AddLine, new double[] { 1, 1 });
            sink.End();

            Assert.True(sink.Failed);

            var recorder = new RecordingGeometryContext();

            sink.Begin(recorder, Matrix.Identity);

            Assert.True(sink.Apply(HvglRenderInstruction.AddPoint, new double[] { 0, 0 }));

            sink.End();

            Assert.False(sink.Failed);
            Assert.Equal(new[] { "M 0,0", "Z" }, recorder.Commands);
        }

        [Fact]
        public void The_Renderer_Should_Be_Available_Exactly_Where_The_System_Provides_It()
        {
            var expected = OperatingSystem.IsMacOSVersionAtLeast(15, 4) || OperatingSystem.IsIOSVersionAtLeast(18, 4) ||
                           OperatingSystem.IsTvOSVersionAtLeast(18, 4) || OperatingSystem.IsMacCatalystVersionAtLeast(18, 4);

            Assert.Equal(expected, HvglTable.IsSupported);
        }

        private sealed class RecordingGeometryContext : IGeometryContext
        {
            public List<string> Commands { get; } = new();

            private static string P(Point p) => string.Create(CultureInfo.InvariantCulture, $"{p.X},{p.Y}");

            public void BeginFigure(Point startPoint, bool isFilled = true) => Commands.Add("M " + P(startPoint));

            public void LineTo(Point point, bool isStroked = true) => Commands.Add("L " + P(point));

            public void QuadraticBezierTo(Point controlPoint, Point endPoint, bool isStroked = true)
                => Commands.Add($"Q {P(controlPoint)} {P(endPoint)}");

            public void CubicBezierTo(Point controlPoint1, Point controlPoint2, Point endPoint, bool isStroked = true)
                => Commands.Add($"C {P(controlPoint1)} {P(controlPoint2)} {P(endPoint)}");

            public void ArcTo(Point point, Size size, double rotationAngle, bool isLargeArc,
                SweepDirection sweepDirection, bool isStroked = true) => Commands.Add("A " + P(point));

            public void EndFigure(bool isClosed) => Commands.Add(isClosed ? "Z" : "E");

            public void SetFillRule(FillRule fillRule)
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
