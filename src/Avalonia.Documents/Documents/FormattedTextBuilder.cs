// Copyright (c) The Avalonia Project. All rights reserved.
// Licensed under the MIT license. See licence.md file in the project root for full license information.

using System.Collections.Generic;
using System.Text;
using Avalonia.Media;

namespace Avalonia.Documents
{
    public class FormattedTextBuilder
    {
        private readonly StringBuilder _builder = new StringBuilder();
        private readonly List<FormattedTextStyleSpan> _spans = new List<FormattedTextStyleSpan>();

        public int StartIndex => _builder.Length;

        public void Add(string text, FormattedTextStyleSpan style)
        {
            _builder.Append(text);

            if (style != null)
            {
                _spans.Add(style);
            }
        }

        public FormattedText ToFormattedText()
        {
            return new FormattedText
            {
                Spans = _spans,
                Text = _builder.ToString(),
            };
        }
    }
}
