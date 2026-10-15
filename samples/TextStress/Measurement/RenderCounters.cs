using System;
using System.Collections.Generic;
using System.Text;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Skia;

namespace TextStress.Measurement
{
    /// <summary>
    /// The render thread's cumulative text counters: why glyph batches were drawn, how many runs
    /// they held, atlas page images and their bytes, glyph rasterizations and cache lookups, and
    /// the clips batched runs were drawn under, the draws that stretched an earlier raster
    /// (or replayed colour glyphs as vectors) while a run's transform kept changing, and the colour
    /// glyphs replayed from their drawings, vectors or strike images, rather than drawn from masks.
    /// The runner reads them at both ends of a render pass and writes the differences.
    /// </summary>
    internal static class RenderCounters
    {
        private static readonly GlyphBatchFlushReason[] s_reasons = Enum.GetValues<GlyphBatchFlushReason>();
        private static readonly GlyphRunClipKind[] s_clipKinds = Enum.GetValues<GlyphRunClipKind>();

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
                "batched_runs", "batches_drawn", "page_images_replaced", "page_upload_bytes", "page_texture_updates", "page_texture_regions",
                "glyph_rasterizations", "mask_hits", "mask_misses", "sprite_set_builds", "atlas_batch_builds",
                "atlas_hits", "atlas_misses", "atlas_placements", "unsettled_draws", "stretched_draws", "color_replays"
            });

            foreach (var kind in s_clipKinds)
            {
                columns.Add("clip_" + Snake(kind.ToString()) + "_in");
                columns.Add("clip_" + Snake(kind.ToString()) + "_cross");
            }

            columns.Add("clip_all_in");

            return columns.ToArray();
        }

        /// <summary>Reads every counter of the calling thread into <paramref name="values"/>.</summary>
        public static void Read(long[] values)
        {
            var raster = GlyphRasterServices.Current.ForCurrentThread();
            var counters = raster.Counters;
            var skia = SkiaTextCounters.Of(raster);
            var i = 0;

            foreach (var reason in s_reasons)
            {
                values[i++] = skia.GetBatchesFlushed(reason);
            }

            foreach (var reason in s_reasons)
            {
                values[i++] = skia.GetFlushes(reason);
            }

            values[i++] = skia.BatchedRuns;
            values[i++] = skia.BatchesDrawn;
            values[i++] = skia.PageImagesReplaced;
            values[i++] = skia.PageImageBytes;
            values[i++] = skia.PageTextureUpdates;
            values[i++] = skia.PageTextureRegions;
            values[i++] = counters.GlyphRasterizations;
            values[i++] = counters.MaskCacheHits;
            values[i++] = counters.MaskCacheMisses;
            values[i++] = counters.SpriteSetBuilds;
            values[i++] = counters.AtlasBatchBuilds;
            values[i++] = counters.AtlasHits;
            values[i++] = counters.AtlasMisses;
            values[i++] = counters.AtlasPlacements;
            values[i++] = raster.UnsettledDraws;
            values[i++] = raster.StretchedDraws;
            values[i++] = counters.ColorGlyphVectorDraws;

            foreach (var kind in s_clipKinds)
            {
                values[i++] = skia.GetClippedRuns(kind, inside: true);
                values[i++] = skia.GetClippedRuns(kind, inside: false);
            }

            values[i] = skia.RunsInsideEveryClip;
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
