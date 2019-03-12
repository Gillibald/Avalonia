// Copyright (c) The Avalonia Project. All rights reserved.
// Licensed under the MIT license. See licence.md file in the project root for full license information.

using Avalonia.Media.Immutable;

namespace Avalonia.Media
{  
    public class TextDecoration : AvaloniaObject
    {
        public static StyledProperty<TextDecorationLocation> LocationProperty =
            AvaloniaProperty.Register<TextDecoration, TextDecorationLocation>(nameof(Location));

        public static StyledProperty<Pen> PenProperty =
            AvaloniaProperty.Register<TextDecoration, Pen>(nameof(Pen));

        public static StyledProperty<double> PenOffsetProperty =
            AvaloniaProperty.Register<TextDecoration, double>(nameof(PenOffset));

        /// <summary>
        /// Gets or sets the location.
        /// </summary>
        /// <value>
        /// The location.
        /// </value>
        public TextDecorationLocation Location
        {
            get => GetValue(LocationProperty);
            set => SetValue(LocationProperty, value);
        }

        /// <summary>
        /// Gets or sets the pen.
        /// </summary>
        /// <value>
        /// The pen.
        /// </value>
        public Pen Pen
        {
            get => GetValue(PenProperty);
            set => SetValue(PenProperty, value);
        }

        /// <summary>
        /// Gets or sets the pen offset.
        /// </summary>
        /// <value>
        /// The pen offset.
        /// </value>
        public double PenOffset
        {
            get => GetValue(PenOffsetProperty);
            set => SetValue(PenOffsetProperty, value);
        }

        public ImmutableTextDecoration ToImmutable()
        {
            return new ImmutableTextDecoration(Location, Pen.ToImmutable(), PenOffset);
        }
    }
}
