using TtsStudio.Core;
using TtsStudio.Core.Model;

namespace TtsStudio.App.ViewModels;

public sealed record PresetInfo(string Key, string Name, string Hint);

/// <summary>One-click speech styles for new or selected regions. Each sets only the properties it names.</summary>
public static class Presets
{
    public static IReadOnlyList<PresetInfo> All { get; } =
    [
        new("emphasis", "Emphasise", "Higher, slower and a little louder"),
        new("rise", "Rising", "Pitch glides up, as at the end of a question"),
        new("fall", "Falling", "Pitch glides down, as at the end of a statement"),
        new("slower", "Slower", "Speak this part more slowly"),
        new("faster", "Faster", "Speak this part more quickly"),
        new("louder", "Louder", "Raise the volume"),
        new("softer", "Softer", "Quieter and less energetic"),
        new("higher", "Higher", "Raise the pitch"),
        new("lower", "Lower", "Lower the pitch, as for an aside"),
        new("monotone", "Flat", "Remove most pitch movement"),
        new("animated", "Animated", "Exaggerate pitch movement"),
        new("pause", "Pause after", "Insert a short silence after this part"),
        new("pronunciation", "Pronunciation", "Spell out how the word(s) should be said"),
    ];

    public static void Apply(Region r, string key)
    {
        switch (key)
        {
            case "emphasis":
                r.Pitch = Markup.EmphasisPitch;
                r.Rate = Markup.EmphasisRate;
                r.Volume = Markup.EmphasisVolume;
                r.Label ??= "emphasis";
                break;
            case "rise": r.Contour = "rise"; r.ContourSemitones = 5; break;
            case "fall": r.Contour = "fall"; r.ContourSemitones = 4; break;
            case "slower": r.Rate = 0.8; break;
            case "faster": r.Rate = 1.25; break;
            case "louder": r.Volume = 4; break;
            case "softer": r.Volume = -6; r.Energy = -1; break;
            case "higher": r.Pitch = 3; break;
            case "lower": r.Pitch = -3; break;
            case "monotone": r.PitchRange = 0.2; break;
            case "animated": r.PitchRange = 1.7; break;
            case "pause": r.PauseAfterMs = 400; break;
        }
    }
}
