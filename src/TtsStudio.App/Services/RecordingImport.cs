using System.IO;
using NAudio.Wave;
using TtsStudio.Core.Model;

namespace TtsStudio.App.Services;

/// <summary>
/// Makes a recording readable by the speech engine. Formats it cannot decode (m4a from Windows
/// Voice Recorder or a phone, aac, wma...) are converted to WAV with Windows' own decoders.
/// </summary>
public static class RecordingImport
{
    public const string Filter =
        "Audio recordings|*.wav;*.flac;*.ogg;*.mp3;*.m4a;*.aac;*.wma;*.mp4|All files (*.*)|*.*";

    /// <summary>
    /// A path the engine can read: the file itself, or a WAV converted next to it (reused if an
    /// earlier conversion is newer than the original).
    /// </summary>
    public static string Prepare(string path)
    {
        if (ReferenceRecordings.IsSupported(path)) return path;
        string dir = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path);
        string wav = Path.Combine(dir, stem + ".wav");
        if (File.Exists(wav) && File.GetLastWriteTimeUtc(wav) >= File.GetLastWriteTimeUtc(path)) return wav;
        for (int n = 2; File.Exists(wav); n++) wav = Path.Combine(dir, $"{stem} ({n}).wav");
        using var reader = new MediaFoundationReader(path);
        WaveFileWriter.CreateWaveFile(wav, reader);
        return wav;
    }
}
