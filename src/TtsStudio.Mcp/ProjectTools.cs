using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TtsStudio.Core;
using TtsStudio.Core.Engine;
using TtsStudio.Core.Model;

namespace TtsStudio.Mcp;

/// <summary>MCP tools over .ttsproj files. Each call loads the file, applies the change and saves it.</summary>
[McpServerToolType]
public sealed class ProjectTools(EngineService engine)
{
    private const string PathDesc = "Path to the .ttsproj file (absolute, or relative to the working directory).";
    private const string SampleDesc = "Sample id or name.";

    // ------------------------------------------------------------------ reference

    [McpServerTool(Name = "get_guide", ReadOnly = true, Idempotent = true)]
    [Description("Reference for region properties, the phoneme set used for pronunciations, the quick markup syntax and the suggested workflow.")]
    public static string GetGuide() => Guide.Full;

    [McpServerTool(Name = "list_voices", ReadOnly = true, Idempotent = true)]
    [Description("Lists the available English voices.")]
    public static string ListVoices() => Json(Voices.All.Select(v => new { v.Id, v.Name, v.Accent, v.Gender }));

    // ------------------------------------------------------------------ project

    [McpServerTool(Name = "create_project")]
    [Description("Creates a new, empty project file.")]
    public static string CreateProject(
        [Description(PathDesc)] string path,
        [Description("Project name.")] string? name = null,
        [Description("Default voice id (see list_voices).")] string? defaultVoice = null,
        [Description("Overwrite an existing file.")] bool overwrite = false)
    {
        string full = FullPath(path);
        if (File.Exists(full) && !overwrite) throw new McpException($"{full} already exists (pass overwrite=true to replace it).");
        var project = new Project { Name = name ?? Path.GetFileNameWithoutExtension(full) };
        if (defaultVoice is not null) project.DefaultVoice = CheckVoice(defaultVoice);
        ProjectFile.Save(project, full);
        return Json(ProjectSummary(project, full));
    }

    [McpServerTool(Name = "get_project", ReadOnly = true, Idempotent = true)]
    [Description("Project settings, lexicon and a list of samples (id, name, text, region count).")]
    public static string GetProject([Description(PathDesc)] string path)
    {
        var (project, full) = Load(path);
        return Json(ProjectSummary(project, full));
    }

    [McpServerTool(Name = "update_project_settings")]
    [Description("Changes project-wide settings. Omitted parameters are left unchanged.")]
    public static string UpdateProjectSettings(
        [Description(PathDesc)] string path,
        string? name = null,
        [Description("Default voice id.")] string? defaultVoice = null,
        [Description("Default speed (1 = normal).")] double? defaultSpeed = null,
        [Description("Folder for export_all, relative to the project file.")] string? exportFolder = null,
        [Description("wav, flac, ogg or mp3.")] string? exportFormat = null)
    {
        return Edit(path, (project, full) =>
        {
            if (name is not null) project.Name = name;
            if (defaultVoice is not null) project.DefaultVoice = CheckVoice(defaultVoice);
            if (defaultSpeed is not null) project.DefaultSpeed = CheckRange("defaultSpeed", defaultSpeed.Value, 0.3, 3);
            if (exportFolder is not null) project.ExportFolder = exportFolder;
            if (exportFormat is not null) project.ExportFormat = Format(exportFormat);
            return ProjectSummary(project, full);
        });
    }

    [McpServerTool(Name = "set_lexicon_entry")]
    [Description("Adds or replaces a project-wide pronunciation: every occurrence of the word (case-insensitive) uses these phonemes unless a region overrides it.")]
    public static string SetLexiconEntry([Description(PathDesc)] string path, string word, [Description("Phonemes, e.g. wˈɪn.")] string phonemes)
    {
        return Edit(path, (project, _) =>
        {
            var entry = project.Lexicon.FirstOrDefault(e => string.Equals(e.Word, word, StringComparison.OrdinalIgnoreCase));
            if (entry is null) project.Lexicon.Add(entry = new LexiconEntry { Word = word.Trim() });
            entry.Phonemes = phonemes.Trim().Trim('/');
            return project.Lexicon;
        });
    }

