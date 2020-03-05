// Copyright (c) The Avalonia Project. All rights reserved.
// Licensed under the MIT license. See licence.md file in the project root for full license information.

using Avalonia.Media;

namespace Avalonia.Documents
{
    /// <summary>
    /// Base class for inline text elements such as <see cref="Run"/>/
    /// </summary>
    public abstract class Inline : TextElement
    {
        /// <summary>
        /// Defines the <see cref="TextDecorations"/> property.
        /// </summary>
        public static readonly AttachedProperty<TextDecorationCollection> TextDecorationsProperty =
            AvaloniaProperty.RegisterAttached<TextElement, AvaloniaObject, TextDecorationCollection>(
                nameof(TextDecorations), null, true);

        static Inline()
        {
            InvalidatesTextElement<Inline>(TextDecorationsProperty);
        }

        /// <summary>
        /// Gets or sets the decorations applied to the element.
        /// </summary>
        public TextDecorationCollection TextDecorations
        {
            get => GetValue(TextDecorationsProperty);
            set => SetValue(TextDecorationsProperty, value);
        }

        public static implicit operator Inline(string s)
        {
            return new Run(s);
        }
    }
}
