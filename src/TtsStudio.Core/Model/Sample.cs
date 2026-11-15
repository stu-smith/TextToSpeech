using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TtsStudio.Core.Model;

/// <summary>One text-to-speech sample: plain text plus annotated regions.</summary>
public sealed partial class Sample : ObservableObject
{
    [ObservableProperty] public partial string Id { get; set; } = Ids.New();
    [ObservableProperty] public partial string Name { get; set; } = "New sample";

    /// <summary>Export file name without extension; defaults to the sample name.</summary>
    [ObservableProperty] public partial string? FileName { get; set; }

    /// <summary>The text to speak. Change it with <see cref="ReplaceText"/> to keep regions aligned.</summary>
    [ObservableProperty] public partial string Text { get; set; } = "";

    /// <summary>Voice id, or null for the project default.</summary>
    [ObservableProperty] public partial string? Voice { get; set; }

    /// <summary>Overall speed, or null for the project default.</summary>
    [ObservableProperty] public partial double? Speed { get; set; }

    /// <summary>Overall pitch shift in semitones.</summary>
    [ObservableProperty] public partial double Pitch { get; set; }

    /// <summary>Overall pitch range multiplier (0 monotone, 1 natural).</summary>
    [ObservableProperty] public partial double PitchRange { get; set; } = 1.0;

    [ObservableProperty] public partial string? Notes { get; set; }

    public ObservableCollection<Region> Regions { get; set; } = [];

    /// <summary>
    /// Replaces the text and moves, resizes or removes regions to follow the edit, as if the
    /// change had been typed. Returns the regions that were removed because their text was deleted.
    /// </summary>
    public IReadOnlyList<Region> ReplaceText(string newText)
    {
        var removed = TextEdits.Apply(Text, newText, Regions);
        Text = newText;
        return removed;
    }

    public string TextOf(Region r)
    {
        int a = Math.Clamp(r.Start, 0, Text.Length), b = Math.Clamp(r.End, a, Text.Length);
        return Text[a..b];
    }

    public string ExportName => string.IsNullOrWhiteSpace(FileName) ? Name : FileName!;
}
