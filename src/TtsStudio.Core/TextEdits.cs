using TtsStudio.Core.Model;

namespace TtsStudio.Core;

/// <summary>Keeps region offsets attached to the same words when the text is edited.</summary>
public static class TextEdits
{
    /// <summary>
    /// Works out the edits that turn <paramref name="oldText"/> into <paramref name="newText"/>
    /// (a word-level diff, so several separate changes each keep the regions between them intact)
    /// and applies them to the regions. Returns regions whose text was entirely deleted; those are
    /// removed from the collection.
    /// </summary>
    public static IReadOnlyList<Region> Apply(string oldText, string newText, IList<Region> regions)
    {
        var gone = new List<Region>();
        // Apply from the end so earlier offsets stay valid.
        foreach (var (pos, removed, inserted) in Enumerable.Reverse(Hunks(oldText, newText)))
            gone.AddRange(Apply(pos, removed, inserted.Length, regions, oldText, inserted));
        return gone;
    }

    /// <summary>Edits as (position in old text, chars removed, text inserted), in order.</summary>
    public static List<(int Pos, int Removed, string Inserted)> Hunks(string oldText, string newText)
    {
        var (pos, removed, inserted) = Diff(oldText, newText);
        if (removed == 0 && inserted == 0) return [];
        var a = Tokenize(oldText.Substring(pos, removed));
        var b = Tokenize(newText.Substring(pos, inserted));
        if (a.Count == 0 || b.Count == 0 || (long)a.Count * b.Count > 4_000_000)
            return [(pos, removed, newText.Substring(pos, inserted))];

        // Longest common subsequence of tokens, then walk it to emit the differing stretches.
        var lcs = new int[a.Count + 1, b.Count + 1];
        for (int i = a.Count - 1; i >= 0; i--)
            for (int j = b.Count - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var hunks = new List<(int, int, string)>();
        int x = 0, y = 0, oldPos = pos, hunkPos = -1, hunkRemoved = 0;
        var hunkText = new System.Text.StringBuilder();
        void Flush()
        {
            if (hunkPos >= 0) hunks.Add((hunkPos, hunkRemoved, hunkText.ToString()));
            hunkPos = -1; hunkRemoved = 0; hunkText.Clear();
        }
        while (x < a.Count || y < b.Count)
        {
            if (x < a.Count && y < b.Count && a[x] == b[y])
            {
                Flush();
                oldPos += a[x].Length;
                x++; y++;
            }
            else
            {
                if (hunkPos < 0) hunkPos = oldPos;
                if (y < b.Count && (x == a.Count || lcs[x, y + 1] >= lcs[x + 1, y]))
                    hunkText.Append(b[y++]);
                else
                {
                    hunkRemoved += a[x].Length;
                    oldPos += a[x++].Length;
                }
            }
        }
        Flush();
        return hunks;
    }

    private static List<string> Tokenize(string s)
    {
        var tokens = new List<string>();
        for (int i = 0; i < s.Length;)
        {
            int j = i + 1;
            if (Markup.IsWordChar(s[i])) while (j < s.Length && Markup.IsWordChar(s[j])) j++;
            else if (char.IsWhiteSpace(s[i])) while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
            tokens.Add(s[i..j]);
            i = j;
        }
        return tokens;
    }

    public static (int Pos, int Removed, int Inserted) Diff(string oldText, string newText)
    {
        int prefix = 0, max = Math.Min(oldText.Length, newText.Length);
        while (prefix < max && oldText[prefix] == newText[prefix]) prefix++;
        int suffix = 0;
        while (suffix < max - prefix && oldText[^(suffix + 1)] == newText[^(suffix + 1)]) suffix++;
        return (prefix, oldText.Length - prefix - suffix, newText.Length - prefix - suffix);
    }

    /// <summary>
    /// Applies "replace <paramref name="removed"/> chars at <paramref name="pos"/> with <paramref name="inserted"/> chars".
    /// When the texts are given, letters typed against a region's edge that continue its word
    /// (e.g. "DEF" -> "DEFINE") extend the region; anything else typed at an edge stays outside.
    /// </summary>
    public static IReadOnlyList<Region> Apply(int pos, int removed, int inserted, IList<Region> regions,
        string? oldText = null, string? insertedText = null)
    {
        if (removed == 0 && inserted == 0) return [];
        int delta = inserted - removed, removedEnd = pos + removed;
        bool pureInsert = removed == 0 && !string.IsNullOrEmpty(insertedText) && oldText is not null;
        bool continuesLeftWord = pureInsert && pos > 0 && Markup.IsWordChar(oldText![pos - 1]) && Markup.IsWordChar(insertedText![0]);
        bool continuesRightWord = pureInsert && pos < oldText!.Length && Markup.IsWordChar(oldText[pos]) && Markup.IsWordChar(insertedText![^1]);
        var gone = new List<Region>();
        foreach (var r in regions.ToList())
        {
            int start = MapStart(r.Start), end = MapEnd(r.End);
            if (end <= start)
            {
                regions.Remove(r);
                gone.Add(r);
                continue;
            }
            r.Start = start;
            r.End = end;
        }
        return gone;

        // Text typed exactly at a region's edge stays outside it unless it continues the edge word;
        // text replacing a selection that starts at the region's start joins it.
        int MapStart(int p) =>
            p < pos || (p == pos && (removed > 0 || continuesRightWord)) ? p
            : p >= removedEnd ? p + delta
            : pos + inserted;

        int MapEnd(int p) =>
            p == pos && continuesLeftWord ? p + inserted
            : p <= pos ? p
            : p >= removedEnd ? p + delta
            : pos;
    }
}
