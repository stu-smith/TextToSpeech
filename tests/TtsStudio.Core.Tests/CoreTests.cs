using TtsStudio.Core;
using TtsStudio.Core.Engine;
using TtsStudio.Core.Model;

namespace TtsStudio.Core.Tests;

public class TextEditTests
{
    private static Sample Make(string text, params (int, int)[] ranges)
    {
        var s = new Sample { Text = text };
        foreach (var (a, b) in ranges) s.Regions.Add(new Region { Start = a, End = b });
        return s;
    }

    private static string[] Covered(Sample s) => s.Regions.Select(s.TextOf).ToArray();

    [Fact]
    public void Insertions_before_after_and_inside_a_region()
    {
        var s = Make("abc DEF ghi", (4, 7));
        s.ReplaceText("xx abc DEF ghi");
        Assert.Equal(["DEF"], Covered(s));
        s.ReplaceText("xx abc DEFINE ghi");
        Assert.Equal(["DEFINE"], Covered(s));
        s.ReplaceText("xx abc DEFINE! ghi"); // typed right after the region's end: not absorbed
        Assert.Equal(["DEFINE"], Covered(s));
    }

    [Fact]
    public void Typing_a_new_word_at_region_start_does_not_extend_it_but_replacing_its_text_keeps_it()
    {
        var s = Make("say DEF", (4, 7));
        s.ReplaceText("say new DEF");
        Assert.Equal(["DEF"], Covered(s));
        s.ReplaceText("say new unDEF");
        Assert.Equal(["unDEF"], Covered(s));
        s.ReplaceText("say new DEF");
        TextEdits.Apply(8, 3, 3, s.Regions);
        s.Text = "say new XYZ";
        Assert.Equal(["XYZ"], Covered(s));
    }

    [Fact]
    public void Separate_edits_in_one_update_each_keep_their_regions()
    {
        var s = Make("The cat sat on the mat today.", (4, 7), (12, 14), (19, 22));
        s.ReplaceText("A big cat sat quietly on the red mat.");
        Assert.Equal(["cat", "on", "mat"], Covered(s));
    }

    [Fact]
    public void Partial_and_full_deletions()
    {
        var s = Make("abc DEF ghi", (4, 7), (0, 4));
        TextEdits.Apply(2, 4, 1, s.Regions); // "c DE" -> "X"
        s.Text = "abXF ghi";
        Assert.Equal(["F", "ab"], Covered(s));

        var gone = s.ReplaceText(" ghi");
        Assert.Equal(2, gone.Count);
        Assert.Empty(s.Regions);
    }
}

public class MarkupTests
{
    [Fact]
    public void Imports_links_emphasis_spans_and_pauses()
    {
        var (text, regions) = Markup.Parse("Well... {pause 400} I *didn't* say {she [stole](/stˈOl/) | p=+3 c=rise:6}.");
        Assert.Equal("Well... I didn't say she stole.", text);

        string Of(Region r) => text[r.Start..r.End];
        var pause = Assert.Single(regions, r => r.PauseAfterMs is not null);
        Assert.Equal(("Well...", 400.0), (Of(pause), pause.PauseAfterMs!.Value));
        var emph = Assert.Single(regions, r => r.Label == "emphasis");
        Assert.Equal("didn't", Of(emph));
        var span = Assert.Single(regions, r => r.Pitch == 3);
        Assert.Equal(("she stole", "rise", 6.0), (Of(span), span.Contour, span.ContourSemitones!.Value));
        var link = Assert.Single(regions, r => r.Pronunciation is not null);
        Assert.Equal(("stole", "stˈOl"), (Of(link), link.Pronunciation));
    }

    [Theory]
    [InlineData("{oops}")]
    [InlineData("*open")]
    [InlineData("{x | q=1}")]
    [InlineData("a } b")]
    public void Reports_errors(string bad) => Assert.Throws<MarkupException>(() => Markup.Parse(bad));
}

