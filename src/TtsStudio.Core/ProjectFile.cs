using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using TtsStudio.Core.Model;

namespace TtsStudio.Core;

/// <summary>Reads and writes .ttsproj files (indented JSON, IPA kept readable).</summary>
public static class ProjectFile
{
    public const string Extension = ".ttsproj";

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Project Load(string path) => Parse(File.ReadAllText(path));

    public static Project Parse(string json)
    {
        var project = JsonSerializer.Deserialize<Project>(json, JsonOptions)
                      ?? throw new InvalidDataException("The project file is empty.");
        if (project.Format != Project.FormatName)
            throw new InvalidDataException($"Not a TTS Studio project (format '{project.Format}').");
        if (project.Version > Project.CurrentVersion)
            throw new InvalidDataException($"The project was saved by a newer version of TTS Studio (format version {project.Version}).");
        Normalize(project);
        return project;
    }

    public static string Serialize(Project project) => JsonSerializer.Serialize(project, JsonOptions);

    /// <summary>Writes via a temporary file so readers (the app, the MCP server) never see a half-written project.</summary>
    public static void Save(Project project, string path)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string tmp = full + ".tmp";
        File.WriteAllText(tmp, Serialize(project) + Environment.NewLine);
        File.Move(tmp, full, overwrite: true);
    }

    /// <summary>Repairs what a hand- or agent-edited file might get wrong: duplicate ids and out-of-range regions.</summary>
    public static void Normalize(Project project)
    {
        var sampleIds = new HashSet<string>();
        foreach (var s in project.Samples)
        {
            if (string.IsNullOrWhiteSpace(s.Id) || !sampleIds.Add(s.Id))
            {
                s.Id = Ids.New();
                sampleIds.Add(s.Id);
            }
            s.Text ??= "";
            s.Regions ??= [];
            var regionIds = new HashSet<string>();
            foreach (var r in s.Regions.ToList())
            {
                if (string.IsNullOrWhiteSpace(r.Id) || !regionIds.Add(r.Id))
                {
                    r.Id = Ids.New();
                    regionIds.Add(r.Id);
                }
                r.Start = Math.Clamp(r.Start, 0, s.Text.Length);
                r.End = Math.Clamp(r.End, r.Start, s.Text.Length);
                if (r.End <= r.Start) s.Regions.Remove(r);
            }
        }
        project.Lexicon ??= [];
    }
}
