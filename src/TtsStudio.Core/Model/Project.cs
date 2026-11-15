using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TtsStudio.Core.Model;

public sealed partial class Project : ObservableObject
{
    public const string FormatName = "tts-studio-project";
    public const int CurrentVersion = 1;

    public string Format { get; set; } = FormatName;
    public int Version { get; set; } = CurrentVersion;

    [ObservableProperty] public partial string Name { get; set; } = "Untitled";
    [ObservableProperty] public partial string DefaultVoice { get; set; } = Voices.Default;
    [ObservableProperty] public partial double DefaultSpeed { get; set; } = 1.0;

    /// <summary>Folder for "export all", relative to the project file.</summary>
    [ObservableProperty] public partial string ExportFolder { get; set; } = "audio";

    /// <summary>wav, flac, ogg or mp3.</summary>
    [ObservableProperty] public partial string ExportFormat { get; set; } = "wav";

    /// <summary>Pronunciations applied to every sample (inline region pronunciations win).</summary>
    public ObservableCollection<LexiconEntry> Lexicon { get; set; } = [];

    public ObservableCollection<Sample> Samples { get; set; } = [];

    public Sample? FindSample(string idOrName) =>
        Samples.FirstOrDefault(s => s.Id == idOrName)
        ?? Samples.FirstOrDefault(s => string.Equals(s.Name, idOrName, StringComparison.OrdinalIgnoreCase));

    public Dictionary<string, string> LexiconMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Lexicon)
            if (!string.IsNullOrWhiteSpace(e.Word) && !string.IsNullOrWhiteSpace(e.Phonemes))
                map[e.Word.Trim().ToLowerInvariant()] = e.Phonemes.Trim().Trim('/');
        return map;
    }
}

public sealed partial class LexiconEntry : ObservableObject
{
    [ObservableProperty] public partial string Word { get; set; } = "";
    [ObservableProperty] public partial string Phonemes { get; set; } = "";
}
