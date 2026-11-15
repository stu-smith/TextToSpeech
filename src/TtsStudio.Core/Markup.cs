using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TtsStudio.Core.Model;

namespace TtsStudio.Core;

public sealed class MarkupException(string message) : Exception(message);

/// <summary>
/// Converts the compact annotation markup from the original Kokoro workbench into plain text
/// plus regions, so text can be typed or generated quickly and then refined in the designer:
/// <code>
///   [word](/IPA/)            pronunciation        [word](+1) / [word](-1)   stress
///   *word*                   emphasis             {some words | p=+3 r=0.8} prosody
///   {pause 400} / {pause 1.2s}  pause              \* \{ \} \|               literals
/// </code>
/// Prosody keys: p pitch (semitones), r rate, c contour (rise, fall, rise:6), g pitch range,
/// v volume (dB), e energy.
/// </summary>
public static partial class Markup
{
    public const double EmphasisPitch = 2.5, EmphasisRate = 0.85, EmphasisVolume = 1.5;

    public const string Reference = """
        [word](/IPA/)              Pronunciation override, e.g. Generate an [IOSS](/ˈIɑs/) return
        [word](+1) / [word](-1)    Raise / lower lexical stress (+2 / -2 force / remove)
        *word*                     Emphasis: pitch +2.5, rate 0.85, volume +1.5 dB
        {some words | p=+3 r=0.8}  Prosody on the enclosed words (spans can nest)
        {pause 400} / {pause 1.2s} Silence (ms by default)
        \* \{ \} \|                Literal characters

        Prosody keys: p pitch (semitones), r rate (>1 faster), c contour (rise, fall, rise:6),
        g pitch range (0 monotone .. 2 animated), v volume (dB), e energy (experimental, -3..3)
        """;

