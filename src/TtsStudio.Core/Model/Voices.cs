namespace TtsStudio.Core.Model;

public sealed record VoiceInfo(string Id, string Name, string Accent, string Gender)
{
    public string Display => $"{Name} ({Accent} {Gender.ToLowerInvariant()})";
}

/// <summary>Kokoro's English voices (the annotations rely on misaki's English G2P).</summary>
public static class Voices
{
    public const string Default = "af_heart";

    public static IReadOnlyList<string> Ids { get; } =
    [
        "af_heart", "af_bella", "af_nicole", "af_aoede", "af_kore", "af_sarah", "af_nova",
        "af_sky", "af_alloy", "af_jessica", "af_river",
        "am_michael", "am_fenrir", "am_puck", "am_echo", "am_eric", "am_liam", "am_onyx",
        "am_adam", "am_santa",
        "bf_emma", "bf_isabella", "bf_alice", "bf_lily",
        "bm_george", "bm_fable", "bm_lewis", "bm_daniel",
    ];

    public static IReadOnlyList<VoiceInfo> All { get; } = Ids.Select(Describe).ToList();

    public static bool IsKnown(string? id) => id is not null && Ids.Contains(id);

    public static VoiceInfo Describe(string id)
    {
        string accent = id.StartsWith('b') ? "British" : "American";
        string gender = id.Length > 1 && id[1] == 'm' ? "Male" : "Female";
        string name = id.Length > 3 ? char.ToUpperInvariant(id[3]) + id[4..] : id;
        return new VoiceInfo(id, name, accent, gender);
    }
}
