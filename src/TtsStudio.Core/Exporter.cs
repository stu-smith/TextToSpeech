using TtsStudio.Core.Engine;
using TtsStudio.Core.Model;

namespace TtsStudio.Core;

public static class Exporter
{
    public static IReadOnlyList<string> Formats { get; } = ["wav", "flac", "ogg", "mp3"];

    public static string NormalizeFormat(string? format)
    {
        string f = (format ?? "wav").Trim().TrimStart('.').ToLowerInvariant();
        return Formats.Contains(f) ? f : throw new ArgumentException($"Unsupported format '{format}'. Use wav, flac, ogg or mp3.");
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return s.Length == 0 ? "sample" : s;
    }

    /// <summary>Export folder for a project (relative folders resolve against the project file).</summary>
    public static string ExportFolder(Project project, string? projectPath)
    {
        string folder = string.IsNullOrWhiteSpace(project.ExportFolder) ? "audio" : project.ExportFolder;
        if (Path.IsPathRooted(folder)) return folder;
        string baseDir = projectPath is null ? Environment.CurrentDirectory : Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        return Path.GetFullPath(Path.Combine(baseDir, folder));
    }

    /// <summary>Output paths for every sample, made unique when two samples share a name.</summary>
    public static Dictionary<Sample, string> PlanExport(Project project, string? projectPath, string? format = null)
    {
        string ext = NormalizeFormat(format ?? project.ExportFormat);
        string folder = ExportFolder(project, projectPath);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new Dictionary<Sample, string>();
        foreach (var s in project.Samples)
        {
            string stem = SafeFileName(s.ExportName), name = stem;
            for (int n = 2; !used.Add(name); n++) name = $"{stem} ({n})";
            plan[s] = Path.Combine(folder, $"{name}.{ext}");
        }
        return plan;
    }

    public static async Task<List<(Sample Sample, string Path, SynthesisResult Result)>> ExportAllAsync(
        EngineClient engine, Project project, string? projectPath, string? format = null,
        IProgress<(int Done, int Total, Sample Sample)>? progress = null, CancellationToken ct = default)
    {
        var results = new List<(Sample, string, SynthesisResult)>();
        var plan = PlanExport(project, projectPath, format).Where(kv => !string.IsNullOrWhiteSpace(kv.Key.Text)).ToList();
        for (int i = 0; i < plan.Count; i++)
        {
            var (sample, path) = (plan[i].Key, plan[i].Value);
            progress?.Report((i, plan.Count, sample));
            var result = await engine.SynthesizeAsync(SynthesisRequest.For(project, sample, path), ct);
            results.Add((sample, path, result));
        }
        return results;
    }
}
