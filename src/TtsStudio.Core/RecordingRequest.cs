using System.Text.Json;

namespace TtsStudio.Core;

/// <summary>
/// Hand-off between the MCP server and the app's recorder (TtsStudio.exe --record &lt;request.json&gt;):
/// the request says what to show and where to save; the app writes the outcome to <see cref="ResultPath"/>.
/// </summary>
public sealed class RecordingRequest
{
    public const string Switch = "--record";

    /// <summary>The text to read aloud.</summary>
    public string Text { get; set; } = "";

    /// <summary>Shown above the text, e.g. the sample name.</summary>
    public string? Title { get; set; }

    /// <summary>Where to save the WAV.</summary>
    public string Output { get; set; } = "";

    public static string ResultPath(string requestPath) => requestPath + ".result.json";

    public static RecordingRequest Load(string path) =>
        JsonSerializer.Deserialize<RecordingRequest>(File.ReadAllText(path), ProjectFile.JsonOptions)
        ?? throw new InvalidDataException("Empty recording request.");

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, ProjectFile.JsonOptions));
}

/// <summary>What happened in the recorder.</summary>
public sealed class RecordingResult
{
    /// <summary>saved, cancelled or error.</summary>
    public string Outcome { get; set; } = "cancelled";

    public string? Output { get; set; }
    public double? Seconds { get; set; }
    public string? Error { get; set; }

    public static RecordingResult? Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<RecordingResult>(File.ReadAllText(path), ProjectFile.JsonOptions) : null;

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, ProjectFile.JsonOptions));
}
