using System;
using System.Collections.Generic;
using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace TextStress.AndroidHost
{
    /// <summary>
    /// Sets Avalonia up only once the activity has read its launch arguments: the rendering mode
    /// and the text rasterization mode are fixed when the platform initializes, and an
    /// application's <c>OnCreate</c> runs before any activity sees its intent.
    /// </summary>
    [Application]
    public sealed class TextStressApplication : AvaloniaAndroidApplication<StressApp>
    {
        private RunOptions? _options;

        public TextStressApplication(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
        }

        public override void OnCreate()
        {
        }

        /// <summary>Initializes Avalonia with <paramref name="options"/>; later calls in the same process do nothing.</summary>
        internal void Start(RunOptions options)
        {
            if (_options is not null)
            {
                return;
            }

            _options = options;
            base.OnCreate();
        }

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {
            var options = _options!;

            builder = base.CustomizeAppBuilder(builder)
                .WithInterFont()
                .With(StressRun.CreateFontManagerOptions(options));

            if (options.Render != "default")
            {
                builder = builder.With(new AndroidPlatformOptions { RenderingMode = ParseRendering(options.Render) });
            }

            return builder;
        }

        private static IReadOnlyList<AndroidRenderingMode> ParseRendering(string value) => value switch
        {
            "egl" => new[] { AndroidRenderingMode.Egl },
            "vulkan" => new[] { AndroidRenderingMode.Vulkan },
            "software" => new[] { AndroidRenderingMode.Software },
            _ => throw new ArgumentException($"Unknown --render '{value}' (egl, vulkan or software).")
        };
    }
}