public class RegionToolsTests
{
    [Fact]
    public void Overlapping_regions_get_separate_lanes()
    {
        Region a = new() { Start = 0, End = 10 }, b = new() { Start = 5, End = 15 }, c = new() { Start = 10, End = 12 }, d = new() { Start = 6, End = 8 };
        var lanes = RegionTools.AssignLanes([a, b, c, d]);
        Assert.Equal(0, lanes[a]);
        Assert.Equal(1, lanes[b]);
        Assert.Equal(0, lanes[c]);
        Assert.Equal(2, lanes[d]);
    }

    [Theory]
    [InlineData("I didn't say", 3, 5, "didn't")]
    [InlineData("I didn't say", 1, 4, "didn't")]
    [InlineData("an IOSS return", 4, 6, "IOSS")]
    [InlineData("well-known fact", 6, 7, "well-known")]
    public void Snaps_to_words(string text, int a, int b, string expected)
    {
        var (s, e) = RegionTools.SnapToWords(text, a, b);
        Assert.Equal(expected, text[s..e]);
    }

    [Fact]
    public void Summaries() =>
        Assert.Equal("/ˈIɑs/ · pitch +2 · rise 6 · pause 300ms after",
            RegionTools.Summary(new Region { Pronunciation = "ˈIɑs", Pitch = 2, Contour = "rise", ContourSemitones = 6, PauseAfterMs = 300 }));
}

public class ProjectFileTests
{
    [Fact]
    public void Round_trips_and_keeps_ipa_readable()
    {
        var p = new Project { Name = "Demo" };
        p.Lexicon.Add(new LexiconEntry { Word = "Nguyen", Phonemes = "/wˈɪn/" });
        var s = new Sample { Name = "One", Text = "Generate an IOSS return" };
        s.Regions.Add(new Region { Start = 12, End = 16, Pronunciation = "ˈIɑs" });
        p.Samples.Add(s);

        string json = ProjectFile.Serialize(p);
        Assert.Contains("\"pronunciation\": \"ˈIɑs\"", json);
        Assert.DoesNotContain("\"pitch\": null", json);

        var back = ProjectFile.Parse(json);
        Assert.Equal("ˈIɑs", back.Samples[0].Regions[0].Pronunciation);
        Assert.Equal("wˈɪn", back.LexiconMap()["nguyen"]);
    }

    [Fact]
    public void Normalizes_bad_regions_and_rejects_other_formats()
    {
        const string json = """
            { "format": "tts-studio-project", "version": 1, "samples": [
              { "id": "a", "text": "hello", "regions": [ { "id": "r", "start": 2, "end": 99 }, { "id": "r", "start": 4, "end": 1 } ] },
              { "id": "a", "text": "x" } ] }
            """;
        var p = ProjectFile.Parse(json);
        Assert.NotEqual(p.Samples[0].Id, p.Samples[1].Id);
        var r = Assert.Single(p.Samples[0].Regions);
        Assert.Equal((2, 5), (r.Start, r.End));
        Assert.Throws<InvalidDataException>(() => ProjectFile.Parse("""{ "format": "other" }"""));
    }

    [Fact]
    public void Export_plan_makes_names_unique()
    {
        var p = new Project { ExportFolder = "out", ExportFormat = "mp3" };
        p.Samples.Add(new Sample { Name = "Intro: hello?" });
        p.Samples.Add(new Sample { Name = "Intro: hello?" });
        var plan = Exporter.PlanExport(p, @"C:\proj\demo.ttsproj").Values.ToList();
        Assert.Equal([@"C:\proj\out\Intro_ hello_.mp3", @"C:\proj\out\Intro_ hello_ (2).mp3"], plan);
    }

    [Fact]
    public void Request_content_key_tracks_changes()
    {
        var p = new Project();
        var s = new Sample { Text = "Hello" };
        p.Samples.Add(s);
        string k1 = SynthesisRequest.For(p, s, "a.wav").ContentKey();
        Assert.Equal(k1, SynthesisRequest.For(p, s, "b.wav").ContentKey());
        s.Regions.Add(new Region { Start = 0, End = 5, Pitch = 1 });
        Assert.NotEqual(k1, SynthesisRequest.For(p, s, "a.wav").ContentKey());
    }
}
