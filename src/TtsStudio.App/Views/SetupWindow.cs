using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TtsStudio.App.Services;
using TtsStudio.Core.Engine;

namespace TtsStudio.App.Views;

/// <summary>
/// Lists what the speech engine still needs to download, with sizes, and downloads it once the user
/// agrees, showing progress. <see cref="Window.DialogResult"/> is true when everything was installed.
/// </summary>
public sealed class SetupWindow : Window
{
    private readonly RuntimePlan _plan;
    private readonly RuntimeManager _runtime;
    private readonly IProgress<string>? _log;
    private readonly ProgressBar _progress;
    private readonly TextBlock _status;
    private readonly Button _ok;
    private CancellationTokenSource? _cts;
    private Task? _install;

    public SetupWindow(RuntimeManager runtime, RuntimePlan plan, IProgress<string>? log)
    {
        _runtime = runtime;
        _plan = plan;
        _log = log;
        Title = "Set up the speech engine";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        TitleBar.Apply(this);

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        panel.Children.Add(new TextBlock { Text = Title, Style = (Style)FindResource("TitleText"), Margin = new Thickness(0, 0, 0, 10) });
        var intro = new TextBlock
        {
            Text = "TTS Studio speaks with the Kokoro model, which runs locally and needs the downloads below. " +
                   $"They are kept in {runtime.Root}; nothing is installed system-wide.",
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19,
        };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        panel.Children.Add(intro);

        var grid = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foreach (var d in plan.Downloads)
        {
            var name = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            name.Children.Add(new TextBlock { Text = d.Name });
            name.Children.Add(new TextBlock { Text = d.Description, Style = (Style)FindResource("DimText"), FontSize = 12 });
            AddRow(grid, name, new TextBlock { Text = FormatSize(d.Bytes), VerticalAlignment = VerticalAlignment.Top });
        }
        var rule = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 8) };
        rule.SetResourceReference(Border.BackgroundProperty, "BorderStrongBrush");
        Grid.SetColumnSpan(rule, 2);
        AddRow(grid, rule);
        string total = FormatSize(plan.TotalBytes) + (plan.HasUnknownSizes ? " +" : "");
        AddRow(grid, new TextBlock { Text = "Total", FontWeight = FontWeights.SemiBold },
            new TextBlock { Text = total, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(grid);

        _progress = new ProgressBar { Height = 6, Maximum = 1, Margin = new Thickness(0, 18, 0, 0) };
        panel.Children.Add(_progress);
        _status = new TextBlock
        {
            Text = plan.HasUnknownSizes ? "Some sizes could not be checked; you may be offline." : "Choose OK to download.",
            Style = (Style)FindResource("DimText"),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 8, 0, 0),
        };
        panel.Children.Add(_status);

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        _ok = new Button { Content = "OK", MinWidth = 88, IsDefault = true, Style = (Style)FindResource("AccentButton") };
        _ok.Click += async (_, _) => await InstallAsync();
        var cancel = new Button { Content = "Cancel", MinWidth = 88, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        row.Children.Add(_ok);
        row.Children.Add(cancel);
        panel.Children.Add(row);
        Content = panel;

        Closing += OnClosing;
    }

    private static void AddRow(Grid grid, UIElement left, UIElement? right = null)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        int index = grid.RowDefinitions.Count - 1;
        Grid.SetRow(left, index);
        grid.Children.Add(left);
        if (right is null) return;
        Grid.SetRow(right, index);
        Grid.SetColumn(right, 1);
        if (right is FrameworkElement fe) fe.Margin = new Thickness(16, 0, 0, 0);
        grid.Children.Add(right);
    }

    private async Task InstallAsync()
    {
        _ok.IsEnabled = false;
        _progress.Value = 0;
        _status.Text = "Starting...";
        _cts = new CancellationTokenSource();
        // Progress<T> raises its callbacks on this (UI) thread.
        var log = new Progress<string>(line =>
        {
            _log?.Report(line);
            if (!string.IsNullOrWhiteSpace(line)) _status.Text = line.Trim();
        });
        var progress = new Progress<double>(p => _progress.Value = p);
        _install = _runtime.InstallAsync(_plan, log, progress, _cts.Token);
        bool done = false;
        try
        {
            await _install;
            done = true;
        }
        catch (OperationCanceledException)
        {
            // Cancelled from OnClosing, which closes the window once this returns.
        }
        catch (Exception e)
        {
            _log?.Report("ERROR: " + e.Message);
            _status.Text = "Setup failed: " + e.Message + " Choose OK to try again.";
            _ok.IsEnabled = true;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _install = null;
        }
        if (done) DialogResult = true;
    }

    /// <summary>Cancelling mid-download stops uv and waits for it to exit before the window closes.</summary>
    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_install is not { } install || _cts is null) return;
        e.Cancel = true;
        _status.Text = "Cancelling...";
        IsEnabled = false;
        await _cts.CancelAsync();
        try { await install; } catch { /* reported by InstallAsync */ }
        Closing -= OnClosing;
        Close();
    }

    private static string FormatSize(long? bytes) => bytes switch
    {
        null => "size unknown",
        >= 1_000_000_000 => $"{bytes / 1e9:0.0#} GB",
        >= 100_000_000 => $"{bytes / 1e6:0} MB",
        _ => $"{bytes / 1e6:0.0} MB",
    };
}