    [McpServerTool(Name = "remove_lexicon_entry", Destructive = true)]
    [Description("Removes a project-wide pronunciation.")]
    public static string RemoveLexiconEntry([Description(PathDesc)] string path, string word)
    {
        return Edit(path, (project, _) =>
        {
            var entry = project.Lexicon.FirstOrDefault(e => string.Equals(e.Word, word, StringComparison.OrdinalIgnoreCase))
                        ?? throw new McpException($"'{word}' is not in the lexicon.");
            project.Lexicon.Remove(entry);
            return project.Lexicon;
        });
    }

    // ------------------------------------------------------------------ samples

    [McpServerTool(Name = "get_sample", ReadOnly = true, Idempotent = true)]
    [Description("A sample's text, settings and regions. Each region lists the text it covers and a summary of its properties.")]
    public static string GetSample([Description(PathDesc)] string path, [Description(SampleDesc)] string sample)
    {
        var (project, _) = Load(path);
        return Json(SampleDetail(FindSample(project, sample)));
    }

    [McpServerTool(Name = "add_sample")]
    [Description("Adds a sample. Give plain `text`, or `markup` to create text and regions in one go (see get_guide), e.g. \"I didn't say *she* stole it.\".")]
    public static string AddSample(
        [Description(PathDesc)] string path,
        string name,
        [Description("Plain text to speak.")] string? text = null,
        [Description("Annotated markup; alternative to text.")] string? markup = null,
        [Description("Voice id; omit for the project default.")] string? voice = null,
        [Description("Speed multiplier; omit for the project default.")] double? speed = null,
        [Description("Overall pitch shift in semitones.")] double? pitch = null,
        [Description("Overall pitch range (0 monotone, 1 natural).")] double? pitchRange = null,
        [Description("Export file name without extension; defaults to the name.")] string? fileName = null,
        string? notes = null,
        [Description("Recording of someone reading the text (wav, flac, ogg or mp3; absolute, or relative to the working directory). The speech follows its timing and intonation; regions still apply on top. Only used on single-line text. Empty string removes it.")] string? referenceAudio = null,
        [Description("How closely to follow the recording: 0 ignores it, 1 (the default) follows it fully.")] double? referenceStrength = null,
        [Description("Insert at this index (default: end).")] int? index = null)
    {
        return Edit(path, (project, full) =>
        {
            var s = new Sample { Name = name, FileName = fileName, Notes = notes };
            SetContent(s, text, markup);
            ApplySampleSettings(s, voice, speed, pitch, pitchRange);
            ApplyReference(s, full, referenceAudio, referenceStrength);
            project.Samples.Insert(Math.Clamp(index ?? project.Samples.Count, 0, project.Samples.Count), s);
            return SampleDetail(s);
        });
    }

    [McpServerTool(Name = "update_sample")]
    [Description("Changes a sample. Omitted parameters are left unchanged. A new `text` keeps existing regions attached to the words they covered (regions whose words are deleted are removed); `markup` replaces the text and all regions.")]
    public static string UpdateSample(
        [Description(PathDesc)] string path,
        [Description(SampleDesc)] string sample,
        string? name = null,
        string? text = null,
        string? markup = null,
        [Description("Voice id; empty string for the project default.")] string? voice = null,
        double? speed = null,
        double? pitch = null,
        double? pitchRange = null,
        string? fileName = null,
        string? notes = null,
        [Description("Recording of someone reading the text (wav, flac, ogg or mp3; absolute, or relative to the working directory). The speech follows its timing and intonation; regions still apply on top. Only used on single-line text. Empty string removes it.")] string? referenceAudio = null,
        [Description("How closely to follow the recording: 0 ignores it, 1 (the default) follows it fully.")] double? referenceStrength = null)
    {
        return Edit(path, (project, full) =>
        {
            var s = FindSample(project, sample);
            if (name is not null) s.Name = name;
            if (fileName is not null) s.FileName = fileName.Length == 0 ? null : fileName;
            if (notes is not null) s.Notes = notes;
            List<object>? removed = null;
            if (markup is not null) SetContent(s, null, markup);
            else if (text is not null) removed = s.ReplaceText(text).Select(r => (object)new { r.Id, summary = RegionTools.Summary(r) }).ToList();
            ApplySampleSettings(s, voice, speed, pitch, pitchRange);
            ApplyReference(s, full, referenceAudio, referenceStrength);
            return new { sample = SampleDetail(s), removedRegions = removed };
        });
    }

