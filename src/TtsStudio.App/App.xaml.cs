using System.Windows;
using System.Windows.Threading;
using TtsStudio.App.Views;

namespace TtsStudio.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        var window = new MainWindow(e.Args.FirstOrDefault());
        MainWindow = window;
        window.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Dialog.ShowError(MainWindow, "Something went wrong", e.Exception.Message);
        e.Handled = true;
    }
}
