using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Media;
using TextShowcase.Panes;
using TextShowcase.Scenes;

namespace TextShowcase.Diagnostics
{
    /// <summary>
    /// Writes captures as PNG: the whole presentation surface, each pane's content and their
    /// diff, all rendered offscreen at scale 1 so files from different machines line up pixel
    /// for pixel. A manifest records what produced them.
    /// </summary>
    internal static class Capture
    {
        /// <summary>A directory name for one machine and one run: os, architecture, graphics, time.</summary>
        public static string SessionName() => string.Join("-",
            PlatformInfo.OsName.ToLowerInvariant(),
            PlatformInfo.Architecture,
            PlatformInfo.Graphics.ToLowerInvariant(),
            DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

        /// <summary>Captures one scene; returns a short description of what was written.</summary>
        public static string Scene(string directory, int number, Scene scene, Visual surface, PaneView left,
            PaneView right, bool leftIsManaged, bool withDiff = true)
        {
            Directory.CreateDirectory(directory);

            var prefix = Path.Combine(directory, FormattableString.Invariant($"{number:00}-{scene.Slug}"));
            var count = 0;

            using (var window = PixelDiff.Capture(surface))
            {
                SavePng(window, prefix + "-window.png");
                count++;
            }

            if (scene.HasCustomRightPane && leftIsManaged)
            {
                using var managedOnly = PixelDiff.Capture(left.ContentHost);
                SavePng(managedOnly, prefix + "-managed.png");

                return $"{count + 1} images";
            }

            using var leftImage = PixelDiff.Capture(left.ContentHost);
            using var rightImage = PixelDiff.Capture(right.ContentHost);

            SavePng(leftImage, prefix + (leftIsManaged ? "-managed.png" : "-backend.png"));
            SavePng(rightImage, prefix + (leftIsManaged ? "-backend.png" : "-managed.png"));
            count += 2;

            if (withDiff)
            {
                var result = PixelDiff.Compare(leftImage, rightImage);

                using (result.HeatMap)
                {
                    SavePng(result.HeatMap, prefix + "-diff.png");
                }

                File.AppendAllText(Path.Combine(directory, "diff.tsv"), FormattableString.Invariant(
                    $"{number:00}-{scene.Slug}\t{result.DifferingPixels}\t{result.InkPixels}\t{result.MaxDelta}\t{result.Rmse:0.000}\t{result.Verdict.Text}\n"));
                count++;
            }

            return $"{count} images";
        }

        /// <summary>Writes <c>manifest.json</c>: machine, renderer, modes and the scenes captured.</summary>
        public static void WriteManifest(string directory, IReadOnlyList<Scene> scenes)
        {
            Directory.CreateDirectory(directory);

            var json = new StringBuilder();
            json.Append("{\n");
            Field(json, "os", System.Runtime.InteropServices.RuntimeInformation.OSDescription);
            Field(json, "arch", PlatformInfo.Architecture);
            Field(json, "runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            Field(json, "graphics", PlatformInfo.Graphics);
            Field(json, "renderer", PlatformInfo.Renderer);
            Field(json, "backend_scaler", PlatformInfo.BackendScaler);
            Field(json, "platform_default_mode", PlatformInfo.PlatformDefault.ToString());
            Field(json, "commit", Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
            Field(json, "captured", DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture));
            json.Append("  \"scenes\": [");

            for (var i = 0; i < scenes.Count; i++)
            {
                json.Append(i == 0 ? "\n" : ",\n");
                json.Append("    \"").Append(FormattableString.Invariant($"{i + 1:00}-")).Append(scenes[i].Slug).Append('"');
            }

            json.Append("\n  ]\n}\n");
            File.WriteAllText(Path.Combine(directory, "manifest.json"), json.ToString());
        }

        private static void SavePng(Avalonia.Media.Imaging.Bitmap bitmap, string path)
        {
            using var stream = File.Create(path);
            bitmap.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        private static void Field(StringBuilder json, string name, string value)
        {
            json.Append("  \"").Append(name).Append("\": \"")
                .Append(value.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\",\n");
        }
    }
}
