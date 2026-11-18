using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using TtsStudio.App.Services;

namespace TtsStudio.App.Views;

/// <summary>
/// Records someone reading a sample's text: shows the text, a microphone picker with a level meter,
/// record/stop, and playback of the take before it is kept. Space records or stops, Enter keeps the
/// take, Esc cancels. Saves to <see cref="Output"/> only when the take is kept.
/// </summary>
public sealed class RecordWindow : Window
{
    private readonly VoiceRecorder _recorder = new();
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly string _take = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TtsStudio", $"take-{Guid.NewGuid():N}.wav");

    private readonly ComboBox _devices = new() { MinWidth = 260 };
    private readonly Border _meter = new() { Height = 8, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    private readonly Border _meterTrack = new() { Height = 8, CornerRadius = new CornerRadius(4) };
    private readonly Button _record = new() { Width = 76, Height = 76, Padding = new Thickness(0) };
    private readonly Ellipse _recordDot = new() { Width = 26, Height = 26, Fill = Brushes.White };
    private readonly Rectangle _stopSquare = new() { Width = 22, Height = 22, RadiusX = 4, RadiusY = 4, Fill = Brushes.White, Visibility = Visibility.Collapsed };
    private readonly TextBlock _clock = new() { Text = "0:00.0", FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 0), MinHeight = 36 };
    private readonly Button _play = new();
    private readonly TextBlock _playLabel = new() { Text = "Play take", Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _use = new() { Content = "Use recording", MinWidth = 120, Margin = new Thickness(8, 0, 0, 0) };

    private volatile float _peak;
    private float _shownLevel;
    private float _takePeak;
    private bool _hasTake, _playing;

    /// <summary>Where the take is saved when the user keeps it.</summary>
    public string Output { get; }

    /// <summary>Length of the kept take.</summary>
    public double Seconds { get; private set; }

    /// <param name="standalone">Opened on its own (by a coding agent through the MCP server) rather than from the app.</param>
    public RecordWindow(string text, string? title, string output, bool standalone = false)
    {
        Output = output;
        Title = "Record a reading — TTS Studio";
        Width = 680;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = standalone;
        Topmost = standalone; // started from a coding agent: make sure the user sees it
        WindowStartupLocation = standalone ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        TitleBar.Apply(this);

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        panel.Children.Add(new TextBlock { Text = "Record a reading", Style = (Style)FindResource("TitleText") });
        panel.Children.Add(Dim((title is null ? "" : $"“{title}”. ") +
                               "Read the text aloud the way you'd like it to sound: the voice will follow your timing and intonation.", 0, 6));

        // The text to read.
        var script = new TextBlock { Text = text, FontSize = 22, TextWrapping = TextWrapping.Wrap, LineHeight = 33 };
        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20, 16, 20, 16),
            Margin = new Thickness(0, 14, 0, 0),
            BorderThickness = new Thickness(1),
            Child = new ScrollViewer { Content = script, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        card.SetResourceReference(Border.BackgroundProperty, "BgBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        panel.Children.Add(card);

        // Microphone and level.
        var mic = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var micLabel = new TextBlock { Text = "Microphone", VerticalAlignment = VerticalAlignment.Center, Width = 90 };
        mic.Children.Add(micLabel);
        _devices.DisplayMemberPath = nameof(Microphone.Name);
        mic.Children.Add(_devices);
        panel.Children.Add(mic);
        _meterTrack.SetResourceReference(Border.BackgroundProperty, "ControlBrush");
        var meter = new Grid { Margin = new Thickness(90, 8, 0, 0) };
        meter.Children.Add(_meterTrack);
        meter.Children.Add(_meter);
        panel.Children.Add(meter);

        // Record / stop.
        var face = new Grid { Width = 64, Height = 64 };
        var circle = new Ellipse();
        circle.SetResourceReference(Shape.FillProperty, "DangerBrush");
        face.Children.Add(circle);
        face.Children.Add(_recordDot);
        face.Children.Add(_stopSquare);
        _record.Content = face;
        _record.Style = (Style)FindResource("SubtleButton");
        _record.HorizontalAlignment = HorizontalAlignment.Center;
        _record.Margin = new Thickness(0, 18, 0, 0);
        _record.ToolTip = "Record or stop (Space)";
        _record.Click += (_, _) => ToggleRecording();
        panel.Children.Add(_record);
        panel.Children.Add(_clock);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        panel.Children.Add(_status);

        // Take controls.
        var row = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        _play.Style = (Style)FindResource("SubtleButton");
        _play.Margin = new Thickness(-8, 0, 0, 0);
        _play.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { Glyph("\uE768"), _playLabel } };
        _play.Click += (_, _) => TogglePlayback();
        _play.IsEnabled = false;
        DockPanel.SetDock(_play, Dock.Left);
        row.Children.Add(_play);
        _use.Style = (Style)FindResource("AccentButton");
        _use.IsEnabled = false;
        _use.Click += (_, _) => Keep();
        DockPanel.SetDock(_use, Dock.Right);
        row.Children.Add(_use);
        var cancel = new Button { Content = "Cancel", MinWidth = 88, IsCancel = true };
        DockPanel.SetDock(cancel, Dock.Right);
        row.Children.Add(cancel);
        panel.Children.Add(row);
        Content = panel;

        _recorder.Level += level => _peak = Math.Max(_peak, level);
        _recorder.Failed += e => Dispatcher.BeginInvoke(() => OnFailed(e));
        _player.MediaEnded += (_, _) => StopPlayback();
        _tick.Tick += (_, _) => OnTick();
        _devices.SelectionChanged += (_, _) => OpenDevice();
        PreviewKeyDown += OnKey;
        Loaded += (_, _) => LoadDevices();
        Closed += (_, _) => CleanUp();
    }

    private TextBlock Dim(string text, double top, double bottom)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, bottom) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        return t;
    }

    private TextBlock Glyph(string glyph) => new() { Text = glyph, Style = (Style)FindResource("Icon") };

    private void SetStatus(string text, bool warning = false)
    {
        _status.Text = text;
        _status.SetResourceReference(TextBlock.ForegroundProperty, warning ? "WarningBrush" : "TextDimBrush");
    }

    // ------------------------------------------------------------------ microphone

    private void LoadDevices()
    {
        try
        {
            var devices = VoiceRecorder.Devices();
            if (devices.Count == 0)
            {
                _record.IsEnabled = false;
                SetStatus("No microphone found. Connect one, then open the recorder again.", warning: true);
                return;
            }
            _devices.ItemsSource = devices;
            string? id = VoiceRecorder.DefaultDeviceId();
            _devices.SelectedItem = devices.FirstOrDefault(d => d.Id == id) ?? devices[0]; // opens it
            _tick.Start();
            _record.Focus();
        }
        catch (COMException e)
        {
            _record.IsEnabled = false;
            SetStatus("Windows audio is not available: " + e.Message, warning: true);
        }
    }

    private void OpenDevice()
    {
        if (_devices.SelectedItem is not Microphone d || _recorder.IsRecording) return;
        try
        {
            _recorder.Open(d.Id);
            _record.IsEnabled = true;
            SetStatus(_hasTake ? TakeSummary() : "Press Record (or Space) and read the text. Watch the meter: speak loud enough to fill most of it.");
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or ArgumentException)
        {
            _record.IsEnabled = false;
            SetStatus($"Couldn't use this microphone ({e.Message}). If apps aren't allowed to use the microphone, " +
                      "turn it on in Windows Settings › Privacy & security › Microphone.", warning: true);
        }
    }

    private void OnFailed(Exception? e)
    {
        if (_recorder.IsRecording) StopRecording();
        _record.IsEnabled = false;
        SetStatus("The microphone stopped" + (e is null ? "." : $": {e.Message}") + " Choose it again, or another one.", warning: true);
    }

    private void OnTick()
    {
        float peak = _peak;
        _peak = 0;
        if (_recorder.IsRecording) _takePeak = Math.Max(_takePeak, peak);
        _shownLevel = Math.Max(peak, _shownLevel * 0.85f);
        double width = _meterTrack.ActualWidth * Math.Min(1, Math.Sqrt(_shownLevel)); // sqrt: quiet speech still shows
        _meter.Width = Math.Max(0, width);
        _meter.SetResourceReference(Border.BackgroundProperty, _shownLevel > 0.97f ? "DangerBrush" : _shownLevel > 0.7f ? "WarningBrush" : "SuccessBrush");
        if (_recorder.IsRecording) _clock.Text = Clock(_recorder.Recorded.TotalSeconds);
    }

    private static string Clock(double s) => $"{(int)s / 60}:{s % 60:00.0}";

    // ------------------------------------------------------------------ recording and playback

    private void ToggleRecording()
    {
        if (_recorder.IsRecording) StopRecording();
        else StartRecording();
    }

    private void StartRecording()
    {
        if (!_record.IsEnabled) return;
        StopPlayback();
        _player.Close(); // release the previous take
        try
        {
            _recorder.BeginRecording(_take);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetStatus("Couldn't start recording: " + e.Message, warning: true);
            return;
        }
        _takePeak = 0;
        _recordDot.Visibility = Visibility.Collapsed;
        _stopSquare.Visibility = Visibility.Visible;
        _devices.IsEnabled = _play.IsEnabled = _use.IsEnabled = false;
        SetStatus("Recording… press Stop (or Space) when you've finished.");
    }

    private void StopRecording()
    {
        Seconds = _recorder.Recorded.TotalSeconds;
        _recorder.EndRecording();
        _recordDot.Visibility = Visibility.Visible;
        _stopSquare.Visibility = Visibility.Collapsed;
        _devices.IsEnabled = true;
        _clock.Text = Clock(Seconds);
        _hasTake = Seconds >= 0.3;
        _play.IsEnabled = _use.IsEnabled = _hasTake;
        if (!_hasTake) SetStatus("That was too short to use. Press Record to try again.", warning: true);
        else if (_takePeak < 0.02) SetStatus("This take is almost silent. Check the microphone isn't muted, then record again.", warning: true);
        else SetStatus(TakeSummary());
        if (_hasTake) _use.Focus();
    }

    private string TakeSummary() => $"Take: {Seconds:0.0} s. Play it back, record again, or use it (Enter).";

    private void TogglePlayback()
    {
        if (_playing)
        {
            StopPlayback();
            return;
        }
        if (!_hasTake || !File.Exists(_take)) return;
        _player.Open(new Uri(_take));
        _player.Play();
        _playing = true;
        _playLabel.Text = "Stop";
    }

    private void StopPlayback()
    {
        if (!_playing) return;
        _player.Stop();
        _playing = false;
        _playLabel.Text = "Play take";
    }

    private void Keep()
    {
        if (!_hasTake || _recorder.IsRecording) return;
        StopPlayback();
        _player.Close();
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Output))!);
            File.Copy(_take, Output, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus("Couldn't save the recording: " + e.Message, warning: true);
            return;
        }
        DialogResult = true;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && !_devices.IsDropDownOpen)
        {
            ToggleRecording();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _use.IsEnabled)
        {
            Keep();
            e.Handled = true;
        }
    }

    private void CleanUp()
    {
        _tick.Stop();
        _recorder.Dispose();
        _player.Close();
        try
        {
            if (File.Exists(_take)) File.Delete(_take);
        }
        catch (IOException)
        {
        }
    }
}
