using Avalonia.Animation.Animators;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Xunit;

namespace Avalonia.Base.UnitTests.Media
{
    public class RadialGradientBrushTests
    {
        [Fact]
        public void ToImmutable_Preserves_All_Properties()
        {
            var source = new RadialGradientBrush
            {
                GradientStops = { new GradientStop(Colors.Red, 0), new GradientStop(Colors.Blue, 1) },
                Center = new RelativePoint(0.3, 0.4, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.1, 0.2, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.6, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.25, RelativeUnit.Relative),
                FocalRadius = new RelativeScalar(0.15, RelativeUnit.Relative),
            };

            var immutable = (ImmutableRadialGradientBrush)source.ToImmutable();

            Assert.Equal(source.Center, immutable.Center);
            Assert.Equal(source.GradientOrigin, immutable.GradientOrigin);
            Assert.Equal(source.RadiusX, immutable.RadiusX);
            Assert.Equal(source.RadiusY, immutable.RadiusY);
            Assert.Equal(source.FocalRadius, immutable.FocalRadius);
        }

        [Fact]
        public void FocalRadius_Defaults_To_Zero()
        {
            Assert.Equal(RelativeScalar.Beginning, new RadialGradientBrush().FocalRadius);
            Assert.Equal(RelativeScalar.Beginning, new ImmutableRadialGradientBrush(
                new[] { new ImmutableGradientStop(0, Colors.Red) }, radius: 0.5).FocalRadius);
        }

        [Fact]
        public void Changing_FocalRadius_Raises_Invalidated()
        {
            var target = new RadialGradientBrush();

            RenderResourceTestHelper.AssertResourceInvalidation(target, () =>
            {
                target.FocalRadius = new RelativeScalar(0.2, RelativeUnit.Relative);
            });
        }

        [Fact]
        public void GradientBrushAnimator_Interpolates_FocalRadius()
        {
            var from = new ImmutableRadialGradientBrush(
                new[] { new ImmutableGradientStop(0, Colors.Red), new ImmutableGradientStop(1, Colors.Blue) },
                focalRadius: new RelativeScalar(0.1, RelativeUnit.Relative),
                opacity: 1, transform: null, transformOrigin: null,
                spreadMethod: GradientSpreadMethod.Pad, center: null, gradientOrigin: null,
                radiusX: null, radiusY: null);
            var to = new ImmutableRadialGradientBrush(
                new[] { new ImmutableGradientStop(0, Colors.Red), new ImmutableGradientStop(1, Colors.Blue) },
                focalRadius: new RelativeScalar(0.3, RelativeUnit.Relative),
                opacity: 1, transform: null, transformOrigin: null,
                spreadMethod: GradientSpreadMethod.Pad, center: null, gradientOrigin: null,
                radiusX: null, radiusY: null);

            var animator = new GradientBrushAnimator();
            var result = (IRadialGradientBrush)animator.Interpolate(0.5, from, to)!;

            Assert.Equal(0.2, result.FocalRadius.Scalar, 10);
            Assert.Equal(RelativeUnit.Relative, result.FocalRadius.Unit);
        }
    }
}
