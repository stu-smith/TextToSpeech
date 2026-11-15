using System.IO;
using System.Text.Json;

namespace TtsStudio.App.Services;

/// <summary>Small per-user settings file (%APPDATA%\TtsStudio\settings.json).</summary>
public sealed class AppSettings
{
    public string? LastProject { get; set; }
    public bool AutoRender { get; set; } = true;
    public bool SnapToWords { get; set; } = true;
    public double? Width { get; set; }
    public double? Height { get; set; }
    public bool Maximized { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TtsStudio", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
