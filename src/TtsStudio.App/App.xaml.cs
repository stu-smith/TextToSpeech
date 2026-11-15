using System.Windows;
using System.Windows.Threading;
using TtsStudio.App.Services;
using TtsStudio.App.Views;

namespace TtsStudio.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        var settings = AppSettings.Load();
        ThemeManager.Apply(settings.Theme);
        var window = new MainWindow(settings, e.Args.FirstOrDefault());
        MainWindow = window;
        window.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Dialog.ShowError(MainWindow, "Something went wrong", e.Exception.Message);
        e.Handled = true;
    }
}
