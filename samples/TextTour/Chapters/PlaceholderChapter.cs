using Avalonia;
using Avalonia.Media;

namespace TextTour.Chapters
{
    /// <summary>Stands in for a chapter that is not built yet, so the rail and the keys stay complete.</summary>
    internal sealed class PlaceholderChapter : Chapter
    {
        public PlaceholderChapter(TourState state, string title) : base(state)
        {
            Title = title;
        }

        public override string Title { get; }

        public override int StepCount => 1;

        public override string Caption => string.Empty;

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            Draw.Text(context, Title, Bounds.Width / 2, Bounds.Height / 2 - 30, 40, Palette.Muted, HAlign.Center);
        }
    }
}