    [McpServerTool(Name = "delete_sample", Destructive = true)]
    [Description("Deletes a sample.")]
    public static string DeleteSample([Description(PathDesc)] string path, [Description(SampleDesc)] string sample)
    {
        return Edit(path, (project, full) =>
        {
            project.Samples.Remove(FindSample(project, sample));
            return ProjectSummary(project, full);
        });
    }

    // ------------------------------------------------------------------ regions

    [McpServerTool(Name = "add_region")]
    [Description("Highlights part of a sample's text and gives it speech properties. Locate it with `match` (text to find, optionally the n-th `occurrence`) or with character offsets `start`/`end`. Regions may overlap other regions.")]
    public static string AddRegion(
        [Description(PathDesc)] string path,
        [Description(SampleDesc)] string sample,
        [Description("Text to find in the sample.")] string? match = null,
        [Description("Which occurrence of `match` (1-based).")] int occurrence = 1,
        [Description("Start offset (inclusive), if not using match.")] int? start = null,
        [Description("End offset (exclusive), if not using match.")] int? end = null,
        [Description("Expand the range to whole words.")] bool snapToWords = true,
        string? label = null,
        [Description("Phonemes for the covered word(s).")] string? pronunciation = null,
        [Description("-2, -1, +1 or +2.")] double? stress = null,
        [Description("Semitones.")] double? pitch = null,
        [Description("Speaking rate, >1 faster.")] double? rate = null,
        [Description("rise or fall.")] string? contour = null,
        double? contourSemitones = null,
        [Description("0 monotone .. 2 animated.")] double? pitchRange = null,
        [Description("dB.")] double? volume = null,
        [Description("Experimental, -3..3.")] double? energy = null,
        double? pauseBeforeMs = null,
        double? pauseAfterMs = null)
    {
        return Edit(path, (project, _) =>
        {
            var s = FindSample(project, sample);
            var (a, b) = Locate(s, match, occurrence, start, end);
            if (snapToWords) (a, b) = RegionTools.SnapToWords(s.Text, a, b);
            if (b <= a) throw new McpException("The region would be empty.");
            var r = new Region { Start = a, End = b };
            SetRegionProperties(r, label, pronunciation, stress, pitch, rate, contour, contourSemitones, pitchRange, volume, energy, pauseBeforeMs, pauseAfterMs);
            s.Regions.Add(r);
            return RegionDetail(s, r);
        });
    }

    [McpServerTool(Name = "update_region")]
    [Description("Changes a region's range or properties. Omitted parameters are left unchanged; list property names in `clear` to unset them (e.g. [\"pitch\", \"rate\"]).")]
    public static string UpdateRegion(
        [Description(PathDesc)] string path,
        [Description(SampleDesc)] string sample,
        string regionId,
        int? start = null,
        int? end = null,
        string? label = null,
        string? pronunciation = null,
        double? stress = null,
        double? pitch = null,
        double? rate = null,
        string? contour = null,
        double? contourSemitones = null,
        double? pitchRange = null,
        double? volume = null,
        double? energy = null,
        double? pauseBeforeMs = null,
        double? pauseAfterMs = null,
        [Description("Property names to unset.")] string[]? clear = null)
    {
        return Edit(path, (project, _) =>
        {
            var s = FindSample(project, sample);
            var r = FindRegion(s, regionId);
            int a = start ?? r.Start, b = end ?? r.End;
            if (a < 0 || b > s.Text.Length || b <= a) throw new McpException($"Invalid range [{a}, {b}) for text of length {s.Text.Length}.");
            (r.Start, r.End) = (a, b);
            foreach (var prop in clear ?? []) ClearProperty(r, prop);
            SetRegionProperties(r, label, pronunciation, stress, pitch, rate, contour, contourSemitones, pitchRange, volume, energy, pauseBeforeMs, pauseAfterMs);
            return RegionDetail(s, r);
        });
    }

