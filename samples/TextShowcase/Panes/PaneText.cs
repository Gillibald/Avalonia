using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TextShowcase.Panes
{
    /// <summary>
    /// A <see cref="TextBlock"/> whose glyph runs are created under its pane's rasterization mode
    /// (see <see cref="PaneMode"/>).
    /// </summary>
    internal class PaneText : TextBlock
    {
        protected override Type StyleKeyOverride => typeof(TextBlock);

        protected override Size MeasureOverride(Size availableSize)
        {
            using var scope = PaneMode.Scope(this);

            return base.MeasureOverride(availableSize);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            using var scope = PaneMode.Scope(this);

            return base.ArrangeOverride(finalSize);
        }

        // TextBlock.Render is sealed; the layout it draws was built during the scoped measure,
        // and the draw is where its glyph runs get their implementation.
        protected override void RenderTextLayout(DrawingContext context, Point origin)
        {
            using var scope = PaneMode.Scope(this);

            base.RenderTextLayout(context, origin);
        }
    }

    /// <summary>
    /// A control that draws its text itself, under its pane's rasterization mode.
    /// </summary>
    internal sealed class PaneTextCanvas : Control
    {
        private readonly Action<DrawingContext, Size> _draw;

        public PaneTextCanvas(Action<DrawingContext, Size> draw)
        {
            _draw = draw;
        }

        public override void Render(DrawingContext context)
        {
            using var scope = PaneMode.Scope(this);

            _draw(context, Bounds.Size);
        }
    }
}
