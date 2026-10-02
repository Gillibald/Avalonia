using System;
using System.Collections.Generic;
using System.Text;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Skia;

namespace TextStress.Measurement
{
    /// <summary>
    /// The render thread's cumulative text counters: why glyph batches were drawn, how many runs
    /// they held, atlas page images and their bytes, glyph rasterizations and cache lookups.
    /// The runner reads them at both ends of a render pass and writes the differences.
    /// </summary>
    internal static class RenderCounters
    {
        private static readonly GlyphBatchFlushReason[] s_reasons = Enum.GetValues<GlyphBatchFlushReason>();

        /// <summary>Column names, in the order <see cref="Read"/> fills its array.</summary>
        public static readonly string[] Columns = BuildColumns();

        public static int Count => Columns.Length;

        private static string[] BuildColumns()
        {
            var columns = new List<string>();

            foreach (var reason in s_reasons)
            {
                columns.Add("fb_" + Snake(reason.ToString()));
            }

            foreach (var reason in s_reasons)
            {
                columns.Add("fe_" + Snake(reason.ToString()));
            }

            columns.AddRange(new[]
            {
                "batched_runs", "batches_drawn", "page_images_replaced", "page_upload_bytes",
                "glyph_rasterizations", "mask_hits", "mask_misses", "sprite_set_builds", "atlas_batch_builds",
                "atlas_hits", "atlas_misses", "atlas_placements"
            });

            return columns.ToArray();
        }

        /// <summary>Reads every counter of the calling thread into <paramref name="values"/>.</summary>
        public static void Read(long[] values)
        {
            var i = 0;

            foreach (var reason in s_reasons)
            {
                values[i++] = DrawingContextImpl.GetBatchesFlushedOnThread(reason);
            }

            foreach (var reason in s_reasons)
            {
                values[i++] = DrawingContextImpl.GetFlushesOnThread(reason);
            }

            values[i++] = DrawingContextImpl.BatchedRunsOnThread;
            values[i++] = DrawingContextImpl.BatchesDrawnOnThread;
            values[i++] = DrawingContextImpl.PageImagesReplacedOnThread;
            values[i++] = DrawingContextImpl.PageImageBytesOnThread;
            values[i++] = GlyphRasterDiagnostics.GlyphRasterizationsOnThread;
            values[i++] = GlyphRasterDiagnostics.MaskCacheHitsOnThread;
            values[i++] = GlyphRasterDiagnostics.MaskCacheMissesOnThread;
            values[i++] = GlyphRasterDiagnostics.SpriteSetBuildsOnThread;
            values[i++] = GlyphRasterDiagnostics.AtlasBatchBuildsOnThread;
            values[i++] = GlyphRasterDiagnostics.AtlasHitsOnThread;
            values[i++] = GlyphRasterDiagnostics.AtlasMissesOnThread;
            values[i] = GlyphRasterDiagnostics.AtlasPlacementsOnThread;
        }

        private static string Snake(string name)
        {
            var builder = new StringBuilder(name.Length + 4);

            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];

                if (char.IsUpper(c) && i > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }

            return builder.ToString();
        }
    }
}
