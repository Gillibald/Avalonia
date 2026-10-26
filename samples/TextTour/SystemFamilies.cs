using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;

namespace TextTour
{
    /// <summary>Picks named families for demo moves from the families the system lists.</summary>
    internal static class SystemFamilies
    {
        /// <summary>
        /// The first of <paramref name="families"/> the system font collection lists. Only the
        /// names are compared: loading a face here would change what later fallback lookups
        /// answer, because the fallback prefers faces that are already loaded.
        /// </summary>
        public static string? FirstOf(params string[] families) => All(families).FirstOrDefault();

        /// <summary>The listed families among <paramref name="families"/>, in the given order.</summary>
        public static IEnumerable<string> All(params string[] families)
        {
            var installed = new HashSet<string>(FontManager.Current.SystemFonts.Select(f => f.Name),
                StringComparer.OrdinalIgnoreCase);

            return families.Where(installed.Contains);
        }
    }
}