    [McpServerTool(Name = "delete_region", Destructive = true)]
    [Description("Deletes a region.")]
    public static string DeleteRegion([Description(PathDesc)] string path, [Description(SampleDesc)] string sample, string regionId)
    {
        return Edit(path, (project, _) =>
        {
            var s = FindSample(project, sample);
            s.Regions.Remove(FindRegion(s, regionId));
            return SampleDetail(s);
        });
    }

    // ------------------------------------------------------------------ engine

    [McpServerTool(Name = "phonemize", ReadOnly = true, Idempotent = true)]
    [Description("Shows how the engine would pronounce plain text, word by word (phonemes and where they came from: gold/silver = dictionary, lexicon, guessed = not in the dictionary). Use it to find phonemes for pronunciation fixes.")]
    public async Task<string> Phonemize(
        string text,
        [Description("Voice id (American and British voices differ).")] string? voice = null,
        [Description("Optional project whose lexicon to apply.")] string? path = null,
        CancellationToken ct = default)
    {
        var lexicon = path is null ? [] : Load(path).Project.LexiconMap();
        var client = await engine.GetAsync(ct);
        var result = await client.PhonemizeAsync(text, voice is null ? Voices.Default : CheckVoice(voice), lexicon, ct);
        return Json(result);
    }

    [McpServerTool(Name = "render_sample")]
    [Description("Synthesises a sample to an audio file and reports the phonemes and timing of every word plus warnings (e.g. guessed pronunciations). Default output is the sample's export path.")]
    public async Task<string> RenderSample(
        [Description(PathDesc)] string path,
        [Description(SampleDesc)] string sample,
        [Description("Output file; the extension picks the format.")] string? output = null,
        [Description("wav, flac, ogg or mp3 (when output is omitted).")] string? format = null,
        CancellationToken ct = default)
    {
        var (project, full) = Load(path);
        var s = FindSample(project, sample);
        if (string.IsNullOrWhiteSpace(s.Text)) throw new McpException("The sample has no text.");
        string outPath = output is not null ? FullPath(output) : Exporter.PlanExport(project, full, format is null ? null : Format(format))[s];
        var client = await engine.GetAsync(ct);
        var r = await client.SynthesizeAsync(SynthesisRequest.For(project, s, outPath, projectPath: full), ct);
        return Json(new
        {
            output = r.Output,
            duration = Math.Round(r.Duration, 2),
            synthSeconds = Math.Round(r.SynthSeconds, 2),
            r.Warnings,
            r.Phonemes,
            words = r.Tokens.Where(t => t.Source != "punct").Select(t => new { t.Text, t.Phonemes, t.Source, t.Start, t.End, t.CharStart, t.Controls }),
        });
    }