    [GeneratedRegex(@"\G\[([^\]]+)\]\(([^\)]*)\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"\G\{\s*pause\s+([0-9]*\.?[0-9]+)\s*(ms|s)?\s*\}", RegexOptions.IgnoreCase)]
    private static partial Regex PauseRegex();

    public static (string Text, List<Region> Regions) Parse(string markup)
    {
        var p = new Parser(markup.Replace("\r\n", "\n").Trim());
        p.Run("", false);
        p.AttachPauses();
        return (p.Out.ToString(), p.Regions.OrderBy(r => r.Start).ThenByDescending(r => r.End).ToList());
    }

    /// <summary>Applies "p=+3 r=0.8 c=rise:6" style controls to a region.</summary>
    public static void ApplyControls(Region r, string spec)
    {
        foreach (var item in Regex.Split(spec.Trim(), @"[\s,]+"))
        {
            if (item.Length == 0) continue;
            int eq = item.IndexOf('=');
            if (eq < 0) throw new MarkupException($"Control '{item}' should look like key=value (e.g. p=+2)");
            string key = item[..eq].Trim().ToLowerInvariant(), value = item[(eq + 1)..].Trim();
            switch (key)
            {
                case "p" or "pitch": r.Pitch = Num(key, value); break;
                case "r" or "rate":
                    r.Rate = Num(key, value);
                    if (r.Rate <= 0) throw new MarkupException("Rate must be greater than 0");
                    break;
                case "g" or "range": r.PitchRange = Num(key, value); break;
                case "v" or "vol" or "volume": r.Volume = Num(key, value); break;
                case "e" or "energy": r.Energy = Num(key, value); break;
                case "c" or "contour":
                    var parts = value.Split(':', 2);
                    string shape = parts[0].ToLowerInvariant();
                    if (shape is not ("rise" or "fall")) throw new MarkupException($"Contour must be rise or fall, got '{shape}'");
                    r.Contour = shape;
                    if (parts.Length == 2) r.ContourSemitones = Num(key, parts[1]);
                    break;
                default: throw new MarkupException($"Unknown control '{key}'. Use p, r, c, g, v or e.");
            }
        }
    }

    private static double Num(string key, string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            ? d : throw new MarkupException($"Bad value for {key}: '{value}'");

    private sealed class Parser(string text)
    {
        private int _i;
        public StringBuilder Out { get; } = new();
        public List<Region> Regions { get; } = [];
        private readonly List<(int Offset, double Ms)> _pauses = [];

        private MarkupException Error(string msg) => new($"{msg} (at character {_i + 1})");

        private void Emit(string s)
        {
            foreach (char c in s)
            {
                // Collapse runs of spaces (e.g. left behind by a removed {pause}).
                if (c is ' ' or '\t' && (Out.Length == 0 || Out[^1] is ' ' or '\n')) continue;
                Out.Append(c == '\t' ? ' ' : c);
            }
        }

        public void Run(string stop, bool inEmphasis)
        {
            while (_i < text.Length)
            {
                char ch = text[_i];
                if (stop.Contains(ch)) return;
                Match m;
                if (ch == '\\' && _i + 1 < text.Length)
                {
                    Emit(text[_i + 1].ToString());
                    _i += 2;
                }
                else if (ch == '[' && (m = LinkRegex().Match(text, _i)).Success)
                {
                    int start = Out.Length;
                    Emit(m.Groups[1].Value);
                    var r = new Region { Start = start, End = Out.Length };
                    string f = m.Groups[2].Value.Trim();
                    if (f.Length > 1 && f[0] == '/') r.Pronunciation = f.Trim('/');
                    else if (double.TryParse(f, NumberStyles.Float, CultureInfo.InvariantCulture, out double stress)) r.Stress = stress;
                    else throw Error($"Link target '{f}' should be /phonemes/ or a stress like +1");
                    Regions.Add(r);
                    _i += m.Length;
                }
                else if (ch == '{' && (m = PauseRegex().Match(text, _i)).Success)
                {
                    double amount = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    bool seconds = m.Groups[2].Value.Equals("s", StringComparison.OrdinalIgnoreCase);
                    _pauses.Add((Out.Length, seconds ? amount * 1000 : amount));
                    _i += m.Length;
                }
                else if (ch == '{') Span();
                else if (ch == '*')
                {
                    if (inEmphasis) return;
                    Emphasis();
                }
                else if (ch is '}' or '|') throw Error($"Unexpected '{ch}' (use \\{ch} for a literal)");
                else
                {
                    Emit(ch.ToString());
                    _i++;
                }
            }
        }

        private void Span()
        {
            int openAt = _i++;
            while (_i < text.Length && char.IsWhiteSpace(text[_i])) _i++;
            int start = Out.Length;
            Run("|}", false);
            // "{word | p=2}": the space before '|' is layout, not text.
            if (Out.Length > start && Out[^1] == ' ') Out.Length--;
            if (_i >= text.Length || text[_i] != '|')
            {
                _i = openAt;
                throw Error("Span '{' needs controls, e.g. {words | p=+2}");
            }
            int close = text.IndexOf('}', _i);
            if (close < 0)
            {
                _i = openAt;
                throw Error("Unclosed '{'");
            }
            var r = new Region { Start = start, End = Out.Length };
            ApplyControls(r, text[(_i + 1)..close]);
            _i = close + 1;
            if (r.End > r.Start) Regions.Add(r);
        }

        private void Emphasis()
        {
            int openAt = _i++;
            int start = Out.Length;
            Run("", true);
            if (_i >= text.Length)
            {
                _i = openAt;
                throw Error("Unclosed '*'");
            }
            _i++;
            if (Out.Length > start)
                Regions.Add(new Region
                {
                    Start = start, End = Out.Length, Label = "emphasis",
                    Pitch = EmphasisPitch, Rate = EmphasisRate, Volume = EmphasisVolume,
                });
        }

        /// <summary>Pauses become "pause after" on the preceding word (or "pause before" on the first word).</summary>
        public void AttachPauses()
        {
            string s = Out.ToString();
            foreach (var (offset, ms) in _pauses)
            {
                // The whole space-delimited chunk before the pause, punctuation included, so the
                // silence still lands after e.g. the "..." in "Well... {pause 400}".
                int end = Math.Min(offset, s.Length);
                while (end > 0 && char.IsWhiteSpace(s[end - 1])) end--;
                if (end > 0)
                {
                    int start = end;
                    while (start > 0 && !char.IsWhiteSpace(s[start - 1])) start--;
                    Regions.Add(new Region { Start = start, End = end, PauseAfterMs = ms });
                    continue;
                }
                int a = offset;
                while (a < s.Length && !char.IsLetterOrDigit(s[a])) a++;
                int b = a;
                while (b < s.Length && IsWordChar(s[b])) b++;
                if (b > a) Regions.Add(new Region { Start = a, End = b, PauseBeforeMs = ms });
            }
        }
    }

    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '\'' or '’' or '-';
}
