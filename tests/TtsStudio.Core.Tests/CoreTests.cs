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

public class UvLockTests
{
    private static string LockText => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "engine", "uv.lock"));

    [Theory]
    [InlineData("torch-2.15.0+cpu-cp312-cp312-win_amd64.whl", 23)]
    [InlineData("pydantic_core-2.0-cp312-abi3-win_amd64.whl", 22)]
    [InlineData("six-1.17.0-py2.py3-none-any.whl", 11)]
    [InlineData("torch-2.15.0-cp312-cp312-macosx_14_0_arm64.whl", 0)]
    [InlineData("numpy-2.5.4-cp313-cp313-win_amd64.whl", 0)]
    [InlineData("thing-1.0-cp39-abi3-win_amd64.whl", 22)]
    public void Wheel_tags_are_scored_for_windows_x64_cpython_312(string file, int score) =>
        Assert.Equal(score, UvLock.WheelScore(file));

    [Theory]
    [InlineData("sys_platform == 'win32'", true)]
    [InlineData("sys_platform == 'darwin'", false)]
    [InlineData("sys_platform != 'darwin'", true)]
    [InlineData("platform_machine == 'AMD64' or platform_machine == 'arm64'", true)]
    [InlineData("python_full_version >= '3.12'", true)]
    public void Markers_are_evaluated_for_windows(string marker, bool applies) =>
        Assert.Equal(applies, UvLock.MarkerApplies(marker));

    [Fact]
    public void Runtime_files_come_from_the_engine_lock()
    {
        var files = UvLock.RequiredFiles(LockText, "ttsengine", (_, _) => false);
        var torch = Assert.Single(files, f => f.Name == "torch");
        Assert.Equal("2.15.0+cpu", torch.Version);
        Assert.EndsWith("win_amd64.whl", torch.Url);
        Assert.Contains(files, f => f.Name == "colorama");          // win32-only dependency
        Assert.Contains(files, f => f.Name == "en-core-web-sm");    // direct URL wheel
        Assert.DoesNotContain(files, f => f.Name == "pytest");      // dev group
        Assert.DoesNotContain(files, f => f.Name == "ttsengine");   // the project itself

        var none = UvLock.RequiredFiles(LockText, "ttsengine", (_, _) => true);
        Assert.Empty(none);
    }
}

