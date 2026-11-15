using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TtsStudio.App.Services;

/// <summary>Asks DWM for a light or dark title bar whose colour matches the theme (Windows 10 20H1+ / 11).</summary>
public static partial class TitleBar
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Styles the window's title bar now (or once it has a handle). ThemeManager calls this again on a theme change.</summary>
    public static void Apply(Window window)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Set(window);
        else window.SourceInitialized += (_, _) => Set(window);
    }

    private static void Set(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = ThemeManager.IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        if (window.TryFindResource("PanelBrush") is SolidColorBrush { Color: var c })
        {
            int caption = c.R | c.G << 8 | c.B << 16; // COLORREF is 0x00BBGGRR
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
        }
    }
}
