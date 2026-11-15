using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TtsStudio.App.Services;

/// <summary>Small per-user settings file (%APPDATA%\TtsStudio\settings.json).</summary>
public sealed class AppSettings
{
    public string? LastProject { get; set; }
    public bool AutoRender { get; set; } = true;
    public bool SnapToWords { get; set; } = true;
    public WindowBounds? Window { get; set; }

    /// <summary>Height of the pitch and timing view, set by the splitter above it.</summary>
    public double? PitchViewHeight { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>The folder holding the settings file.</summary>
    public static string FolderPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TtsStudio");

    private static string FilePath => Path.Combine(FolderPath, "settings.json");

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