public class ReferenceRecordingTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "tts-ref-tests");
    private static string In(params string[] parts) => Path.Combine([Root, .. parts]);

    [Fact]
    public void Paths_are_stored_relative_to_the_project_and_resolved_back()
    {
        string project = In("proj", "voice.ttsproj"), recording = In("proj", "takes", "intro.wav");
        string stored = ReferenceRecordings.Store(recording, project);
        Assert.Equal(Path.Combine("takes", "intro.wav"), stored);
        Assert.Equal(recording, ReferenceRecordings.Resolve(stored, project));
        Assert.Equal(recording, ReferenceRecordings.Store(recording, null)); // unsaved project: absolute
        Assert.Null(ReferenceRecordings.Resolve(null, project));
    }

    [Fact]
    public void Save_as_rebases_relative_paths()
    {
        var p = new Project();
        p.Samples.Add(new Sample { Text = "Hi.", ReferenceAudio = Path.Combine("takes", "hi.wav") });
        ReferenceRecordings.Rebase(p, In("a", "x.ttsproj"), In("b", "c", "x.ttsproj"));
        Assert.Equal(Path.Combine("..", "..", "a", "takes", "hi.wav"), p.Samples[0].ReferenceAudio);
        Assert.Equal(In("a", "takes", "hi.wav"), ReferenceRecordings.Resolve(p.Samples[0].ReferenceAudio, In("b", "c", "x.ttsproj")));
    }

    [Fact]
    public void Formats_the_engine_reads()
    {
        Assert.True(ReferenceRecordings.IsSupported("take.WAV"));
        Assert.True(ReferenceRecordings.IsSupported("take.flac"));
        Assert.False(ReferenceRecordings.IsSupported("take.m4a"));
    }

    [Fact]
    public void Loading_cleans_up_reference_settings_and_round_trips()
    {
        string json = """
            {"format":"tts-studio-project","version":1,"samples":[
              {"id":"a","text":"One.","referenceAudio":"take.wav","referenceStrength":7},
              {"id":"b","text":"Two.","referenceAudio":"  ","referenceStrength":-1}]}
            """;
        var p = ProjectFile.Parse(json);
        Assert.Equal(("take.wav", 1.0), (p.Samples[0].ReferenceAudio, p.Samples[0].ReferenceStrength));
        Assert.Equal((null, 0.0), (p.Samples[1].ReferenceAudio, p.Samples[1].ReferenceStrength));
        var again = ProjectFile.Parse(ProjectFile.Serialize(p));
        Assert.Equal("take.wav", again.Samples[0].ReferenceAudio);
    }

    [Fact]
    public void Requests_carry_the_full_path_and_their_key_changes_with_the_recording()
    {
        Directory.CreateDirectory(In("proj"));
        string project = In("proj", "p.ttsproj"), take = In("proj", "take.wav");
        File.WriteAllBytes(take, new byte[100]);
        var p = new Project();
        var s = new Sample { Text = "Hello there.", ReferenceAudio = "take.wav", ReferenceStrength = 0.5 };
        p.Samples.Add(s);

        var req = SynthesisRequest.For(p, s, "out.wav", projectPath: project);
        Assert.Equal((take, 0.5), (req.ReferenceAudio, req.ReferenceStrength));
        string key = req.ContentKey();

        s.ReferenceStrength = 0.75;
        Assert.NotEqual(key, SynthesisRequest.For(p, s, "out.wav", projectPath: project).ContentKey());
        s.ReferenceStrength = 0.5;
        Assert.Equal(key, SynthesisRequest.For(p, s, "out.wav", projectPath: project).ContentKey());

        File.WriteAllBytes(take, new byte[200]); // re-recorded
        Assert.NotEqual(key, SynthesisRequest.For(p, s, "out.wav", projectPath: project).ContentKey());
    }
}

public class RecordingTests
{
    [Fact]
    public void New_takes_go_in_a_recordings_folder_and_are_numbered()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tts-rec-tests", Guid.NewGuid().ToString("N"));
        string project = Path.Combine(dir, "p.ttsproj");
        string first = ReferenceRecordings.NewRecordingPath(project, "Intro: take?");
        Assert.Equal(Path.Combine(dir, "recordings", "Intro_ take_.wav"), first);
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        File.WriteAllBytes(first, [0]);
        Assert.Equal(Path.Combine(dir, "recordings", "Intro_ take_ (2).wav"), ReferenceRecordings.NewRecordingPath(project, "Intro: take?"));
    }

    [Fact]
    public void Requests_and_results_round_trip()
    {
        string path = Path.Combine(Path.GetTempPath(), $"req-{Guid.NewGuid():N}.json");
        new RecordingRequest { Text = "Hello.", Title = "Greeting", Output = @"C:\x\y.wav" }.Save(path);
        var req = RecordingRequest.Load(path);
        Assert.Equal(("Hello.", "Greeting", @"C:\x\y.wav"), (req.Text, req.Title, req.Output));

        Assert.Null(RecordingResult.Load(RecordingRequest.ResultPath(path)));
        new RecordingResult { Outcome = "saved", Output = req.Output, Seconds = 2.5 }.Save(RecordingRequest.ResultPath(path));
        var result = RecordingResult.Load(RecordingRequest.ResultPath(path))!;
        Assert.Equal(("saved", 2.5), (result.Outcome, result.Seconds));
        File.Delete(path);
        File.Delete(RecordingRequest.ResultPath(path));
    }
}
