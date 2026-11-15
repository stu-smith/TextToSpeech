using System.Globalization;
using TtsStudio.Core.Model;

namespace TtsStudio.Core;

public enum RegionKind { Pronunciation, Inflection, Timing, Loudness, Pause, Mixed, Empty }

/// <summary>Human-readable descriptions of regions, shared by the designer and the MCP server.</summary>
public static class RegionTools
{
    public static RegionKind Kind(Region r)
    {
        var kinds = new List<RegionKind>();
        if (!string.IsNullOrWhiteSpace(r.Pronunciation) || r.Stress is not null) kinds.Add(RegionKind.Pronunciation);
        if (r.Pitch is not null || r.Contour is not null || r.PitchRange is not null) kinds.Add(RegionKind.Inflection);
        if (r.Rate is not null) kinds.Add(RegionKind.Timing);
        if (r.Volume is not null || r.Energy is not null) kinds.Add(RegionKind.Loudness);
        if (r.PauseBeforeMs is not null || r.PauseAfterMs is not null) kinds.Add(RegionKind.Pause);
        return kinds.Count switch { 0 => RegionKind.Empty, 1 => kinds[0], _ => RegionKind.Mixed };
    }

    /// <summary>Short summary such as "/ˈIɑs/ · pitch +2 · rise 6".</summary>
    public static string Summary(Region r)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.Pronunciation)) parts.Add($"/{r.Pronunciation.Trim('/')}/");
        else if (r.Stress is double st) parts.Add($"stress {Signed(st)}");
        if (r.Pitch is double p) parts.Add($"pitch {Signed(p)}");
        if (r.Contour is { } c) parts.Add($"{c} {F(r.ContourSemitones ?? 4)}");
        if (r.PitchRange is double g) parts.Add($"range {F(g)}");
        if (r.Rate is double rate) parts.Add($"rate {F(rate)}");
        if (r.Volume is double v) parts.Add($"vol {Signed(v)}dB");
        if (r.Energy is double e) parts.Add($"energy {Signed(e)}");
        if (r.PauseBeforeMs is double pb) parts.Add($"pause {F(pb)}ms before");
        if (r.PauseAfterMs is double pa) parts.Add($"pause {F(pa)}ms after");
        return parts.Count == 0 ? "no properties" : string.Join(" · ", parts);
    }

    public static string Title(Region r) => string.IsNullOrWhiteSpace(r.Label) ? Summary(r) : $"{r.Label}: {Summary(r)}";

    private static string F(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Signed(double d) => (d > 0 ? "+" : "") + F(d);

    /// <summary>
    /// Assigns each region a display lane so overlapping regions sit on separate rows
    /// (longest-first within a start position, first free lane wins).
    /// </summary>
    public static Dictionary<Region, int> AssignLanes(IEnumerable<Region> regions)
    {
        var lanes = new Dictionary<Region, int>();
        var laneEnds = new List<int>();
        foreach (var r in regions.OrderBy(r => r.Start).ThenByDescending(r => r.End).ThenBy(r => r.Id, StringComparer.Ordinal))
        {
            int lane = laneEnds.FindIndex(end => end <= r.Start);
            if (lane < 0)
            {
                lane = laneEnds.Count;
                laneEnds.Add(0);
            }
            laneEnds[lane] = r.End;
            lanes[r] = lane;
        }
        return lanes;
    }

    /// <summary>Expands a range to whole words (letters, digits, apostrophes, hyphens).</summary>
    public static (int Start, int End) SnapToWords(string text, int start, int end)
    {
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        while (start > 0 && Markup.IsWordChar(text[start - 1]) && (start == text.Length || Markup.IsWordChar(text[start]))) start--;
        while (end < text.Length && Markup.IsWordChar(text[end]) && end > 0 && Markup.IsWordChar(text[end - 1])) end++;
        return (start, end);
    }
}
