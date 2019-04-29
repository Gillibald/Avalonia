// Copyright (c) The Avalonia Project. All rights reserved.
// Licensed under the MIT license. See licence.md file in the project root for full license information.

namespace Avalonia.Media.Immutable
{
    public class ImmutableTextDecoration
    {
        public ImmutableTextDecoration(TextDecorationLocation location, Pen pen, TextDecorationUnit penThicknessUnit,
            double penOffset, TextDecorationUnit penOffsetUnit)
        {
            Location = location;
            Pen = pen;
            PenThicknessUnit = penThicknessUnit;
            PenOffset = penOffset;
            PenOffsetUnit = penOffsetUnit;
        }

        public TextDecorationLocation Location { get; }

        public Pen Pen { get; }

        public TextDecorationUnit PenThicknessUnit { get; }

        public double PenOffset { get; }

        public TextDecorationUnit PenOffsetUnit { get; }
    }
}
