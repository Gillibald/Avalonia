using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace TextTour
{
    /// <summary>
    /// Renders every chapter and step to PNG without a visible window: the shell is hosted at the
    /// requested size inside a small offscreen window (so styles, fonts and the platform font
    /// manager are the live ones) and captured with <see cref="RenderTargetBitmap"/> at 96 dpi.
    /// </summary>
    internal sealed class StepExporter
    {
        private const int FontsChapterIndex = 2;

        private readonly TourOptions _options;
        private readonly string _directory;

        public StepExporter(TourOptions options, string directory)
        {
            _options = options;
            _directory = directory;
        }

        /// <summary>Raised with the process exit code once every frame is written.</summary>
        public event Action<int>? Completed;

        public void Start() => Dispatcher.UIThread.Post(Run, DispatcherPriority.Background);

        private void Run()
        {
            var code = 0;
            var state = new TourState(_options.Preset)
            {
                Culture = _options.Culture,
                Dark = _options.Dark,
                ContentScale = _options.Scale,
                Animate = false,
            };
            var view = new TourView(state) { Width = _options.Width, Height = _options.Height };
            var canvas = new Canvas();
            canvas.Children.Add(view);

            var window = new Window
            {
                Width = 120,
                Height = 120,
                WindowDecorations = WindowDecorations.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Position = new PixelPoint(-30000, -30000),
                Content = canvas,
            };

            try
            {
                window.Show();
                Directory.CreateDirectory(_directory);

                var log = new StringBuilder();
                log.AppendLine("TextTour step export");
                log.AppendLine($"renderer: {Renderer()}");
                log.AppendLine($"platform: {RuntimeInformation.OSDescription}");
                log.AppendLine($"size: {_options.Width}x{_options.Height} points at 1x, theme {(_options.Dark ? "dark" : "light")}, " +
                               $"content scale {_options.Scale * 100:0} %, preset '{_options.Preset.Name}'");
                log.AppendLine();

                // The Fonts chapter's demo moves load other families, and loaded families change
                // what later fallback lookups answer, so it runs after the chapters that only read.
                var order = Enumerable.Range(0, view.ChapterCount).OrderBy(c => c == FontsChapterIndex ? 1 : 0);

                foreach (var c in order)
                {
                    view.ShowChapter(c);
                    var chapter = view.CurrentChapter;

                    for (var step = 1; step <= chapter.StepCount; step++)
                    {
                        for (var variant = 0; variant < chapter.ExportVariantCount(step); variant++)
                        {
                            chapter.Step = step;

                            // The chapter prepares its demo state against its final size.
                            view.UpdateLayout();
                            chapter.PrepareExport(step, variant);

                            var name = string.Format(CultureInfo.InvariantCulture, "{0:00}-{1}-{2}{3}.png", c + 1,
                                chapter.Title.ToLowerInvariant(), step, chapter.ExportVariantName(step, variant));

                            Capture(view, Path.Combine(_directory, name));
                            log.AppendLine($"{name}: {chapter.StepName} | {chapter.Caption}");
                        }
                    }

                    chapter.EndExport();
                }

                log.AppendLine();
                log.AppendLine("analysis of the string as the pipeline sees it:");
                var first = state.Analysis.Describe();
                log.AppendLine(first);

                // Every chapter has run its lookups by now; a fresh layout of the same string shows
                // whether those lookups changed what the pipeline answers.
                using (var again = new TextAnalysis(state.Text, state.StyleSpan, state.Typeface, state.FontSize,
                           state.Culture, state.Palette))
                {
                    var second = again.Describe();
                    log.AppendLine(second == first ?
                        "a fresh layout after all chapters gives the same analysis" :
                        "a fresh layout after all chapters differs:" + Environment.NewLine + second);
                }
                File.WriteAllText(Path.Combine(_directory, "export.txt"), log.ToString());
                Console.WriteLine($"Exported to {Path.GetFullPath(_directory)}");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                code = 1;
            }
            finally
            {
                window.Close();
                view.Dispose();
                state.Dispose();
            }

            Completed?.Invoke(code);
        }

        private void Capture(TourView view, string path)
        {
            view.UpdateLayout();

            using var bitmap = new RenderTargetBitmap(new PixelSize(_options.Width, _options.Height), new Vector(96, 96));
            bitmap.Render(view);
            bitmap.Save(path, PngBitmapEncoderOptions.Default);
        }

        private static string Renderer()
        {
            var factory = AvaloniaLocator.Current.GetService<IPlatformRenderInterface>();
            var name = factory?.GetType().FullName ?? "unknown";

            return $"{name} via RenderTargetBitmap (offscreen, grayscale antialiasing)";
        }
    }
}
