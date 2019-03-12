// Copyright (c) The Avalonia Project. All rights reserved.
// Licensed under the MIT license. See licence.md file in the project root for full license information.

using System.Collections.Generic;

using Avalonia.Collections;
using Avalonia.Media.Immutable;

namespace Avalonia.Media
{
    public class TextDecorationCollection : AvaloniaList<TextDecoration>
    {
        public IReadOnlyList<ImmutableTextDecoration> ToImmutable()
        {
            var immutable = new ImmutableTextDecoration[Count];

            for (var i = 0; i < Count; i++)
            {
                immutable[i] = this[i].ToImmutable();
            }

            return immutable;
        }
    }
}
