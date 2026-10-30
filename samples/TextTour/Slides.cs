namespace TextTour
{
    /// <summary>A slide of the talk: its section number as the deck prints it, and its title.</summary>
    internal sealed record Slide(string Number, string Title);

    /// <summary>
    /// Which slide of the talk each chapter step belongs to. The deck numbers slides by section
    /// (4.3 is the third slide of section 4), so the numbers stay valid when pages are added.
    /// </summary>
    internal static class Slides
    {
        private sealed record Entry(string Chapter, int FirstStep, int LastStep, bool Card, Slide Slide);

        private static readonly Slide s_oneString = new("1.2", "One string that touches every stage");
        private static readonly Slide s_units = new("2.1", "Four units, and none of them maps 1:1");
        private static readonly Slide s_properties = new("2.2", "Every code point carries properties");
        private static readonly Slide s_query = new("3.1", "A family name is a query, not a file");
        private static readonly Slide s_coverage = new("3.2", "The matched face does not cover every character");
        private static readonly Slide s_source = new("4.1", "The text source hands out runs");
        private static readonly Slide s_breaks = new("4.2", "Break opportunities do not come from spaces");
        private static readonly Slide s_bidi = new("4.3", "BiDi gives every character an embedding level");
        private static readonly Slide s_items = new("4.4", "Itemization cuts wherever anything changes");
        private static readonly Slide s_shaping = new("4.5", "Shaping turns an item into positioned glyphs");
        private static readonly Slide s_lineBreaks = new("5.1", "Lines break at the last opportunity that fits");
        private static readonly Slide s_reorder = new("5.2", "Lines break in logical order, then reorder per line");
        private static readonly Slide s_metrics = new("5.3", "Line height comes from three sets of metrics");
        private static readonly Slide s_hits = new("6.1", "Hit testing maps points to indices and back");
        private static readonly Slide s_carets = new("6.2", "Carets live inside glyphs and in two places");

        // Card entries apply while the chapter shows its detail card and win over the plain ones.
        private static readonly Entry[] s_entries =
        {
            new("Start", 1, 2, false, s_oneString),
            new("Units", 1, 5, false, s_units),
            new("Units", 2, 5, true, s_properties),
            new("Fonts", 1, 4, false, s_query),
            new("Fonts", 5, 5, false, s_coverage),
            new("Tokens", 1, 1, false, s_source),
            new("Tokens", 2, 4, false, s_breaks),
            new("Tokens", 5, 5, false, s_bidi),
            new("Tokens", 6, 6, false, s_items),
            new("Tokens", 7, 7, false, s_shaping),
            new("Layout", 1, 2, false, s_lineBreaks),
            new("Layout", 3, 3, false, s_reorder),
            new("Layout", 4, 5, false, s_metrics),
            new("Layout", 6, 6, false, s_lineBreaks),
            new("Hits", 1, 2, false, s_hits),
            new("Hits", 3, 5, false, s_carets),
        };

        /// <summary>The slide a chapter step belongs to, or null for pages outside the talk.</summary>
        public static Slide? For(string chapter, int step, bool card)
        {
            Slide? plain = null;

            foreach (var entry in s_entries)
            {
                if (entry.Chapter != chapter || step < entry.FirstStep || step > entry.LastStep)
                {
                    continue;
                }

                if (entry.Card == card)
                {
                    return entry.Slide;
                }

                if (!entry.Card)
                {
                    plain = entry.Slide;
                }
            }

            return plain;
        }
    }
}
