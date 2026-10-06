using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace TextStress.AndroidHost
{
    public sealed class StressApp : Avalonia.Application
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
                lifetime.MainViewFactory = AndroidRun.CreateHost;
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
