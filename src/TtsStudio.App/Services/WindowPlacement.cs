using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TtsStudio.App.Services;

/// <summary>A window's restored bounds (screen pixels) and whether it was maximized.</summary>
public sealed record WindowBounds(int Left, int Top, int Width, int Height, bool Maximized);

/// <summary>
/// Saves and restores a window's position, size and maximized state with the Win32 placement
/// API, which records the restored bounds even while maximized and moves a window back on screen
/// if its monitor has gone.
/// </summary>
public static partial class WindowPlacement
{
    private const int SwShowNormal = 1, SwShowMaximized = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Placement
    {
        public int Length, Flags, ShowCmd;
        public int MinX, MinY, MaxX, MaxY;
        public Rect Normal;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowPlacement(IntPtr hwnd, ref Placement placement);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPlacement(IntPtr hwnd, ref Placement placement);

    public static WindowBounds? Get(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var p = new Placement { Length = Marshal.SizeOf<Placement>() };
        if (hwnd == IntPtr.Zero || !GetWindowPlacement(hwnd, ref p)) return null;
        var r = p.Normal;
        return new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, p.ShowCmd == SwShowMaximized);
    }

    /// <summary>Applies saved bounds; call from SourceInitialized, before the window is shown.</summary>
    public static void Set(Window window, WindowBounds bounds)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || bounds.Width < 200 || bounds.Height < 150) return;
        var p = new Placement
        {
            Length = Marshal.SizeOf<Placement>(),
            ShowCmd = SwShowNormal,
            Normal = new Rect { Left = bounds.Left, Top = bounds.Top, Right = bounds.Left + bounds.Width, Bottom = bounds.Top + bounds.Height },
        };
        // Twice: if the window lands on a monitor with a different DPI, the first move rescales it.
        SetWindowPlacement(hwnd, ref p);
        SetWindowPlacement(hwnd, ref p);
        if (bounds.Maximized) window.WindowState = WindowState.Maximized;
    }
}
