using TtsStudio.Core.Model;

namespace TtsStudio.Core.Engine;

public sealed class SynthesisRequest
{
    public string Text { get; set; } = "";
    public List<Region> Regions { get; set; } = [];
    public string Voice { get; set; } = Voices.Default;
    public double Speed { get; set; } = 1.0;
    public double Pitch { get; set; }
    public double PitchRange { get; set; } = 1.0;
    public Dictionary<string, string> Lexicon { get; set; } = [];

    /// <summary>Output file; the extension picks the format (wav, flac, ogg, mp3).</summary>
    public string Output { get; set; } = "";

    /// <summary>Optional second render with prosody ignored (pronunciations kept), for A/B listening.</summary>
    public string? BaselineOutput { get; set; }

    public static SynthesisRequest For(Project project, Sample sample, string output, string? baselineOutput = null) => new()
    {
        Text = sample.Text,
        Regions = sample.Regions.ToList(),
        Voice = Voices.IsKnown(sample.Voice) ? sample.Voice! : project.DefaultVoice,
        Speed = sample.Speed ?? project.DefaultSpeed,
        Pitch = sample.Pitch,
        PitchRange = sample.PitchRange,
        Lexicon = project.LexiconMap(),
        Output = output,
        BaselineOutput = baselineOutput,
    };

    /// <summary>Identifies the audio this request produces, so unchanged samples need not be re-rendered.</summary>
    public string ContentKey()
    {
        var copy = new SynthesisRequest
        {
            Text = Text, Regions = Regions, Voice = Voice, Speed = Speed, Pitch = Pitch,
            PitchRange = PitchRange, Lexicon = new(Lexicon.OrderBy(kv => kv.Key)),
        };
        string json = System.Text.Json.JsonSerializer.Serialize(copy, ProjectFile.JsonOptions);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)))[..16];
    }
}

public sealed class SynthesisResult
{
    public string Output { get; set; } = "";
    public double Duration { get; set; }
    public int SampleRate { get; set; }
    public double SynthSeconds { get; set; }
    public List<string> Phonemes { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<TokenTiming> Tokens { get; set; } = [];
    public double F0Rate { get; set; } = 80;
    public List<double?> F0Predicted { get; set; } = [];
    public List<double?> F0Final { get; set; } = [];
    public List<double> Envelope { get; set; } = [];
    public BaselineResult? Baseline { get; set; }
}

public sealed class BaselineResult
{
    public string Output { get; set; } = "";
    public double Duration { get; set; }
}

/// <summary>One G2P token: its phonemes, where they came from, when it is spoken and where it is in the text.</summary>
public sealed class TokenTiming
{
    public string Text { get; set; } = "";
    public string Phonemes { get; set; } = "";

    /// <summary>markup (region), lexicon, gold / silver (dictionary), derived, guessed (espeak-ng fallback), punct.</summary>
    public string Source { get; set; } = "";
    public double? Start { get; set; }
    public double? End { get; set; }
    public string Controls { get; set; } = "";
    public int? CharStart { get; set; }
    public int? CharEnd { get; set; }

    public bool IsGuessed => Source == "guessed";
}

public sealed class PhonemizeResult
{
    public string Phonemes { get; set; } = "";
    public List<TokenTiming> Tokens { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class EngineInfo
{
    public int Protocol { get; set; }
    public List<string> Voices { get; set; } = [];
    public int SampleRate { get; set; }
    public List<string> Formats { get; set; } = [];
}

public sealed class EngineException(string message) : Exception(message);
