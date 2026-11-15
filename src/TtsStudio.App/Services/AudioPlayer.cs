using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TtsStudio.App.Services;

/// <summary>Plays rendered files and reports the playback position for the playhead.</summary>
public sealed partial class AudioPlayer : ObservableObject
{
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private double _startAt;

    [ObservableProperty] public partial bool IsPlaying { get; private set; }

    /// <summary>Seconds into the current file, or -1 when stopped.</summary>
    [ObservableProperty] public partial double Position { get; private set; } = -1;

    [ObservableProperty] public partial string? CurrentFile { get; private set; }

    public AudioPlayer()
    {
        _player.MediaOpened += (_, _) =>
        {
            _player.Position = TimeSpan.FromSeconds(_startAt);
            _player.Play();
        };
        _player.MediaEnded += (_, _) => Stop();
        _player.MediaFailed += (_, _) => Stop();
        _timer.Tick += (_, _) => Position = _player.Position.TotalSeconds;
    }

    public void Play(string file, double from = 0)
    {
        _startAt = Math.Max(0, from);
        if (CurrentFile == file && _player.Source is not null)
        {
            _player.Position = TimeSpan.FromSeconds(_startAt);
            _player.Play();
        }
        else
        {
            CurrentFile = file;
            _player.Open(new Uri(file));
        }
        IsPlaying = true;
        Position = _startAt;
        _timer.Start();
    }

    public void Stop()
    {
        _player.Stop();
        _timer.Stop();
        IsPlaying = false;
        Position = -1;
    }
}
