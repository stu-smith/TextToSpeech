using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TtsStudio.Core.Model;

/// <summary>
/// A highlighted range of a sample's text with speech properties. Offsets are character
/// indices into <see cref="Sample.Text"/> (end exclusive). Regions may overlap; prosody
/// properties of overlapping regions add up, while pronunciation and stress apply to whole
/// words and the first region wins where two of those overlap. A null property is "not set".
/// </summary>
public sealed partial class Region : ObservableObject
{
    [ObservableProperty] public partial string Id { get; set; } = Ids.New();
    [ObservableProperty] public partial int Start { get; set; }
    [ObservableProperty] public partial int End { get; set; }
    [ObservableProperty] public partial string? Label { get; set; }

    /// <summary>Phonemes in Kokoro's set (IPA plus A I O Q W Y for diphthongs), without slashes.</summary>
    [ObservableProperty] public partial string? Pronunciation { get; set; }

    /// <summary>Lexical stress change: -2 removes stress, -1 lowers, +1 raises, +2 forces primary.</summary>
    [ObservableProperty] public partial double? Stress { get; set; }

    /// <summary>Pitch shift in semitones.</summary>
    [ObservableProperty] public partial double? Pitch { get; set; }

    /// <summary>Speaking rate multiplier; above 1 is faster.</summary>
    [ObservableProperty] public partial double? Rate { get; set; }

    /// <summary>Pitch glide across the region: "rise" or "fall".</summary>
    [ObservableProperty] public partial string? Contour { get; set; }

    /// <summary>Size of the contour glide in semitones (default 4).</summary>
    [ObservableProperty] public partial double? ContourSemitones { get; set; }

    /// <summary>Pitch range multiplier: 0 is monotone, 1 unchanged, 2 very animated.</summary>
    [ObservableProperty] public partial double? PitchRange { get; set; }

    /// <summary>Volume change in dB.</summary>
    [ObservableProperty] public partial double? Volume { get; set; }

    /// <summary>Experimental offset to the model's internal energy curve (about -3 to +3).</summary>
    [ObservableProperty] public partial double? Energy { get; set; }

    [ObservableProperty] public partial double? PauseBeforeMs { get; set; }
    [ObservableProperty] public partial double? PauseAfterMs { get; set; }

    [JsonIgnore] public int Length => End - Start;

    [JsonIgnore]
    public bool HasProsody => Pitch is not null || Rate is not null || Contour is not null
                              || PitchRange is not null || Volume is not null || Energy is not null;

    /// <summary>Short description of the properties, e.g. "/ˈIɑs/ · pitch +2" (updates with them).</summary>
    [JsonIgnore] public string Summary => RegionTools.Summary(this);

    [JsonIgnore] public RegionKind Kind => RegionTools.Kind(this);

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Summary) or nameof(Kind) or nameof(Id) or nameof(Start) or nameof(End)) return;
        base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Summary)));
        base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Kind)));
    }

    /// <summary>A detached copy with a new id.</summary>
    public Region Clone()
    {
        var r = new Region { Start = Start, End = End };
        r.CopyPropertiesFrom(this);
        return r;
    }

    /// <summary>Copies every speech property (not the id or range) from another region.</summary>
    public void CopyPropertiesFrom(Region o)
    {
        Label = o.Label; Pronunciation = o.Pronunciation; Stress = o.Stress; Pitch = o.Pitch;
        Rate = o.Rate; Contour = o.Contour; ContourSemitones = o.ContourSemitones;
        PitchRange = o.PitchRange; Volume = o.Volume; Energy = o.Energy;
        PauseBeforeMs = o.PauseBeforeMs; PauseAfterMs = o.PauseAfterMs;
    }
}

internal static class Ids
{
    public static string New() => Guid.NewGuid().ToString("N")[..8];
}
