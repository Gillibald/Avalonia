using System;
using System.Collections.Generic;
using System.IO;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Avalonia;
using Avalonia.Android;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace TextShowcase.AndroidHost
{
    /// <summary>
    /// Shows the showcase full screen in landscape. Options arrive as one string extra, the
    /// desktop command line:
    /// <c>adb shell am start -n com.avalonia.textshowcase/.MainActivity --es args "--capture-all captures/run1"</c>.
    /// Relative capture directories land in the app's external files directory.
    /// </summary>
    [Activity(Name = "com.avalonia.textshowcase.MainActivity", Label = "TextShowcase",
        Theme = "@style/Theme.AppCompat.Light.NoActionBar", MainLauncher = true, Exported = true,
        ScreenOrientation = ScreenOrientation.SensorLandscape,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
    public sealed class MainActivity : AvaloniaMainActivity
    {
        internal static MainActivity? Current { get; private set; }

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            Current = this;
            Window?.AddFlags(WindowManagerFlags.KeepScreenOn);

            var filesDir = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath;
            var args = new List<string>((Intent?.GetStringExtra("args") ?? "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            for (var i = 0; i + 1 < args.Count; i++)
            {
                if ((args[i] == "--capture-all" || args[i] == "--capture-dir") && !Path.IsPathRooted(args[i + 1]))
                {
                    args[i + 1] = Path.Combine(filesDir, args[i + 1]);
                }
            }

            if (!args.Contains("--capture-dir"))
            {
                args.Add("--capture-dir");
                args.Add(Path.Combine(filesDir, "captures"));
            }

            ShowcaseOptions.Current = ShowcaseOptions.Parse(args.ToArray());

            base.OnCreate(savedInstanceState);
        }
    }

    [Application]
    public sealed class ShowcaseApplication : AvaloniaAndroidApplication<ShowcaseApp>
    {
        public ShowcaseApplication(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
        }

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
            => base.CustomizeAppBuilder(builder)
                .WithInterFont()
                // Registered so the panes can scope the mode; the chrome keeps the platform default.
                .With(new FontManagerOptions());
    }

    public sealed class ShowcaseApp : Avalonia.Application
    {
        public override void Initialize()
        {
            RequestedThemeVariant = ThemeVariant.Light;
            Styles.Add(new FluentTheme());
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IActivityApplicationLifetime lifetime)
            {
                lifetime.MainViewFactory = () =>
                {
                    var view = new ShowcaseView(ShowcaseOptions.Current);
                    view.ExitRequested += _ => MainActivity.Current?.FinishAndRemoveTask();
                    return view;
                };
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
