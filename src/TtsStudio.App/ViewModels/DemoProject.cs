using TtsStudio.Core;
using TtsStudio.Core.Model;

namespace TtsStudio.App.ViewModels;

/// <summary>The example project shown on first run (the examples from the original Kokoro workbench).</summary>
public static class DemoProject
{
    private static readonly (string Name, string Markup)[] Examples =
    [
        ("Shifting emphasis", "I didn't say *she* stole the money.\nI didn't say she *stole* the money.\nI didn't say she stole *the money*."),
        ("Question or statement", "You're leaving {tomorrow | c=rise:6}?\nYou're leaving {tomorrow | c=fall:4}."),
        ("Heteronyms", "The {wind | p=+2} was too strong to [wind](/wˈInd/) the sail. I {live | r=0.8} near the [live](/lˈIv/) venue."),
        ("Pacing and expression", "Well... {pause 400} I suppose {that could work | g=0.3 r=0.9}. {Fantastic | g=1.8 p=+2 v=+2}!"),
        ("Names and places", "[Siobhan](/ʃəvˈɔn/) and Nguyen drove to Worcestershire, then on to Loughborough."),
        ("Acronyms", "Generate an [IOSS](/ˈIɑs/) return before the deadline."),
    ];

    public static Project Create()
    {
        var project = new Project { Name = "Welcome to TTS Studio" };
        project.Lexicon.Add(new LexiconEntry { Word = "Nguyen", Phonemes = "wˈɪn" });
        project.Lexicon.Add(new LexiconEntry { Word = "Loughborough", Phonemes = "lˈʌfbəɹə" });
        foreach (var (name, markup) in Examples)
        {
            var (text, regions) = Markup.Parse(markup);
            var sample = new Sample { Name = name, Text = text };
            foreach (var r in regions) sample.Regions.Add(r);
            project.Samples.Add(sample);
        }

        // Overlapping regions: a slow, low phrase containing a rising word.
        var overlap = new Sample { Name = "Overlapping regions", Text = "This whole sentence is slower, but only the last word rises?" };
        overlap.Regions.Add(new Region { Start = 0, End = overlap.Text.Length - 1, Rate = 0.85, Pitch = -1, Label = "slow, low" });
        int rises = overlap.Text.IndexOf("rises", StringComparison.Ordinal);
        overlap.Regions.Add(new Region { Start = rises, End = rises + 5, Contour = "rise", ContourSemitones = 6 });
        int only = overlap.Text.IndexOf("only the last", StringComparison.Ordinal);
        overlap.Regions.Add(new Region { Start = only, End = only + 13, Volume = 3, Label = "louder" });
        project.Samples.Add(overlap);
        return project;
    }
}
