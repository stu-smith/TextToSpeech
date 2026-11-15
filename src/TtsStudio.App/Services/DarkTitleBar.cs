using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TtsStudio.App.Services;

/// <summary>Asks DWM for a dark title bar whose colour matches the theme (Windows 10 20H1+ / 11).</summary>
public static partial class DarkTitleBar
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        void Set()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
            int caption = 0x00221E1D; // COLORREF (0x00BBGGRR) of the panel colour #1D1E22
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
        }
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Set();
        else window.SourceInitialized += (_, _) => Set();
    }
}
