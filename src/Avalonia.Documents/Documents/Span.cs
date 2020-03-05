using Avalonia.Metadata;

namespace Avalonia.Documents
{
    public class Span : Inline
    {
        /// <summary>
        /// Defines the <see cref="Inlines"/> property.
        /// </summary>
        public static readonly StyledProperty<InlineCollection> InlinesProperty =
            AvaloniaProperty.Register<Span, InlineCollection>(nameof(Inlines), new InlineCollection(), true);

        [Content]
        public InlineCollection Inlines
        {
            get => GetValue(InlinesProperty);
            set => SetValue(InlinesProperty, value);
        }
    }

    public class Bold : Span
    {

    }

    public class Italic : Span
    {

    }

    public class Underline : Span
    {

    }

    public class Hyperlink : Span
    {

    }
}
