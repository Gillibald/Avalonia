// Copyright (c) The Avalonia Project. All rights reserved.
// Licensed under the MIT license. See licence.md file in the project root for full license information.

namespace Avalonia.Media.Immutable
{
    public class ImmutableTextDecoration
    {
        public ImmutableTextDecoration(TextDecorationLocation location, Pen pen, double penOffset)
        {
            Location = location;
            Pen = pen;
            PenOffset = penOffset;
        }

        public TextDecorationLocation Location { get; }

        public Pen Pen { get; }

        public double PenOffset { get; }
    }
}
