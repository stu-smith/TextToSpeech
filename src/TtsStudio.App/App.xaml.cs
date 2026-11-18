using System.IO;
using System.Windows;
using System.Windows.Threading;
using TtsStudio.App.Services;
using TtsStudio.App.Views;
using TtsStudio.Core;

namespace TtsStudio.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        var settings = AppSettings.Load();
        ThemeManager.Apply(settings.Theme);
        if (e.Args is [RecordingRequest.Switch, var request, ..])
        {
            RunRecorder(request);
            return;
        }
        var window = new MainWindow(settings, e.Args.FirstOrDefault());
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Recorder-only mode, used by the MCP server's record_reading tool: shows just the recorder for
    /// the request's text, writes the outcome next to the request and exits (0 saved, 2 cancelled, 1 error).
    /// </summary>
    private void RunRecorder(string requestPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var result = new RecordingResult();
        try
        {
            var req = RecordingRequest.Load(requestPath);
            var window = new RecordWindow(req.Text, req.Title, req.Output, standalone: true);
            MainWindow = window;
            if (window.ShowDialog() == true)
                result = new RecordingResult { Outcome = "saved", Output = window.Output, Seconds = Math.Round(window.Seconds, 2) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            result = new RecordingResult { Outcome = "error", Error = ex.Message };
        }
        try
        {
            result.Save(RecordingRequest.ResultPath(requestPath));
        }
        catch (IOException)
        {
        }
        Shutdown(result.Outcome switch { "saved" => 0, "cancelled" => 2, _ => 1 });
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Dialog.ShowError(MainWindow, "Something went wrong", e.Exception.Message);
        e.Handled = true;
    }
}