    [McpServerTool(Name = "record_reading")]
    [Description("Opens TTS Studio's voice recorder on the user's screen, showing the sample's text with record, stop, play-back and keep controls, so the user can record themselves reading it. " +
                 "Waits until they keep a take or cancel (this can take a few minutes; tell the user to look for the recorder window). " +
                 "A kept take is saved as a WAV and attached as the sample's referenceAudio, so the speech follows its timing and intonation. Only used on single-line samples.")]
    public async Task<string> RecordReading(
        [Description(PathDesc)] string path,
        [Description(SampleDesc)] string sample,
        [Description("Where to save the WAV; default recordings/<sample name>.wav next to the project (numbered if taken).")] string? output = null,
        [Description("How closely to follow the recording, 0..1 (default: keep the sample's setting, initially 1).")] double? referenceStrength = null,
        CancellationToken ct = default)
    {
        var (project, full) = Load(path);
        var s = FindSample(project, sample);
        if (string.IsNullOrWhiteSpace(s.Text)) throw new McpException("The sample has no text to read.");
        string app = Path.Combine(AppContext.BaseDirectory, "TtsStudio.exe");
        if (!File.Exists(app))
            throw new McpException("The recorder is part of the TTS Studio app. Run this MCP server from the app's folder (TtsStudio.Mcp.exe next to TtsStudio.exe).");
        string wav = output is not null ? FullPath(output) : ReferenceRecordings.NewRecordingPath(full, s.Name);
        if (!wav.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) throw new McpException("The recording is saved as WAV; give an output path ending in .wav.");

        string dir = Path.Combine(Path.GetTempPath(), "TtsStudio");
        Directory.CreateDirectory(dir);
        string requestPath = Path.Combine(dir, $"record-{Guid.NewGuid():N}.json");
        new RecordingRequest { Text = s.Text, Title = s.Name, Output = wav }.Save(requestPath);
        RecordingResult? result;
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(app) { ArgumentList = { RecordingRequest.Switch, requestPath } })
                                ?? throw new McpException("Could not start the TTS Studio recorder.");
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                process.Kill();
                throw;
            }
            result = RecordingResult.Load(RecordingRequest.ResultPath(requestPath));
        }
        finally
        {
            File.Delete(requestPath);
            File.Delete(RecordingRequest.ResultPath(requestPath));
        }

        if (result is null) throw new McpException("The recorder closed without reporting a result.");
        if (result.Outcome == "error") throw new McpException("The recorder failed: " + result.Error);
        if (result.Outcome != "saved" || !File.Exists(wav)) return Json(new { outcome = "cancelled", message = "The user closed the recorder without keeping a take." });

        // Re-load: the project may have changed (e.g. in the app) while the user was recording.
        return Edit(path, (p, f) =>
        {
            var target = FindSample(p, s.Id);
            ApplyReference(target, f, wav, referenceStrength);
            return new { outcome = "saved", recording = wav, seconds = result.Seconds, sample = SampleDetail(target) };
        });
    }

    [McpServerTool(Name = "export_all")]
    [Description("Renders every sample into the project's export folder.")]
    public async Task<string> ExportAll(
        [Description(PathDesc)] string path,
        [Description("wav, flac, ogg or mp3; defaults to the project's export format.")] string? format = null,
        CancellationToken ct = default)
    {
        var (project, full) = Load(path);
        var client = await engine.GetAsync(ct);
        var results = await Exporter.ExportAllAsync(client, project, full, format is null ? null : Format(format), ct: ct);
        return Json(results.Select(x => new { sample = x.Sample.Name, file = x.Path, duration = Math.Round(x.Result.Duration, 2), x.Result.Warnings }));
    }

    // ------------------------------------------------------------------ helpers

    private static string Json(object? value) => JsonSerializer.Serialize(value, ProjectFile.JsonOptions);

    private static string FullPath(string path) => Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));

    private static (Project Project, string FullPath) Load(string path)
    {
        string full = FullPath(path);
        if (!File.Exists(full)) throw new McpException($"Project file not found: {full}");
        try
        {
            return (ProjectFile.Load(full), full);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        {
            throw new McpException($"Could not read {full}: {e.Message}");
        }
    }

    private static string Edit(string path, Func<Project, string, object?> change)
    {
        var (project, full) = Load(path);
        object? result;
        try
        {
            result = change(project, full);
        }
        catch (MarkupException e)
        {
            throw new McpException("Markup error: " + e.Message);
        }
        ProjectFile.Save(project, full);
        return Json(result);
    }

    private static Sample FindSample(Project p, string idOrName) =>
        p.FindSample(idOrName) ?? throw new McpException(
            $"No sample '{idOrName}'. Samples: {string.Join(", ", p.Samples.Select(s => $"{s.Name} ({s.Id})"))}");

    private static Region FindRegion(Sample s, string id) =>
        s.Regions.FirstOrDefault(r => r.Id == id) ?? throw new McpException(
            $"No region '{id}' in sample '{s.Name}'. Regions: {string.Join(", ", s.Regions.Select(r => r.Id))}");

    private static string CheckVoice(string voice) =>
        Voices.IsKnown(voice) ? voice : throw new McpException($"Unknown voice '{voice}'. Use list_voices.");

    private static string Format(string format)
    {
        try { return Exporter.NormalizeFormat(format); }
        catch (ArgumentException e) { throw new McpException(e.Message); }
    }

    private static double CheckRange(string name, double v, double min, double max) =>
        v >= min && v <= max ? v : throw new McpException($"{name} must be between {min} and {max}.");

    private static void SetContent(Sample s, string? text, string? markup)
    {
        if (markup is not null)
        {
            var (t, regions) = Markup.Parse(markup);
            s.Regions.Clear();
            s.Text = t;
            foreach (var r in regions) s.Regions.Add(r);
        }
        else if (text is not null)
        {
            s.Regions.Clear();
            s.Text = text;
        }
    }

    private static void ApplySampleSettings(Sample s, string? voice, double? speed, double? pitch, double? pitchRange)
    {
        if (voice is not null) s.Voice = voice.Length == 0 ? null : CheckVoice(voice);
        if (speed is not null) s.Speed = CheckRange("speed", speed.Value, 0.3, 3);
        if (pitch is not null) s.Pitch = CheckRange("pitch", pitch.Value, -12, 12);
        if (pitchRange is not null) s.PitchRange = CheckRange("pitchRange", pitchRange.Value, 0, 3);
    }

    private static void ApplyReference(Sample s, string projectPath, string? referenceAudio, double? referenceStrength)
    {
        if (referenceAudio is not null)
        {
            if (referenceAudio.Length == 0) s.ReferenceAudio = null;
            else
            {
                string file = FullPath(referenceAudio);
                if (!File.Exists(file)) throw new McpException($"Recording not found: {file}");
                if (!ReferenceRecordings.IsSupported(file))
                    throw new McpException($"The engine reads {string.Join(", ", ReferenceRecordings.Formats)} recordings. " +
                                           "Convert it first (the TTS Studio app converts m4a and similar when you attach them there).");
                s.ReferenceAudio = ReferenceRecordings.Store(file, projectPath);
            }
        }
        if (referenceStrength is not null) s.ReferenceStrength = CheckRange("referenceStrength", referenceStrength.Value, 0, 1);
    }

    private static (int, int) Locate(Sample s, string? match, int occurrence, int? start, int? end)
    {
        if (!string.IsNullOrEmpty(match))
        {
            // Exact case if it occurs that way, otherwise case-insensitive.
            var comparison = s.Text.Contains(match, StringComparison.Ordinal) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int at = -1;
            for (int n = 0; n < Math.Max(1, occurrence); n++)
            {
                at = s.Text.IndexOf(match, at + 1, comparison);
                if (at < 0) throw new McpException($"'{match}' (occurrence {occurrence}) not found in the text of '{s.Name}'.");
            }
            return (at, at + match.Length);
        }
        if (start is null || end is null) throw new McpException("Give either match, or both start and end.");
        if (start < 0 || end > s.Text.Length || end <= start)
            throw new McpException($"Invalid range [{start}, {end}) for text of length {s.Text.Length}.");
        return (start.Value, end.Value);
    }

    private static void SetRegionProperties(Region r, string? label, string? pronunciation, double? stress, double? pitch,
        double? rate, string? contour, double? contourSemitones, double? pitchRange, double? volume, double? energy,
        double? pauseBeforeMs, double? pauseAfterMs)
    {
        if (label is not null) r.Label = label.Length == 0 ? null : label;
        if (pronunciation is not null) r.Pronunciation = pronunciation.Trim().Trim('/') is { Length: > 0 } ps ? ps : null;
        if (stress is not null) r.Stress = CheckRange("stress", stress.Value, -2, 2);
        if (pitch is not null) r.Pitch = CheckRange("pitch", pitch.Value, -24, 24);
        if (rate is not null) r.Rate = CheckRange("rate", rate.Value, 0.1, 5);
        if (contour is not null)
        {
            string c = contour.Trim().ToLowerInvariant();
            r.Contour = c switch
            {
                "rise" or "fall" => c,
                "" or "none" => null,
                _ => throw new McpException("contour must be rise, fall or none."),
            };
        }
        if (contourSemitones is not null) r.ContourSemitones = CheckRange("contourSemitones", contourSemitones.Value, 0, 24);
        if (pitchRange is not null) r.PitchRange = CheckRange("pitchRange", pitchRange.Value, 0, 4);
        if (volume is not null) r.Volume = CheckRange("volume", volume.Value, -40, 20);
        if (energy is not null) r.Energy = CheckRange("energy", energy.Value, -5, 5);
        if (pauseBeforeMs is not null) r.PauseBeforeMs = CheckRange("pauseBeforeMs", pauseBeforeMs.Value, 0, 10000);
        if (pauseAfterMs is not null) r.PauseAfterMs = CheckRange("pauseAfterMs", pauseAfterMs.Value, 0, 10000);
    }

    private static void ClearProperty(Region r, string name)
    {
        switch (name.Trim().ToLowerInvariant())
        {
            case "label": r.Label = null; break;
            case "pronunciation": r.Pronunciation = null; break;
            case "stress": r.Stress = null; break;
            case "pitch": r.Pitch = null; break;
            case "rate": r.Rate = null; break;
            case "contour": r.Contour = null; r.ContourSemitones = null; break;
            case "contoursemitones": r.ContourSemitones = null; break;
            case "pitchrange": r.PitchRange = null; break;
            case "volume": r.Volume = null; break;
            case "energy": r.Energy = null; break;
            case "pausebeforems": r.PauseBeforeMs = null; break;
            case "pauseafterms": r.PauseAfterMs = null; break;
            default: throw new McpException($"Unknown property '{name}'.");
        }
    }

    private static object ProjectSummary(Project p, string full) => new
    {
        path = full,
        p.Name,
        p.DefaultVoice,
        p.DefaultSpeed,
        p.ExportFolder,
        p.ExportFormat,
        lexicon = p.Lexicon,
        samples = p.Samples.Select(s => new { s.Id, s.Name, s.Text, s.Voice, regions = s.Regions.Count }),
    };

    private static object SampleDetail(Sample s) => new
    {
        s.Id,
        s.Name,
        s.FileName,
        s.Text,
        s.Voice,
        s.Speed,
        s.Pitch,
        s.PitchRange,
        s.Notes,
        s.ReferenceAudio,
        referenceStrength = s.ReferenceAudio is null ? (double?)null : s.ReferenceStrength,
        regions = s.Regions.OrderBy(r => r.Start).Select(r => RegionDetail(s, r)),
    };

    private static object RegionDetail(Sample s, Region r) => new
    {
        r.Id,
        r.Start,
        r.End,
        text = s.TextOf(r),
        summary = RegionTools.Summary(r),
        r.Label,
        r.Pronunciation,
        r.Stress,
        r.Pitch,
        r.Rate,
        r.Contour,
        r.ContourSemitones,
        r.PitchRange,
        r.Volume,
        r.Energy,
        r.PauseBeforeMs,
        r.PauseAfterMs,
    };
}
