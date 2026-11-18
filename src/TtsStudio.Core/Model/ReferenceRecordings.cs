namespace TtsStudio.Core.Model;

/// <summary>
/// Where a sample's reference recording lives. Paths are stored relative to the project file's
/// folder when they can be, so a project and its recordings can be moved together.
/// </summary>
public static class ReferenceRecordings
{
    /// <summary>Formats the speech engine reads directly. Others (e.g. m4a) need converting first.</summary>
    public static IReadOnlyList<string> Formats { get; } = ["wav", "flac", "ogg", "mp3"];

    public static bool IsSupported(string path) =>
        Formats.Contains(Path.GetExtension(path).TrimStart('.').ToLowerInvariant());

    /// <summary>The recording's full path, or null if the sample has none.</summary>
    public static string? Resolve(string? reference, string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        if (Path.IsPathRooted(reference)) return Path.GetFullPath(reference);
        string baseDir = projectPath is null ? Environment.CurrentDirectory : Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        return Path.GetFullPath(Path.Combine(baseDir, reference));
    }

    /// <summary>How to store a recording's path in the project: relative to the project when it is on the same drive.</summary>
    public static string Store(string fullPath, string? projectPath)
    {
        fullPath = Path.GetFullPath(fullPath);
        if (projectPath is null) return fullPath;
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        if (!string.Equals(Path.GetPathRoot(baseDir), Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase)) return fullPath;
        return Path.GetRelativePath(baseDir, fullPath);
    }

    /// <summary>Re-expresses every sample's recording path for a project saved to a new location.</summary>
    public static void Rebase(Project project, string? oldProjectPath, string newProjectPath)
    {
        foreach (var s in project.Samples)
            if (Resolve(s.ReferenceAudio, oldProjectPath) is { } full)
                s.ReferenceAudio = Store(full, newProjectPath);
    }
}
