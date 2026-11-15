using System.Windows.Media;
using TtsStudio.Core;
using TtsStudio.Core.Model;

namespace TtsStudio.App.Controls;

/// <summary>Colour per region kind, used by the designer, the prosody view and the lists.</summary>
public static class RegionColors
{
    private static readonly Dictionary<RegionKind, Color> Colors = new()
    {
        [RegionKind.Pronunciation] = Color.FromRgb(0x3F, 0xC8, 0xC9),
        [RegionKind.Inflection] = Color.FromRgb(0xF5, 0xA6, 0x4C),
        [RegionKind.Timing] = Color.FromRgb(0x6C, 0xCB, 0x7A),
        [RegionKind.Loudness] = Color.FromRgb(0xE8, 0x72, 0xAE),
        [RegionKind.Pause] = Color.FromRgb(0x9A, 0xA3, 0xB8),
        [RegionKind.Mixed] = Color.FromRgb(0xA9, 0x8A, 0xFF),
        [RegionKind.Empty] = Color.FromRgb(0x7C, 0x85, 0x97),
    };

    private static readonly Dictionary<(RegionKind, byte), SolidColorBrush> Brushes = [];

    public static Color For(RegionKind kind) => Colors[kind];

    public static Color For(Region r) => Colors[RegionTools.Kind(r)];

    public static SolidColorBrush Brush(RegionKind kind, byte alpha = 255)
    {
        if (!Brushes.TryGetValue((kind, alpha), out var b))
        {
            var c = Colors[kind];
            b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            b.Freeze();
            Brushes[(kind, alpha)] = b;
        }
        return b;
    }

    public static SolidColorBrush Brush(Region r, byte alpha = 255) => Brush(RegionTools.Kind(r), alpha);
}
