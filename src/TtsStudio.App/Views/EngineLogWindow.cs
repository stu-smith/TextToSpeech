using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TtsStudio.App.Services;
using TtsStudio.Core.Engine;

namespace TtsStudio.App.Views;

/// <summary>Live log of runtime installation and the engine worker.</summary>
public sealed class EngineLogWindow : Window
{
    private readonly TextBox _box;

    public EngineLogWindow(EngineService engine)
    {
        Title = "Speech engine log";
        Width = 820;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        TitleBar.Apply(this);

        var info = new TextBlock
        {
            Text = $"Runtime folder: {engine.Runtime.Root}\nEngine folder: {engine.Runtime.EngineDir}",
            Margin = new Thickness(0, 0, 0, 10),
        };
        info.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        _box = new TextBox
        {
            IsReadOnly = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = string.Join(Environment.NewLine, engine.LogLines),
        };
        var dock = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(info, Dock.Top);
        dock.Children.Add(info);
        dock.Children.Add(_box);
        Content = dock;

        engine.LogLine += OnLog;
        Closed += (_, _) => engine.LogLine -= OnLog;
        Loaded += (_, _) => _box.ScrollToEnd();
    }

    private void OnLog(string line) => Dispatcher.BeginInvoke(() =>
    {
        _box.AppendText(Environment.NewLine + line);
        _box.ScrollToEnd();
    });
}
