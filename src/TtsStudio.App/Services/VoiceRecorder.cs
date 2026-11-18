using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TtsStudio.App.Services;

public sealed record Microphone(string Id, string Name);

/// <summary>
/// Captures from a microphone (WASAPI, shared mode). While open it reports the input level, so the
/// user can check their microphone; between <see cref="BeginRecording"/> and <see cref="EndRecording"/>
/// it also writes the sound, mixed to mono, as a 16-bit WAV at the device's sample rate.
/// </summary>
public sealed class VoiceRecorder : IDisposable
{
    private readonly object _lock = new();
    private WasapiCapture? _capture;
    private WaveFileWriter? _writer;
    private long _framesWritten;
    private short[] _buffer = [];

    private static readonly Guid IeeeFloat = new("00000003-0000-0010-8000-00aa00389b71"); // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT

    /// <summary>Peak level of each captured buffer, 0..1. Raised on the capture thread.</summary>
    public event Action<float>? Level;

    /// <summary>Capture stopped by itself (e.g. the microphone was unplugged). Raised on the capture thread.</summary>
    public event Action<Exception?>? Failed;

    public bool IsRecording { get; private set; }

    /// <summary>Length of the recording so far.</summary>
    public TimeSpan Recorded
    {
        get
        {
            lock (_lock)
                return _capture is null ? TimeSpan.Zero : TimeSpan.FromSeconds((double)_framesWritten / _capture.WaveFormat.SampleRate);
        }
    }

    public static IReadOnlyList<Microphone> Devices()
    {
        using var e = new MMDeviceEnumerator();
        return e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Select(d => new Microphone(d.ID, d.FriendlyName)).ToList();
    }

    public static string? DefaultDeviceId()
    {
        using var e = new MMDeviceEnumerator();
        return e.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console) ? e.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console).ID : null;
    }

    /// <summary>Starts listening to a device (the default if null), replacing any previous one.</summary>
    public void Open(string? deviceId)
    {
        Close();
        using var e = new MMDeviceEnumerator();
        var device = deviceId is null ? e.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console) : e.GetDevice(deviceId);
        var capture = new WasapiCapture(device);
        capture.DataAvailable += OnData;
        capture.RecordingStopped += (_, a) =>
        {
            if (ReferenceEquals(capture, _capture)) Failed?.Invoke(a.Exception);
        };
        _capture = capture;
        capture.StartRecording();
    }

    public void BeginRecording(string path)
    {
        lock (_lock)
        {
            if (_capture is null) throw new InvalidOperationException("No microphone is open.");
            _writer?.Dispose();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            _writer = new WaveFileWriter(path, new WaveFormat(_capture.WaveFormat.SampleRate, 16, 1));
            _framesWritten = 0;
            IsRecording = true;
        }
    }

    public void EndRecording()
    {
        lock (_lock)
        {
            IsRecording = false;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var format = ((WasapiCapture)sender!).WaveFormat;
        int channels = format.Channels, bytes = format.BitsPerSample / 8, frames = e.BytesRecorded / format.BlockAlign;
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloat);
        if (_buffer.Length < frames) _buffer = new short[frames];
        float peak = 0;
        var data = e.Buffer.AsSpan(0, e.BytesRecorded);
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                var s = data.Slice(f * format.BlockAlign + c * bytes, bytes);
                sum += isFloat && bytes == 4 ? BitConverter.ToSingle(s)
                    : bytes == 2 ? BitConverter.ToInt16(s) / 32768f
                    : bytes == 3 ? ((s[0] << 8) | (s[1] << 16) | (s[2] << 24)) / 2147483648f
                    : bytes == 4 ? BitConverter.ToInt32(s) / 2147483648f
                    : 0;
            }
            float mono = Math.Clamp(sum / channels, -1f, 1f);
            peak = Math.Max(peak, Math.Abs(mono));
            _buffer[f] = (short)(mono * 32767);
        }
        lock (_lock)
        {
            if (_writer is not null)
            {
                _writer.Write(MemoryMarshal.AsBytes(_buffer.AsSpan(0, frames)));
                _framesWritten += frames;
            }
        }
        Level?.Invoke(peak);
    }

    private void Close()
    {
        EndRecording();
        var capture = _capture;
        _capture = null;
        if (capture is null) return;
        capture.DataAvailable -= OnData;
        try
        {
            capture.StopRecording();
        }
        catch (COMException)
        {
        }
        capture.Dispose();
    }

    public void Dispose() => Close();
}
