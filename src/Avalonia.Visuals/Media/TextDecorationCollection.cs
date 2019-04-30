// Copyright (c) The Avalonia Project. All rights reserved.
// Licensed under the MIT license. See licence.md file in the project root for full license information.

using System;
using System.Collections.Generic;

using Avalonia.Collections;
using Avalonia.Media.Immutable;
using Avalonia.Utilities;

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

        public static TextDecorationCollection Parse(string s)
        {
            var locations = new List<TextDecorationLocation>();

            using (var tokenizer = new StringTokenizer(s, ',', "Invalid text decoration."))
            {
                while (tokenizer.TryReadString(out var name))
                {
                    var location = GetTextDecorationLocation(name);

                    if (locations.Contains(location))
                    {
                        throw new ArgumentException("Text decoration already specified.", nameof(s));
                    }

                    locations.Add(location);
                }
            }

            var textDecorations = new TextDecorationCollection();

            foreach (var textDecorationLocation in locations)
            {
                textDecorations.Add(new TextDecoration { Location = textDecorationLocation });
            }

            return textDecorations;
        }

        private static TextDecorationLocation GetTextDecorationLocation(string s)
        {
            if (Enum.TryParse<TextDecorationLocation>(s, out var location))
            {
                return location;
            }

            throw new ArgumentException("Could not parse text decoration.", nameof(s));
        }
    }
}
